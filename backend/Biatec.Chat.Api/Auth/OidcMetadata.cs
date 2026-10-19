using System.Text.Json;
using Biatec.Chat.Api.Config;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Biatec.Chat.Api.Auth;

/// <summary>The endpoints of Biatec OIDC discovered from its metadata. Every endpoint is on the configured issuer's origin.</summary>
public sealed record OidcMetadata(string Issuer, Uri AuthorizationEndpoint, Uri TokenEndpoint, Uri JwksUri)
{
    /// <summary>RP-initiated logout. Discovery's end_session_endpoint when it is on the issuer origin, else the existing /connect/endsession.</summary>
    public Uri EndSessionEndpoint { get; init; } = new(Issuer.TrimEnd('/') + "/connect/endsession");
}

public interface IOidcMetadataProvider
{
    Task<OidcMetadata> GetAsync(CancellationToken ct);

    /// <summary>Signing keys from the JWKS. <paramref name="forceRefresh"/> bypasses the cache (rate limited) for key rotation.</summary>
    Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(bool forceRefresh, CancellationToken ct);
}

public sealed class OidcException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Discovery and JWKS, cached. The configured issuer is authoritative: discovery that names another issuer or endpoints on another origin is refused.</summary>
public sealed class OidcMetadataProvider(IHttpClientFactory http, IOptions<OidcOptions> options, TimeProvider clock) : IOidcMetadataProvider
{
    public const string HttpClientName = "oidc";
    private static readonly TimeSpan MetadataTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan JwksTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan JwksMinRefresh = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private OidcMetadata? _meta;
    private DateTimeOffset _metaAt;
    private IReadOnlyList<SecurityKey> _keys = [];
    private DateTimeOffset _keysAt = DateTimeOffset.MinValue;

    private string Issuer => options.Value.Issuer.TrimEnd('/');

    public async Task<OidcMetadata> GetAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_meta is { } m && now - _metaAt < MetadataTtl) return m;
        await _gate.WaitAsync(ct);
        try
        {
            if (_meta is { } m2 && now - _metaAt < MetadataTtl) return m2;
            _meta = await FetchMetadataAsync(ct);
            _metaAt = clock.GetUtcNow();
            return _meta;
        }
        finally { _gate.Release(); }
    }

    private async Task<OidcMetadata> FetchMetadataAsync(CancellationToken ct)
    {
        try
        {
            var json = await http.CreateClient(HttpClientName).GetStringAsync(Issuer + "/.well-known/openid-configuration", ct);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var issuer = r.GetProperty("issuer").GetString()?.TrimEnd('/');
            if (!string.Equals(issuer, Issuer, StringComparison.Ordinal)) throw new OidcException("Discovery issuer does not match the configured issuer.");
            return new OidcMetadata(Issuer,
                SameOrigin(r, "authorization_endpoint"), SameOrigin(r, "token_endpoint"), SameOrigin(r, "jwks_uri"))
            {
                EndSessionEndpoint = r.TryGetProperty("end_session_endpoint", out _)
                    ? SameOrigin(r, "end_session_endpoint")
                    : new Uri(Issuer + "/connect/endsession"),
            };
        }
        catch (Exception ex) when (ex is not OidcException and not OperationCanceledException)
        {
            throw new OidcException("OIDC discovery failed.", ex);
        }
    }

    private Uri SameOrigin(JsonElement root, string name)
    {
        var s = root.GetProperty(name).GetString();
        var issuer = new Uri(Issuer);
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.GetLeftPart(UriPartial.Authority) != issuer.GetLeftPart(UriPartial.Authority))
            throw new OidcException($"Discovery {name} is not on the issuer origin.");
        return u;
    }

    public async Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(bool forceRefresh, CancellationToken ct)
    {
        var meta = await GetAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var age = clock.GetUtcNow() - _keysAt;
            if (_keys.Count > 0 && age < JwksTtl && !(forceRefresh && age >= JwksMinRefresh)) return _keys;
            try
            {
                var json = await http.CreateClient(HttpClientName).GetStringAsync(meta.JwksUri, ct);
                _keys = new JsonWebKeySet(json).GetSigningKeys().ToList();
                _keysAt = clock.GetUtcNow();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new OidcException("Fetching the OIDC signing keys failed.", ex);
            }
            return _keys;
        }
        finally { _gate.Release(); }
    }
}
