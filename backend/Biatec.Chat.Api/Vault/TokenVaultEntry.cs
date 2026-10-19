namespace Biatec.Chat.Api.Vault;

/// <summary>
/// The tokens of one session. Plaintext only in memory; ToString redacts every token so a stray log line or
/// exception message cannot leak one.
/// </summary>
public sealed class TokenVaultEntry
{
    public required string AccessToken { get; init; }
    public required DateTimeOffset AccessExpiresAt { get; init; }
    public required string RefreshToken { get; init; }
    public string? IdToken { get; init; }
    public string[] Scopes { get; init; } = [];

    /// <summary>The session's absolute end. Rotation keeps it; the Redis TTL is this plus one hour.</summary>
    public required DateTimeOffset AbsoluteExpiresAt { get; init; }

    public override string ToString() =>
        $"TokenVaultEntry {{ AccessToken = [redacted], RefreshToken = [redacted], IdToken = [redacted], AccessExpiresAt = {AccessExpiresAt:O} }}";
}

/// <summary>Result of one refresh-token grant. A null <see cref="RefreshToken"/> means the old one stays valid.</summary>
public sealed class TokenRefreshResult
{
    public required string AccessToken { get; init; }
    public required DateTimeOffset AccessExpiresAt { get; init; }
    public string? RefreshToken { get; init; }
    public string? IdToken { get; init; }
    public string[]? Scopes { get; init; }

    public override string ToString() => "TokenRefreshResult { [redacted] }";
}

/// <summary>Performs the OAuth refresh grant against Biatec OIDC. The OAuth flows themselves are out of scope for the vault.</summary>
public interface ITokenRefresher
{
    /// <summary>The session id identifies the tenant (client credentials) the refresh token was issued to.</summary>
    Task<TokenRefreshResult> RefreshAsync(string sessionId, string refreshToken, CancellationToken ct);
}

public sealed class VaultRefreshException(string message, Exception? inner = null) : Exception(message, inner);
