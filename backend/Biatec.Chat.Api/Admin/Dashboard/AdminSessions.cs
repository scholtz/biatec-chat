using System.Text.Json;
using Biatec.Chat.Api.Vault;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Biatec.Chat.Api.Admin.Dashboard;

/// <summary>The people allowed into the dashboard: <c>Admin:Admins</c>, emails separated by <c>;</c>, compared case-insensitively.</summary>
public sealed class AdminAllowList
{
    private readonly HashSet<string> _emails;

    public AdminAllowList(IOptions<AdminOptions> options, ILogger<AdminAllowList> log)
    {
        _emails = new HashSet<string>(Parse(options.Value.Admins, out var ignored), StringComparer.Ordinal);
        if (ignored > 0)
            log.LogWarning("Admin:Admins: {Ignored} entries are not email addresses and were ignored.", ignored);
        if (_emails.Count > 0)
            log.LogInformation("Admin dashboard enabled for {Count} administrator(s).", _emails.Count);
    }

    /// <summary>False while no admin is configured: the dashboard does not exist (404).</summary>
    public bool Enabled => _emails.Count > 0;

    public bool Contains(string? email) => email is not null && _emails.Contains(Normalize(email));

    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Splits the configured list; entries that are not a plain <c>local@domain</c> address are counted in <paramref name="ignored"/>.</summary>
    public static IReadOnlyList<string> Parse(string? raw, out int ignored)
    {
        ignored = 0;
        var result = new List<string>();
        foreach (var part in (raw ?? "").Split(';', '\n', '\r'))
        {
            var e = Normalize(part);
            if (e.Length == 0) continue;
            var at = e.IndexOf('@');
            if (at <= 0 || at != e.LastIndexOf('@') || at == e.Length - 1 || e.Length > 320 || e.Any(char.IsWhiteSpace) || e.Any(char.IsControl))
            {
                ignored++;
                continue;
            }
            result.Add(e);
        }
        return result;
    }
}

/// <summary>What an admin is currently viewing as: set by the admin, read-only, and gone at <see cref="ExpiresAt"/>.</summary>
public sealed class AdminImpersonation
{
    public required string TenantId { get; init; }
    public required string Sub { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>An admin's server-side session. It holds no OIDC token: the ID token is read once at sign-in and dropped.</summary>
public sealed class AdminSession
{
    public required string Email { get; init; }
    public required string Subject { get; init; }
    public required string TenantId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>Absolute end of the session.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
    public AdminImpersonation? Impersonation { get; set; }
}

public interface IAdminSessionStore
{
    /// <summary>Stores the session under the SHA-256 of its cookie; the entry disappears after <paramref name="ttl"/> (idle timeout).</summary>
    Task PutAsync(string tokenHash, AdminSession session, TimeSpan ttl, CancellationToken ct);
    /// <summary>Rewrites a session that still exists (false when it ended meanwhile): a concurrent sign-out is never undone by an update.</summary>
    Task<bool> ReplaceAsync(string tokenHash, AdminSession session, TimeSpan ttl, CancellationToken ct);
    Task<AdminSession?> GetAsync(string tokenHash, CancellationToken ct);
    /// <summary>Restarts the idle timeout.</summary>
    Task TouchAsync(string tokenHash, TimeSpan ttl, CancellationToken ct);
    Task DeleteAsync(string tokenHash, CancellationToken ct);
}

/// <summary>Redis, sealed with the vault's key provider like the other server-side auth state (the email is personal data).</summary>
public sealed class RedisAdminSessionStore(IConnectionMultiplexer redis, IKeyProvider keys) : IAdminSessionStore
{
    private static string Key(string tokenHash) => $"biatec:admin:session:{tokenHash}";
    private static string Aad(string tokenHash) => "admin-session|" + tokenHash;

    public async Task PutAsync(string tokenHash, AdminSession session, TimeSpan ttl, CancellationToken ct)
    {
        var blob = await VaultCrypto.SealAsync(keys, Aad(tokenHash), JsonSerializer.SerializeToUtf8Bytes(session), ct);
        await redis.GetDatabase().StringSetAsync(Key(tokenHash), blob, ttl);
    }

    public async Task<bool> ReplaceAsync(string tokenHash, AdminSession session, TimeSpan ttl, CancellationToken ct)
    {
        var blob = await VaultCrypto.SealAsync(keys, Aad(tokenHash), JsonSerializer.SerializeToUtf8Bytes(session), ct);
        return await redis.GetDatabase().StringSetAsync(Key(tokenHash), blob, ttl, When.Exists);
    }

    public async Task<AdminSession?> GetAsync(string tokenHash, CancellationToken ct)
    {
        byte[]? blob = await redis.GetDatabase().StringGetAsync(Key(tokenHash));
        if (blob is null) return null;
        try { return JsonSerializer.Deserialize<AdminSession>(await VaultCrypto.OpenAsync(keys, Aad(tokenHash), blob, ct)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    public Task TouchAsync(string tokenHash, TimeSpan ttl, CancellationToken ct) =>
        redis.GetDatabase().KeyExpireAsync(Key(tokenHash), ttl);

    public Task DeleteAsync(string tokenHash, CancellationToken ct) => redis.GetDatabase().KeyDeleteAsync(Key(tokenHash));
}
