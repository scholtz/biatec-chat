using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Biatec.Chat.Api.Auth;

/// <summary>The only claims the backend keeps from a validated ID token.</summary>
public sealed record IdTokenIdentity(string Subject, string? Email, bool? EmailVerified = null);

public interface IIdTokenValidator
{
    /// <summary>Validates signature (JWKS), issuer, audience, nonce and lifetime. Throws <see cref="OidcException"/> otherwise; the message never contains the token.</summary>
    Task<IdTokenIdentity> ValidateAsync(string idToken, string clientId, string expectedNonce, CancellationToken ct);
}

public sealed class IdTokenValidator(IOidcMetadataProvider metadata, TimeProvider clock) : IIdTokenValidator
{
    // Asymmetric algorithms only: "none" and HMAC (key confusion with a public key) are never accepted.
    private static readonly string[] Algorithms = ["RS256", "RS384", "RS512", "PS256", "ES256", "ES384"];
    private static readonly JsonWebTokenHandler Handler = new();

    public async Task<IdTokenIdentity> ValidateAsync(string idToken, string clientId, string expectedNonce, CancellationToken ct)
    {
        var meta = await metadata.GetAsync(ct);
        var result = await TryAsync(idToken, clientId, meta.Issuer, await metadata.GetSigningKeysAsync(false, ct));
        if (!result.IsValid) // key rotation: one rate limited refresh of the JWKS, then give up
            result = await TryAsync(idToken, clientId, meta.Issuer, await metadata.GetSigningKeysAsync(true, ct));
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt) throw new OidcException("The ID token is not valid.");

        var now = clock.GetUtcNow().UtcDateTime;
        var skew = TimeSpan.FromSeconds(60);
        if (!jwt.TryGetPayloadValue<long>("exp", out _) || jwt.ValidTo + skew <= now || (jwt.ValidFrom != DateTime.MinValue && jwt.ValidFrom - skew > now))
            throw new OidcException("The ID token is expired or not yet valid.");
        if (!jwt.TryGetPayloadValue<string>("nonce", out var nonce) || !FixedEquals(nonce, expectedNonce))
            throw new OidcException("The ID token nonce does not match.");
        if (jwt.Audiences.Count() > 1 && (!jwt.TryGetPayloadValue<string>("azp", out var azp) || azp != clientId))
            throw new OidcException("The ID token authorized party does not match.");
        if (string.IsNullOrEmpty(jwt.Subject)) throw new OidcException("The ID token has no subject.");
        jwt.TryGetPayloadValue<string>("email", out var email);
        return new IdTokenIdentity(jwt.Subject, email, EmailVerified(jwt));
    }

    /// <summary>The <c>email_verified</c> claim as a boolean (some providers send the string "true"); null when absent or not a boolean.</summary>
    private static bool? EmailVerified(JsonWebToken jwt)
    {
        if (jwt.TryGetPayloadValue<bool>("email_verified", out var b)) return b;
        if (jwt.TryGetPayloadValue<string>("email_verified", out var s) && bool.TryParse(s, out var parsed)) return parsed;
        return null;
    }

    private async Task<TokenValidationResult> TryAsync(string idToken, string clientId, string issuer, IReadOnlyList<SecurityKey> keys) =>
        await Handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = clientId,
            ValidateLifetime = false, // checked below against the injected clock
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ValidAlgorithms = Algorithms,
            ClockSkew = TimeSpan.FromSeconds(60),
        });

    private static bool FixedEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}
