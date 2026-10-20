using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Api.Evidence.Keys;

/// <summary>
/// Production <see cref="IEvidenceKeyStore"/>: key metadata (kid, public point, created/retired dates, which one is
/// active) lives in PostgreSQL so every replica publishes the same JWKS and retired keys survive restarts; private
/// keys come from an <see cref="IEvidenceKeyMaterial"/> (secret or KMS) and are never stored here.
/// </summary>
public sealed class DbEvidenceKeyStore(IServiceScopeFactory scopes, IEvidenceKeyMaterial material, TimeProvider? time = null) : IEvidenceKeyStore
{
    private static readonly TimeSpan ActiveKidTtl = TimeSpan.FromSeconds(30);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private (string Kid, DateTimeOffset Until)? _cached;

    /// <summary>Active kid, cached briefly: after a rotation on another replica this one may sign with the retired kid for up to the TTL, which still verifies because retired keys stay in the JWKS.</summary>
    public async Task<string> GetActiveKidAsync(CancellationToken ct)
    {
        if (_cached is { } c && c.Until > _time.GetUtcNow()) return c.Kid;
        var kid = await LoadActiveKidAsync(ct);
        _cached = (kid, _time.GetUtcNow() + ActiveKidTtl);
        return kid;
    }

    private async Task<string> LoadActiveKidAsync(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var kid = await db.EvidenceSigningKeys.AsNoTracking().Where(k => k.IsActive).Select(k => k.Kid).FirstOrDefaultAsync(ct);
            if (kid is not null) return kid;
            // First start on an empty database: provision the first key. Another replica may win the race (unique active index).
            try { return await ProvisionAsync(db, retire: null, ct); }
            catch (DbUpdateException) when (attempt < 3) { }
        }
    }

    public Task<byte[]> SignAsync(string kid, byte[] data, CancellationToken ct) => material.SignAsync(kid, data, ct);

    public async Task<IReadOnlyList<EvidencePublicKey>> GetPublicKeysAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var rows = await db.EvidenceSigningKeys.AsNoTracking().ToListAsync(ct);
        if (rows.Count == 0)
        {
            await GetActiveKidAsync(ct); // first start on an empty database provisions the first key
            rows = await db.EvidenceSigningKeys.AsNoTracking().ToListAsync(ct);
        }
        return rows.OrderBy(k => k.CreatedAt).ThenBy(k => k.Kid, StringComparer.Ordinal)
            .Select(k => new EvidencePublicKey(k.Kid, k.X, k.Y, k.CreatedAt, k.RetiredAt)).ToList();
    }

    public async Task<string> RotateAsync(CancellationToken ct) => (await RotateCoreAsync(null, ct))!;

    /// <summary>Rotates only if the active key is at least <paramref name="maxAge"/> old (or none exists); returns the new kid, or null when nothing changed. Safe to call from every replica: the unique active index lets one win.</summary>
    public async Task<string?> RotateIfOlderThanAsync(TimeSpan maxAge, CancellationToken ct)
    {
        try { return await RotateCoreAsync(maxAge, ct); }
        catch (DbUpdateException) { _cached = null; return null; } // another replica rotated first
    }

    private async Task<string?> RotateCoreAsync(TimeSpan? maxAge, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var active = await db.EvidenceSigningKeys.Where(k => k.IsActive).SingleOrDefaultAsync(ct);
        if (maxAge is { } age && active is not null && _time.GetUtcNow() - active.CreatedAt < age) return null;
        var kid = await ProvisionAsync(db, active, ct);
        await tx.CommitAsync(ct);
        _cached = null;
        return kid;
    }

    private async Task<string> ProvisionAsync(ChatDbContext db, EvidenceSigningKey? retire, CancellationToken ct)
    {
        var used = (await db.EvidenceSigningKeys.AsNoTracking().Select(k => k.Kid).ToListAsync(ct)).ToHashSet();
        var fresh = await material.CreateAsync(used, ct);
        var now = _time.GetUtcNow();
        if (retire is not null)
        {
            // Retire first and flush so the partial unique index never sees two active rows.
            retire.IsActive = false;
            retire.RetiredAt = now;
            await db.SaveChangesAsync(ct);
        }
        db.EvidenceSigningKeys.Add(new EvidenceSigningKey { Kid = fresh.Kid, X = fresh.X, Y = fresh.Y, CreatedAt = now, IsActive = true });
        await db.SaveChangesAsync(ct);
        return fresh.Kid;
    }
}
