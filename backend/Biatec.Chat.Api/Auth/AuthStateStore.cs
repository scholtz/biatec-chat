using System.Text.Json;
using Biatec.Chat.Api.Vault;
using StackExchange.Redis;

namespace Biatec.Chat.Api.Auth;

/// <summary>
/// What /auth/login remembers between the redirect to Biatec OIDC and the callback. Held server side only: the
/// browser sees the opaque <c>state</c>, never the nonce or the PKCE verifier.
/// </summary>
public sealed class PendingAuth
{
    public required string SessionId { get; init; }
    public required string TenantId { get; init; }
    public required Guid PopupId { get; init; }
    public required string Purpose { get; init; }
    public required string Nonce { get; init; }
    public required string Verifier { get; init; }
    public required string Scope { get; init; }
    /// <summary>For Purpose "elevate": the single allow-listed scope being added.</summary>
    public string? Elevate { get; init; }
}

public interface IAuthStateStore
{
    Task PutAsync(string stateHash, PendingAuth pending, TimeSpan ttl, CancellationToken ct);
    Task<PendingAuth?> GetAsync(string stateHash, CancellationToken ct);
    /// <summary>Atomically removes the entry. True for exactly one caller, so a state can be used once.</summary>
    Task<bool> TakeAsync(string stateHash, CancellationToken ct);
}

/// <summary>Redis, encrypted with the vault's key provider (the PKCE verifier is a secret until the code is exchanged).</summary>
public sealed class RedisAuthStateStore(IConnectionMultiplexer redis, IKeyProvider keys) : IAuthStateStore
{
    private static string Key(string stateHash) => $"biatec:auth:state:{stateHash}";
    private static string Aad(string stateHash) => "auth-state|" + stateHash;

    public async Task PutAsync(string stateHash, PendingAuth pending, TimeSpan ttl, CancellationToken ct)
    {
        var blob = await VaultCrypto.SealAsync(keys, Aad(stateHash), JsonSerializer.SerializeToUtf8Bytes(pending), ct);
        await redis.GetDatabase().StringSetAsync(Key(stateHash), blob, ttl);
    }

    public async Task<PendingAuth?> GetAsync(string stateHash, CancellationToken ct)
    {
        byte[]? blob = await redis.GetDatabase().StringGetAsync(Key(stateHash));
        if (blob is null) return null;
        try { return JsonSerializer.Deserialize<PendingAuth>(await VaultCrypto.OpenAsync(keys, Aad(stateHash), blob, ct)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    public Task<bool> TakeAsync(string stateHash, CancellationToken ct) => redis.GetDatabase().KeyDeleteAsync(Key(stateHash));
}
