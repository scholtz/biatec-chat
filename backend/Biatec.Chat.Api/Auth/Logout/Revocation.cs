using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Auth.Logout;

public sealed class RevocationOptions
{
    public const string Section = "Oidc:Revocation";

    /// <summary>
    /// The RFC 7009 revocation endpoint of Biatec OIDC (a Required Changes item, not shipped yet). Empty means the
    /// fallback: nothing is sent, refresh tokens are parked in PendingRevocation and the vault entry is destroyed, so
    /// the token ages out at Biatec OIDC. It must be on the issuer's origin or it is ignored.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>How long a parked refresh token is kept at most (Biatec OIDC issues 30-day refresh tokens today).</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);

    public int BatchSize { get; set; } = 100;
}

public interface IRevocationService
{
    /// <summary>True when a revocation URL on the issuer's origin is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Revokes the refresh token. When that is not possible (no endpoint yet, or the call failed) the token is parked
    /// encrypted in PendingRevocation and the method returns normally. Throws only when the token could not be parked
    /// either; the caller then falls back to destroying the vault entry. Never logs the token.
    /// </summary>
    Task RevokeOrParkAsync(Tenant tenant, string refreshToken, CancellationToken ct);

    /// <summary>
    /// Purges parked tokens past their expiry, then (only when a revocation URL is configured) retries the rest.
    /// Returns the number revoked.
    /// </summary>
    Task<int> RetryPendingAsync(CancellationToken ct);
}

public sealed class RevocationService(
    ChatDbContext db,
    IKeyProvider keys,
    IHttpClientFactory http,
    ISecretResolver secrets,
    IOptions<RevocationOptions> options,
    IOptions<OidcOptions> oidcOptions,
    TimeProvider clock,
    ILogger<RevocationService> log) : IRevocationService
{
    private Uri? Endpoint
    {
        get
        {
            var url = options.Value.Url;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
            if (!Uri.TryCreate(oidcOptions.Value.Issuer, UriKind.Absolute, out var issuer)) return null;
            return u.GetLeftPart(UriPartial.Authority) == issuer.GetLeftPart(UriPartial.Authority) ? u : null;
        }
    }

    public bool IsConfigured => Endpoint is not null;

    public async Task RevokeOrParkAsync(Tenant tenant, string refreshToken, CancellationToken ct)
    {
        if (await TryRevokeAsync(tenant, refreshToken, ct)) return;
        await ParkAsync(tenant.Id, refreshToken, ct);
    }

    private async Task<bool> TryRevokeAsync(Tenant tenant, string refreshToken, CancellationToken ct)
    {
        var endpoint = Endpoint;
        if (endpoint is null) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"] = refreshToken,
                    ["token_type_hint"] = "refresh_token",
                }),
            };
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{Uri.EscapeDataString(tenant.OidcClientId)}:{Uri.EscapeDataString(secrets.Resolve(tenant.ClientSecretRef))}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            using var res = await http.CreateClient(OidcMetadataProvider.HttpClientName).SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return true; // RFC 7009: 200 also for unknown tokens
            log.LogWarning("Token revocation was refused ({Status}).", (int)res.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Token revocation failed: {ErrorType}", ex.GetType().Name);
            return false;
        }
    }

    private async Task ParkAsync(string tenantId, string refreshToken, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var plain = Encoding.UTF8.GetBytes(refreshToken);
        byte[] blob;
        try { blob = await VaultCrypto.SealAsync(keys, Aad(id), plain, ct); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        db.PendingRevocations.Add(new PendingRevocation
        {
            Id = id,
            TenantId = tenantId,
            EncryptedRefreshToken = blob,
            ExpiresAt = clock.GetUtcNow() + options.Value.RefreshTokenLifetime,
            Attempts = 0,
        });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    public async Task<int> RetryPendingAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // A token past its expiry is dead at Biatec OIDC anyway: stop holding it.
        await db.PendingRevocations.Where(p => p.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        if (Endpoint is null) return 0;

        var rows = await db.PendingRevocations.AsNoTracking()
            .OrderBy(p => p.Attempts).ThenBy(p => p.ExpiresAt).Take(options.Value.BatchSize).ToListAsync(ct);
        var revoked = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == row.TenantId, ct);
            var ok = false;
            if (tenant is not null)
            {
                string? token = null;
                try
                {
                    var plain = await VaultCrypto.OpenAsync(keys, Aad(row.Id), row.EncryptedRefreshToken, ct);
                    try { token = Encoding.UTF8.GetString(plain); }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                catch (CryptographicException) { log.LogError("A parked refresh token could not be decrypted; it stays until it expires."); }
                if (token is not null) ok = await TryRevokeAsync(tenant, token, ct);
            }
            if (ok)
            {
                await db.PendingRevocations.Where(p => p.Id == row.Id).ExecuteDeleteAsync(ct);
                revoked++;
            }
            else
            {
                await db.PendingRevocations.Where(p => p.Id == row.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(p => p.Attempts, p => p.Attempts + 1), ct);
            }
        }
        return revoked;
    }

    private static string Aad(Guid id) => "pending-revocation:" + id.ToString("N");
}
