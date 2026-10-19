using System.Text.Json;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Auth;

/// <summary>Resolves a tenant's "secret://..." pointer to the secret value. Values never reach code, committed config or logs.</summary>
public interface ISecretResolver
{
    string Resolve(string secretRef);
}

/// <summary>
/// Reads the secret from configuration, which in deployments is fed from the secret store or environment variables:
/// "secret://biatec-chat/oidc-client-secret" is looked up as <c>Secrets:biatec-chat:oidc-client-secret</c>
/// (environment variable <c>Secrets__biatec-chat__oidc-client-secret</c>).
/// </summary>
public sealed class ConfigurationSecretResolver(IConfiguration config) : ISecretResolver
{
    public string Resolve(string secretRef)
    {
        const string prefix = "secret://";
        var value = secretRef.StartsWith(prefix, StringComparison.Ordinal)
            ? config["Secrets:" + secretRef[prefix.Length..].Replace('/', ':')]
            : null;
        return string.IsNullOrEmpty(value) ? throw new OidcException("The tenant client secret is not configured.") : value;
    }
}

/// <summary>Result of the token endpoint. ToString is redacted.</summary>
public sealed class OidcTokenResponse
{
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public string? IdToken { get; init; }
    public required int ExpiresIn { get; init; }
    public string[]? Scopes { get; init; }
    public override string ToString() => "OidcTokenResponse { [redacted] }";
}

public interface IOidcClient
{
    Task<Uri> BuildAuthorizeUrlAsync(Tenant tenant, string scope, string state, string nonce, string codeChallenge, CancellationToken ct);
    Task<OidcTokenResponse> ExchangeCodeAsync(Tenant tenant, string code, string codeVerifier, CancellationToken ct);
    Task<OidcTokenResponse> RefreshAsync(Tenant tenant, string refreshToken, CancellationToken ct);
    /// <summary>Throws when the tenant's client secret is not configured (checked before a login starts).</summary>
    void EnsureConfigured(Tenant tenant);
}

/// <summary>The backend as a confidential client: authorization code with S256 PKCE and the MCP resource indicator.</summary>
public sealed class OidcClient(IHttpClientFactory http, IOidcMetadataProvider metadata, ISecretResolver secrets, IOptions<OidcOptions> options) : IOidcClient
{
    public void EnsureConfigured(Tenant tenant) => secrets.Resolve(tenant.ClientSecretRef);

    public async Task<Uri> BuildAuthorizeUrlAsync(Tenant tenant, string scope, string state, string nonce, string codeChallenge, CancellationToken ct)
    {
        var meta = await metadata.GetAsync(ct);
        var q = new (string, string)[]
        {
            ("response_type", "code"), ("client_id", tenant.OidcClientId), ("redirect_uri", tenant.RedirectUri),
            ("scope", scope), ("state", state), ("nonce", nonce),
            ("code_challenge", codeChallenge), ("code_challenge_method", "S256"),
            ("resource", options.Value.McpResource),
        };
        var query = string.Join('&', q.Select(p => $"{Uri.EscapeDataString(p.Item1)}={Uri.EscapeDataString(p.Item2)}"));
        return new UriBuilder(meta.AuthorizationEndpoint) { Query = query }.Uri;
    }

    public Task<OidcTokenResponse> ExchangeCodeAsync(Tenant tenant, string code, string codeVerifier, CancellationToken ct) =>
        PostAsync(tenant, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = tenant.RedirectUri,
            ["code_verifier"] = codeVerifier,
            ["resource"] = options.Value.McpResource,
        }, ct);

    public Task<OidcTokenResponse> RefreshAsync(Tenant tenant, string refreshToken, CancellationToken ct) =>
        PostAsync(tenant, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["resource"] = options.Value.McpResource,
        }, ct);

    private async Task<OidcTokenResponse> PostAsync(Tenant tenant, Dictionary<string, string> form, CancellationToken ct)
    {
        var meta = await metadata.GetAsync(ct);
        form["client_id"] = tenant.OidcClientId;
        form["client_secret"] = secrets.Resolve(tenant.ClientSecretRef);
        try
        {
            using var res = await http.CreateClient(OidcMetadataProvider.HttpClientName)
                .PostAsync(meta.TokenEndpoint, new FormUrlEncodedContent(form), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            // The body is parsed, never logged or put in an exception message: it holds tokens on success.
            if (!res.IsSuccessStatusCode) throw new OidcException($"The token endpoint refused the request ({(int)res.StatusCode}).");
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            var scope = r.TryGetProperty("scope", out var sc) ? sc.GetString() : null;
            return new OidcTokenResponse
            {
                AccessToken = r.GetProperty("access_token").GetString() ?? throw new OidcException("No access token."),
                RefreshToken = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                IdToken = r.TryGetProperty("id_token", out var it) ? it.GetString() : null,
                ExpiresIn = r.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var n) && n > 0 ? n : 300,
                Scopes = scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            };
        }
        catch (Exception ex) when (ex is not OidcException and not OperationCanceledException)
        {
            throw new OidcException("The token request failed.", ex);
        }
    }
}

/// <summary>The vault's refresh grant: finds the session's tenant (its client credentials) and calls the token endpoint.</summary>
public sealed class OidcTokenRefresher(IServiceScopeFactory scopes, IOidcClient client, TimeProvider clock) : ITokenRefresher
{
    public async Task<TokenRefreshResult> RefreshAsync(string sessionId, string refreshToken, CancellationToken ct)
    {
        if (!Guid.TryParseExact(sessionId, "N", out var id)) throw new OidcException("Unknown session.");
        Tenant tenant;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var tenantId = await db.ChatSessions.AsNoTracking().Where(s => s.Id == id).Select(s => s.TenantId).FirstOrDefaultAsync(ct)
                ?? throw new OidcException("Unknown session.");
            tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantId, ct);
        }
        var r = await client.RefreshAsync(tenant, refreshToken, ct);
        return new TokenRefreshResult
        {
            AccessToken = r.AccessToken,
            AccessExpiresAt = clock.GetUtcNow().AddSeconds(r.ExpiresIn),
            RefreshToken = r.RefreshToken,
            IdToken = r.IdToken,
            Scopes = r.Scopes,
        };
    }
}
