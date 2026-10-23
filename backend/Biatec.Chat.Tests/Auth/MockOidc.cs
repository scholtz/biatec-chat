using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Biatec.Chat.Tests.Auth;

/// <summary>
/// In-process stand-in for Biatec OIDC: discovery, authorize, token (authorization code with S256 PKCE and refresh)
/// and JWKS. It checks what a real server checks (client secret, redirect URI, verifier, resource) so the tests
/// fail if the backend sends the wrong thing. No network is touched.
/// </summary>
public sealed class MockOidc : HttpMessageHandler
{
    public const string Issuer = "https://oidc.example.test";
    public const string Resource = "https://mcp.example.test";
    public const string ClientId = "biatec-chat";
    public const string ClientSecret = "test-client-secret";

    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "k1" };
    private readonly RsaSecurityKey _otherKey = new(RSA.Create(2048)) { KeyId = "k1" };

    private sealed record Issued(string ChallengeS256, string Nonce, string RedirectUri, string Resource, string Scope);
    private readonly Dictionary<string, Issued> _codes = new();
    private int _refreshCounter;

    public string Sub = "user-123";
    public string Email = "alice@example.test";
    /// <summary>The email_verified claim: null leaves it out; a bool or a string ("true") is sent as is.</summary>
    public object? EmailVerified;
    public DateTimeOffset Now = DateTimeOffset.UtcNow;
    public string AccessToken = "ACCESS-TOKEN-SECRET";
    public string RefreshToken = "REFRESH-TOKEN-SECRET";
    /// <summary>When set, the token endpoint reports this scope instead of the requested one (a user who declined a scope).</summary>
    public string? GrantedScopeOverride;

    /// <summary>Tamper switches for the id_token the next exchange returns.</summary>
    public string? IdTokenIssuer, IdTokenAudience, IdTokenNonceOverride;
    public bool IdTokenExpired, IdTokenSignedWithOtherKey;

    public List<Dictionary<string, string>> TokenRequests { get; } = [];

    /// <summary>What the browser does at Biatec OIDC: validates the authorize request and returns (code, state) for the callback.</summary>
    public (string Code, string State) Authorize(Uri location, string expectedScope = "openid profile email sign")
    {
        Assert.Equal(Issuer, location.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/authorize", location.AbsolutePath);
        var q = System.Web.HttpUtility.ParseQueryString(location.Query);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal(ClientId, q["client_id"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal(expectedScope, q["scope"]);
        Assert.Equal(Resource, q["resource"]);
        Assert.Null(q["client_secret"]);
        Assert.False(string.IsNullOrEmpty(q["state"]));
        Assert.False(string.IsNullOrEmpty(q["nonce"]));
        Assert.InRange(q["code_challenge"]!.Length, 43, 128);
        var code = "code-" + Guid.NewGuid().ToString("N");
        _codes[code] = new Issued(q["code_challenge"]!, q["nonce"]!, q["redirect_uri"]!, q["resource"]!, q["scope"]!);
        return (code, q["state"]!);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!;
        Assert.Equal(Issuer, url.GetLeftPart(UriPartial.Authority));
        return Task.FromResult(url.AbsolutePath switch
        {
            "/.well-known/openid-configuration" => Json(new
            {
                issuer = Issuer,
                authorization_endpoint = Issuer + "/authorize",
                token_endpoint = Issuer + "/token",
                jwks_uri = Issuer + "/.well-known/jwks.json",
            }),
            "/.well-known/jwks.json" => Jwks(),
            "/token" => Token(request),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound),
        });
    }

    private HttpResponseMessage Jwks()
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_key);
        return Json(new { keys = new[] { new { kty = jwk.Kty, use = "sig", alg = "RS256", kid = jwk.Kid, n = jwk.N, e = jwk.E } } });
    }

    private HttpResponseMessage Token(HttpRequestMessage request)
    {
        var form = System.Web.HttpUtility.ParseQueryString(request.Content!.ReadAsStringAsync().Result)
            .AllKeys.ToDictionary(k => k!, k => System.Web.HttpUtility.ParseQueryString(request.Content.ReadAsStringAsync().Result)[k]!);
        TokenRequests.Add(form);
        if (form.GetValueOrDefault("client_id") != ClientId || form.GetValueOrDefault("client_secret") != ClientSecret) return Error("invalid_client");
        if (form.GetValueOrDefault("resource") != Resource) return Error("invalid_target");

        switch (form.GetValueOrDefault("grant_type"))
        {
            case "authorization_code":
                if (!_codes.Remove(form.GetValueOrDefault("code") ?? "", out var issued)) return Error("invalid_grant"); // single use
                if (form.GetValueOrDefault("redirect_uri") != issued.RedirectUri) return Error("invalid_grant");
                var verifier = form.GetValueOrDefault("code_verifier") ?? "";
                var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                if (challenge != issued.ChallengeS256) return Error("invalid_grant");
                return Json(new
                {
                    access_token = AccessToken, refresh_token = RefreshToken, token_type = "Bearer", expires_in = 900,
                    scope = GrantedScopeOverride ?? issued.Scope, id_token = IdToken(issued.Nonce),
                });
            case "refresh_token":
                if (form.GetValueOrDefault("refresh_token") != RefreshToken) return Error("invalid_grant");
                RefreshToken = "REFRESH-TOKEN-SECRET-" + ++_refreshCounter;
                AccessToken = "ACCESS-TOKEN-SECRET-" + _refreshCounter;
                return Json(new { access_token = AccessToken, refresh_token = RefreshToken, token_type = "Bearer", expires_in = 900 });
            default:
                return Error("unsupported_grant_type");
        }
    }

    private string IdToken(string nonce)
    {
        var now = Now.UtcDateTime;
        var d = new SecurityTokenDescriptor
        {
            Issuer = IdTokenIssuer ?? Issuer,
            Audience = IdTokenAudience ?? ClientId,
            IssuedAt = now,
            NotBefore = now,
            Expires = IdTokenExpired ? now.AddHours(-1).AddSeconds(-1) : now.AddMinutes(10),
            Claims = new Dictionary<string, object> { ["sub"] = Sub, ["email"] = Email, ["nonce"] = IdTokenNonceOverride ?? nonce },
            SigningCredentials = new SigningCredentials(IdTokenSignedWithOtherKey ? _otherKey : _key, SecurityAlgorithms.RsaSha256),
        };
        if (EmailVerified is not null) d.Claims["email_verified"] = EmailVerified;
        if (IdTokenExpired) d.NotBefore = now.AddHours(-2);
        if (IdTokenExpired) d.IssuedAt = now.AddHours(-2);
        return new JsonWebTokenHandler().CreateToken(d);
    }

    private static HttpResponseMessage Json(object o) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Error(string code) => new(System.Net.HttpStatusCode.BadRequest)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { error = code }), Encoding.UTF8, "application/json"),
    };
}
