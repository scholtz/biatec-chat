using System.Security.Cryptography;
using Biatec.Chat.Api.Auth.Logout;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Sweeper;

public sealed class SweeperOptions
{
    public const string Section = "Sweeper";

    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);
    public int BatchSize { get; set; } = 200;
}

public sealed record SweepResult(int Ended, int Skipped);

/// <summary>
/// Ends sessions that are idle past their timeout (except one with an open ConsentRequest, which is exempt from the
/// idle timeout only) or past their absolute lifetime. Order per session, as the spec fixes it: read the refresh
/// token, revoke it (or park it in PendingRevocation), delete the vault entry, then delete the session. A session whose
/// vault cannot be read is left for the next run, so a token is never dropped unrevoked because Redis hiccuped.
/// </summary>
public sealed class SessionSweeper(
    ChatDbContext db,
    ITokenVault vault,
    IRevocationService revocation,
    IOptions<SweeperOptions> options,
    TimeProvider clock,
    ILogger<SessionSweeper> log)
{
    public async Task<SweepResult> SweepAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var idleCutoff = now - SessionPolicy.IdleTimeout;

        // Conversations with a consent request still waiting for the user's phone.
        var awaitingConsent =
            from c in db.ConsentRequests
            where c.Status == ConsentStatus.Pending && c.ExpiresAt > now
            join st in db.ActionSteps on c.StepId equals st.Id
            join p in db.ActionPreviews on st.PreviewId equals p.Id
            select p.ConversationId;

        // The complement of SessionService's "live" test: expired absolutely, or idle without an open consent.
        var due = await db.ChatSessions.AsNoTracking()
            .Where(s => s.AbsoluteExpiresAt <= now || (s.LastActivityAt <= idleCutoff && !awaitingConsent.Contains(s.ConversationId)))
            .OrderBy(s => s.AbsoluteExpiresAt)
            .Take(options.Value.BatchSize)
            .ToListAsync(ct);

        int ended = 0, skipped = 0;
        foreach (var s in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await EndAsync(s, ct);
                ended++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped++;
                log.LogWarning("Sweeping a session failed, retrying next run: {ErrorType}", ex.GetType().Name);
            }
        }
        return new SweepResult(ended, skipped);
    }

    private async Task EndAsync(ChatSession s, CancellationToken ct)
    {
        var vaultId = s.Id.ToString("N");
        TokenVaultEntry? entry = null;
        try { entry = await vault.GetAsync(vaultId, ct); }
        catch (CryptographicException) { log.LogError("A vault entry could not be decrypted; it is deleted without revocation."); }

        if (entry is not null)
        {
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == s.TenantId, ct);
            if (tenant is null)
            {
                // Cannot revoke or park without the tenant: keep the vault entry and the session for the next run.
                log.LogWarning("A session's tenant is missing; the session is left for the next run.");
                throw new InvalidOperationException("Tenant missing for session.");
            }
            try { await revocation.RevokeOrParkAsync(tenant, entry.RefreshToken, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not even parked: the fallback is to destroy the entry and let the token age out.
                log.LogError("A refresh token could be neither revoked nor parked: {ErrorType}", ex.GetType().Name);
            }
        }
        await vault.DeleteAsync(vaultId, ct);
        await db.ChatSessions.Where(x => x.Id == s.Id).ExecuteDeleteAsync(ct);
    }
}

/// <summary>Runs the sweeper and the pending-revocation retry every minute. A failure is logged by type and the loop goes on.</summary>
public sealed class SweeperHostedService(IServiceScopeFactory scopes, IOptions<SweeperOptions> options, TimeProvider clock, ILogger<SweeperHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(options.Value.Interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<SessionSweeper>().SweepAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<IRevocationService>().RetryPendingAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError("Sweeper run failed: {ErrorType}", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
