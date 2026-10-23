using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Biatec.Chat.Api.Vault;

public sealed class VaultOptions
{
    public const string Section = "Vault";

    /// <summary>
    /// "Dev" (default, no AWS; refused at start-up outside Development unless <see cref="AllowEphemeralKeys"/>), "Secret" (master key from <see cref="MasterKey"/>, the Kubernetes deployment) or "Kms"
    /// (AWS KMS envelope encryption using the Kms section).
    /// </summary>
    public string KeyProvider { get; set; } = "Dev";

    /// <summary>Base64 of 32 bytes for the Dev provider. Omit it and a random per-process key is used.</summary>
    public string? DevKey { get; set; }

    /// <summary>
    /// The Dev key provider keeps the vault key in this process (or in <see cref="DevKey"/>), so outside Development it is refused
    /// unless this is true: tokens would be unreadable after a restart, or protected by a key anyone with the config can read (BCHAT-012).
    /// </summary>
    public bool AllowEphemeralKeys { get; set; }

    /// <summary>Base64 of 32 bytes for the Secret provider; comes from a Kubernetes secret, never from appsettings.</summary>
    public string? MasterKey { get; set; }

    /// <summary>Grace period added to the absolute session lifetime for the Redis TTL (backstop for a sweeper outage).</summary>
    public TimeSpan TtlGrace { get; set; } = TimeSpan.FromHours(1);

    /// <summary>An access token this close to expiry is refreshed before use.</summary>
    public TimeSpan RefreshSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Redis lock lifetime. It must outlast one refresh HTTP call.</summary>
    public TimeSpan LockTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a caller waits for another pod's refresh before giving up.</summary>
    public TimeSpan WaitTimeout { get; set; } = TimeSpan.FromSeconds(20);
}

public interface ITokenVault
{
    /// <summary>Stores (or replaces) the entry, with Redis TTL = absolute lifetime + grace.</summary>
    Task PutAsync(string sessionId, TokenVaultEntry entry, CancellationToken ct = default);

    Task<TokenVaultEntry?> GetAsync(string sessionId, CancellationToken ct = default);

    Task DeleteAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Returns a usable access token, refreshing on demand. The refresh is single-flighted per session through a Redis
    /// lock, so concurrent callers (on any pod) share one refresh and a rotated refresh token is never presented twice.
    /// Returns null when the session has no entry or is past its absolute lifetime.
    /// </summary>
    Task<string?> GetAccessTokenAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the entry from the entry itself while holding the same lock the refresh uses, so a refresh and a replacement never
    /// overwrite each other. <paramref name="replace"/> gets the current entry and returns the new one, or null to leave it as it is.
    /// Returns the entry that was replaced (its refresh token is now superseded), or null when there was none or nothing was written.
    /// The default implementation is a plain read and write for in-memory stores; <see cref="TokenVault"/> locks.
    /// </summary>
    async Task<TokenVaultEntry?> ReplaceAsync(string sessionId, Func<TokenVaultEntry, TokenVaultEntry?> replace, CancellationToken ct = default)
    {
        var current = await GetAsync(sessionId, ct);
        if (current is null || replace(current) is not { } next) return null;
        await PutAsync(sessionId, next, ct);
        return current;
    }
}

/// <summary>Tokens encrypted with an envelope scheme and stored in Redis keyed by session id. Nothing here logs a token.</summary>
public sealed class TokenVault(
    IConnectionMultiplexer redis,
    IKeyProvider keys,
    ITokenRefresher refresher,
    IOptions<VaultOptions> options,
    ILogger<TokenVault> log,
    TimeProvider? clock = null) : ITokenVault
{
    private readonly VaultOptions _o = options.Value;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public static string EntryKey(string sessionId) => $"biatec:vault:{sessionId}";
    private static string LockKey(string sessionId) => $"biatec:vault:lock:{sessionId}";
    private static string FailKey(string sessionId) => $"biatec:vault:refreshfail:{sessionId}";

    private IDatabase Db => redis.GetDatabase();

    public async Task PutAsync(string sessionId, TokenVaultEntry entry, CancellationToken ct = default)
    {
        var blob = await SealAsync(sessionId, entry, ct);
        var ttl = Ttl(entry);
        if (ttl <= TimeSpan.Zero) { await Db.KeyDeleteAsync(EntryKey(sessionId)); return; }
        await Db.StringSetAsync(EntryKey(sessionId), blob, ttl);
    }

    public async Task<TokenVaultEntry?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        byte[]? blob = await Db.StringGetAsync(EntryKey(sessionId));
        if (blob is null) return null;
        var plain = await VaultCrypto.OpenAsync(keys, sessionId, blob, ct);
        try { return JsonSerializer.Deserialize<TokenVaultEntry>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public Task DeleteAsync(string sessionId, CancellationToken ct = default) =>
        Db.KeyDeleteAsync(EntryKey(sessionId));

    public async Task<string?> GetAccessTokenAsync(string sessionId, CancellationToken ct = default)
    {
        var deadline = _clock.GetUtcNow() + _o.WaitTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var entry = await GetAsync(sessionId, ct);
            if (entry is null || _clock.GetUtcNow() >= entry.AbsoluteExpiresAt) return null;
            if (IsFresh(entry)) return entry.AccessToken;

            var lockToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            if (await Db.StringSetAsync(LockKey(sessionId), lockToken, _o.LockTtl, When.NotExists))
            {
                try
                {
                    // Another pod may have refreshed between our read and taking the lock.
                    entry = await GetAsync(sessionId, ct);
                    if (entry is null || _clock.GetUtcNow() >= entry.AbsoluteExpiresAt) return null;
                    if (IsFresh(entry)) return entry.AccessToken;
                    if (await Db.KeyExistsAsync(FailKey(sessionId)))
                        throw new VaultRefreshException("A recent token refresh for this session failed.");
                    return await RefreshAsync(sessionId, entry, lockToken, ct);
                }
                finally { await ReleaseAsync(sessionId, lockToken); }
            }

            if (_clock.GetUtcNow() >= deadline)
                throw new VaultRefreshException("Timed out waiting for a token refresh in progress.");
            await Task.Delay(50, ct);
        }
    }

    public async Task<TokenVaultEntry?> ReplaceAsync(string sessionId, Func<TokenVaultEntry, TokenVaultEntry?> replace, CancellationToken ct = default)
    {
        var deadline = _clock.GetUtcNow() + _o.WaitTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var lockToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            if (await Db.StringSetAsync(LockKey(sessionId), lockToken, _o.LockTtl, When.NotExists))
            {
                try
                {
                    // Read inside the lock: a refresh that rotated the tokens before this point is what gets replaced, and none can run during it.
                    var current = await GetAsync(sessionId, ct);
                    if (current is null || replace(current) is not { } next) return null;
                    var ttl = Ttl(next);
                    if (ttl <= TimeSpan.Zero) return null;
                    var blob = await SealAsync(sessionId, next, ct);
                    var ok = (int)await Db.ScriptEvaluateAsync(WriteIfLocked,
                        [(RedisKey)LockKey(sessionId), (RedisKey)EntryKey(sessionId)],
                        [lockToken, blob, (long)Math.Max(1, ttl.TotalMilliseconds)]) == 1;
                    if (!ok) throw new VaultRefreshException("The vault lock was lost before the replacement was stored.");
                    return current;
                }
                finally { await ReleaseAsync(sessionId, lockToken); }
            }
            if (_clock.GetUtcNow() >= deadline) throw new VaultRefreshException("Timed out waiting for the vault lock.");
            await Task.Delay(50, ct);
        }
    }

    private async Task<string> RefreshAsync(string sessionId, TokenVaultEntry old, string lockToken, CancellationToken ct)
    {
        TokenRefreshResult r;
        try { r = await refresher.RefreshAsync(sessionId, old.RefreshToken, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Waiters fail fast instead of presenting a refresh token that may already be spent. No token in the message.
            await Db.StringSetAsync(FailKey(sessionId), "1", TimeSpan.FromSeconds(5));
            log.LogWarning("Token refresh failed for session {Session}: {ErrorType}", Redact.Session(sessionId), ex.GetType().Name);
            throw new VaultRefreshException("Token refresh failed.", ex);
        }

        // Rotation: the new refresh token replaces the old; the absolute expiry is inherited, never restarted.
        var updated = new TokenVaultEntry
        {
            AccessToken = r.AccessToken,
            AccessExpiresAt = r.AccessExpiresAt,
            RefreshToken = r.RefreshToken ?? old.RefreshToken,
            IdToken = r.IdToken ?? old.IdToken,
            Scopes = r.Scopes ?? old.Scopes,
            AbsoluteExpiresAt = old.AbsoluteExpiresAt,
        };
        var blob = await SealAsync(sessionId, updated, ct);
        var ttl = Ttl(updated);
        // Write only while we still hold the lock, so a lock that expired mid-refresh cannot clobber a newer entry.
        var ok = (int)await Db.ScriptEvaluateAsync(WriteIfLocked,
            [(RedisKey)LockKey(sessionId), (RedisKey)EntryKey(sessionId)],
            [lockToken, blob, (long)Math.Max(1, ttl.TotalMilliseconds)]) == 1;
        if (!ok)
        {
            log.LogError("Refresh lock for session {Session} was lost before the rotated tokens were stored.", Redact.Session(sessionId));
            throw new VaultRefreshException("Refresh lock was lost.");
        }
        log.LogDebug("Refreshed tokens for session {Session}", Redact.Session(sessionId));
        return updated.AccessToken;
    }

    private const string WriteIfLocked =
        "if redis.call('get', KEYS[1]) == ARGV[1] then redis.call('set', KEYS[2], ARGV[2], 'PX', ARGV[3]) return 1 end return 0";

    private const string Unlock =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) end return 0";

    private async Task ReleaseAsync(string sessionId, string lockToken)
    {
        try { await Db.ScriptEvaluateAsync(Unlock, [(RedisKey)LockKey(sessionId)], [lockToken]); }
        catch (Exception ex) { log.LogWarning("Could not release refresh lock for session {Session}: {ErrorType}", Redact.Session(sessionId), ex.GetType().Name); }
    }

    private bool IsFresh(TokenVaultEntry e) => e.AccessExpiresAt - _clock.GetUtcNow() > _o.RefreshSkew;

    private TimeSpan Ttl(TokenVaultEntry e) => e.AbsoluteExpiresAt + _o.TtlGrace - _clock.GetUtcNow();

    private Task<byte[]> SealAsync(string sessionId, TokenVaultEntry entry, CancellationToken ct)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(entry);
        return Seal();
        async Task<byte[]> Seal()
        {
            try { return await VaultCrypto.SealAsync(keys, sessionId, plain, ct); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }
}

/// <summary>Log-safe identifiers. A session id is a credential-adjacent value, so only a short hash is logged.</summary>
public static class Redact
{
    public static string Session(string sessionId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..8].ToLowerInvariant();
}
