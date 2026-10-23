using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Auth.Elevation;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Biatec.Chat.Tests.Auth;

/// <summary>Scope elevation (T-010). Needs the docker-compose postgres; Biatec OIDC is the in-process <see cref="MockOidc"/>.</summary>
public sealed class ElevationTests : IAsyncLifetime
{
    private sealed class MemoryTenantStore(params Tenant[] tenants) : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) => Task.FromResult(tenants.FirstOrDefault(t => t.Host == host));
    }

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStates : IAuthStateStore
    {
        public readonly Dictionary<string, PendingAuth> Items = new();
        public Task PutAsync(string h, PendingAuth p, TimeSpan ttl, CancellationToken ct) { Items[h] = p; return Task.CompletedTask; }
        public Task<PendingAuth?> GetAsync(string h, CancellationToken ct) => Task.FromResult(Items.GetValueOrDefault(h));
        public Task<bool> TakeAsync(string h, CancellationToken ct) => Task.FromResult(Items.Remove(h));
    }

    private sealed class MemoryVault : ITokenVault
    {
        public readonly Dictionary<string, TokenVaultEntry> Items = new();
        public Task PutAsync(string id, TokenVaultEntry e, CancellationToken ct = default) { Items[id] = e; return Task.CompletedTask; }
        public Task<TokenVaultEntry?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task DeleteAsync(string id, CancellationToken ct = default) { Items.Remove(id); return Task.CompletedTask; }
        public Task<string?> GetAccessTokenAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id)?.AccessToken);
    }

    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private const string Origin = "https://web.biatec.io";
    private const string Host = "chat.biatec.io";
    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly FakeTime _time = new();
    private readonly MockOidc _oidc = new();
    private readonly MemoryStates _states = new();
    private readonly MemoryVault _vault = new();
    private WebApplicationFactory<Program> _factory = null!;
    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;

    private async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync()
    {
        _oidc.Now = _time.Now;
        await AdminAsync($"CREATE DATABASE \"{_dbName}\"");
        await using (var db = NewContext())
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(TenantSeed.BiatecChat());
            await db.SaveChangesAsync();
        }
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oidc:Issuer"] = MockOidc.Issuer,
                ["Oidc:McpResource"] = MockOidc.Resource,
                ["Mcp:Endpoint"] = "https://mcp.example.test/mcp",
                ["Redis:ConnectionString"] = "127.0.0.1:1",
                ["Postgres:ConnectionString"] = Cs,
                ["Algod:Networks:testnet:Url"] = "https://algod.example.test",
                ["Kms:KeyId"] = "alias/test",
                ["Secrets:biatec-chat:oidc-client-secret"] = MockOidc.ClientSecret,
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ITenantStore>();
                s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat(), TenantSeed.LocalhostDev()));
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(_time);
                s.RemoveAll<IAuthStateStore>();
                s.AddSingleton<IAuthStateStore>(_states);
                s.RemoveAll<ITokenVault>();
                s.AddSingleton<ITokenVault>(_vault);
                s.AddHttpClient(OidcMetadataProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _oidc);
            });
        });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private ChatDbContext NewContext() => new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod m, string pathAndQuery, string? cookie = null, object? json = null)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var r = new HttpRequestMessage(m, "http://" + Host + pathAndQuery);
        if (m != HttpMethod.Get)
        {
            r.Headers.TryAddWithoutValidation("Origin", Origin);
            r.Headers.TryAddWithoutValidation("X-Biatec-Chat", "1");
            r.Headers.TryAddWithoutValidation("X-Biatec-Client-Key", "biatec-web");
        }
        if (json is not null) r.Content = JsonContent.Create(json);
        if (cookie is not null) r.Headers.TryAddWithoutValidation("Cookie", $"{SessionPolicy.CookieName}={cookie}");
        return await client.SendAsync(r);
    }

    private static string? SetCookie(HttpResponseMessage r) =>
        r.Headers.TryGetValues("Set-Cookie", out var v) ? v.SingleOrDefault(x => x.StartsWith(SessionPolicy.CookieName + "=")) : null;

    private static string TokenOf(string setCookie) => setCookie[(SessionPolicy.CookieName.Length + 1)..].Split(';')[0];

    /// <summary>
    /// Signs in through the real login flow and returns the authenticated session's cookie. Login asks for `sign`, but here
    /// the server narrows it away (a client not allow-listed for `sign`), which is the case elevation exists for.
    /// </summary>
    private async Task<string> LoginAsync()
    {
        var anon = TokenOf(SetCookie(await SendAsync(HttpMethod.Get, "/session/probe"))!);
        var start = await SendAsync(HttpMethod.Post, "/auth/popup", anon);
        var path = new Uri((await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("loginUrl").GetString()!).PathAndQuery;
        var redirect = await SendAsync(HttpMethod.Get, path, anon);
        var (code, state) = _oidc.Authorize(redirect.Headers.Location!);
        _oidc.GrantedScopeOverride = "openid profile email";
        var cb = await SendAsync(HttpMethod.Get, $"/auth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", anon);
        _oidc.GrantedScopeOverride = null;
        Assert.Equal(HttpStatusCode.OK, cb.StatusCode);
        return TokenOf(SetCookie(cb)!);
    }

    private async Task<(Guid Id, string Path)> StartElevationAsync(string cookie, string scope)
    {
        var r = await SendAsync(HttpMethod.Post, "/auth/elevate/popup", cookie, new { scope });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        var url = new Uri(j.GetProperty("loginUrl").GetString()!);
        Assert.Equal("https://chat.biatec.io", url.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/auth/elevate", url.AbsolutePath);
        return (Guid.Parse(j.GetProperty("authRequestId").GetString()!), url.PathAndQuery);
    }

    /// <summary>Runs the whole elevation in the popup and returns the callback response.</summary>
    private async Task<(Guid Id, HttpResponseMessage Callback)> ElevateAsync(string cookie, string scope, string expectedOidcScope)
    {
        var (id, path) = await StartElevationAsync(cookie, scope);
        var redirect = await SendAsync(HttpMethod.Get, path, cookie);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        var (code, state) = _oidc.Authorize(redirect.Headers.Location!, expectedOidcScope);
        var cb = await SendAsync(HttpMethod.Get, $"/auth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", cookie);
        return (id, cb);
    }

    private IScopeGuard Guard => _factory.Services.GetRequiredService<IScopeGuard>();

    private async Task<PopupAuthStatus> StatusAsync(Guid id)
    {
        await using var db = NewContext();
        return (await db.PopupAuthRequests.SingleAsync(p => p.Id == id)).Status;
    }

    private string SessionIdOf(string cookie) => SessionService.IdFromToken(cookie).ToString("N");

    [Theory]
    [InlineData("rekey")]
    [InlineData("openid")]
    [InlineData("admin")]
    [InlineData("sign rekey")]
    [InlineData("sign manage-limits")]
    [InlineData("SIGN")]
    [InlineData("sign ")]
    [InlineData("")]
    public async Task Elevation_RejectsAnyScopeOutsideTheAllowList(string scope)
    {
        var cookie = await LoginAsync();
        var r = await SendAsync(HttpMethod.Post, "/auth/elevate/popup", cookie, new { scope });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);

        // The navigation endpoint re-checks on its own: a hand-built URL cannot smuggle rekey in.
        var nav = await SendAsync(HttpMethod.Get, $"/auth/elevate?req={Guid.NewGuid()}&nonce=x&scope={Uri.EscapeDataString(scope)}", cookie);
        Assert.Equal(HttpStatusCode.BadRequest, nav.StatusCode);
        Assert.Null(nav.Headers.Location);
        Assert.Single(_oidc.TokenRequests); // only the login exchange
        await using var db = NewContext();
        Assert.False(await db.PopupAuthRequests.AnyAsync(p => p.Purpose == PopupAuthPurpose.Elevate));
    }

    [Fact]
    public async Task Elevation_RejectsMissingBodyAndAnonymousSessions()
    {
        var anon = TokenOf(SetCookie(await SendAsync(HttpMethod.Get, "/session/probe"))!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/auth/elevate/popup", anon, new { scope = "sign" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/auth/elevate/popup", anon, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/auth/elevate/popup", null, new { scope = "sign" })).StatusCode);
        var nav = await SendAsync(HttpMethod.Get, $"/auth/elevate?req={Guid.NewGuid()}&nonce=x&scope=sign", anon);
        Assert.Equal(HttpStatusCode.Forbidden, nav.StatusCode);
    }

    [Fact]
    public async Task Elevation_RequestsTheUnionOfHeldAndNewScopes_AndVaultsTheNewTokens()
    {
        var cookie = await LoginAsync();
        var sid = SessionIdOf(cookie);
        Assert.Equal(["openid", "profile", "email"], _vault.Items[sid].Scopes);
        var loginRefresh = _vault.Items[sid].RefreshToken;
        _oidc.RefreshToken = "REFRESH-TOKEN-SECRET-AFTER-SIGN";
        _oidc.AccessToken = "ACCESS-TOKEN-SECRET-AFTER-SIGN";

        var (id, cb) = await ElevateAsync(cookie, "sign", "openid profile email sign");
        Assert.Equal(HttpStatusCode.OK, cb.StatusCode);
        Assert.Null(SetCookie(cb)); // same session, same cookie: nothing is rotated
        Assert.Equal(PopupAuthStatus.Completed, await StatusAsync(id));

        var entry = _vault.Items[sid];
        Assert.Equal("ACCESS-TOKEN-SECRET-AFTER-SIGN", entry.AccessToken);
        Assert.NotEqual(loginRefresh, entry.RefreshToken);
        Assert.Equal(["openid", "profile", "email", "sign"], entry.Scopes);
        Assert.Single(_vault.Items);

        // The second elevation requests everything already held plus the new scope: scopes only ever grow.
        var (_, cb2) = await ElevateAsync(cookie, "manage-limits", "openid profile email sign manage-limits");
        Assert.Equal(HttpStatusCode.OK, cb2.StatusCode);
        Assert.Equal(["openid", "profile", "email", "sign", "manage-limits"], _vault.Items[sid].Scopes);

        // Every token request still carried the MCP resource (the mock enforces it) and the secret.
        Assert.All(_oidc.TokenRequests, r => Assert.Equal(MockOidc.Resource, r["resource"]));
        var body = await cb.Content.ReadAsStringAsync();
        Assert.DoesNotContain("TOKEN-SECRET", body);
        Assert.DoesNotContain("eyJ", body);
    }

    [Fact]
    public async Task Elevation_CallbackInvalidatesTheCachedSessionProfile()
    {
        var cookie = await LoginAsync();
        async Task<string[]> ScopesAsync() =>
            [.. (await (await SendAsync(HttpMethod.Get, "/session", cookie)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scopes").EnumerateArray().Select(x => x.GetString()!)];

        Assert.DoesNotContain("sign", await ScopesAsync()); // primes the profile cache
        var (_, cb) = await ElevateAsync(cookie, "sign", "openid profile email sign");
        Assert.Equal(HttpStatusCode.OK, cb.StatusCode);
        Assert.Contains("sign", await ScopesAsync()); // not the stale cached scopes
    }

    [Fact]
    public async Task Elevation_ReElevatingTheSameScopeKeepsTheUnionStable()
    {
        var cookie = await LoginAsync();
        await ElevateAsync(cookie, "sign", "openid profile email sign");
        var (_, cb) = await ElevateAsync(cookie, "sign", "openid profile email sign");
        Assert.Equal(HttpStatusCode.OK, cb.StatusCode);
        Assert.Equal(["openid", "profile", "email", "sign"], _vault.Items[SessionIdOf(cookie)].Scopes);
    }

    [Fact]
    public async Task ElevationScopeGuard_RefusesSigningWithoutSignAndOffersReauthorization()
    {
        var cookie = await LoginAsync();
        var sid = SessionIdOf(cookie);

        var refused = await Guard.CheckAsync(sid, ElevationScopes.Sign);
        Assert.False(refused.Allowed);
        Assert.Equal("sign", refused.Reauthorize);
        Assert.False(string.IsNullOrWhiteSpace(refused.Message));
        var limits = await Guard.CheckAsync(sid, ElevationScopes.ManageLimits);
        Assert.False(limits.Allowed);
        Assert.Equal("manage-limits", limits.Reauthorize);

        await ElevateAsync(cookie, "sign", "openid profile email sign");
        Assert.True((await Guard.CheckAsync(sid, ElevationScopes.Sign)).Allowed);
        Assert.False((await Guard.CheckAsync(sid, ElevationScopes.ManageLimits)).Allowed); // sign does not imply limits
    }

    [Fact]
    public async Task ElevationScopeGuard_RefusesUnknownScopesUnknownSessionsAndExpiredEntries()
    {
        var cookie = await LoginAsync();
        var sid = SessionIdOf(cookie);
        var rekey = await Guard.CheckAsync(sid, "rekey");
        Assert.False(rekey.Allowed);
        Assert.Null(rekey.Reauthorize); // nothing is ever offered for rekey

        var unknown = await Guard.CheckAsync(Guid.NewGuid().ToString("N"), ElevationScopes.Sign);
        Assert.False(unknown.Allowed);
        Assert.Null(unknown.Reauthorize);

        _vault.Items[sid] = new TokenVaultEntry
        {
            AccessToken = "a", RefreshToken = "r", AccessExpiresAt = _time.Now.AddMinutes(5),
            Scopes = ["openid", "sign"], AbsoluteExpiresAt = _time.Now.AddMinutes(-1),
        };
        Assert.False((await Guard.CheckAsync(sid, ElevationScopes.Sign)).Allowed);
    }

    [Fact]
    public async Task Elevation_NonceIsSingleUseAndBoundToItsScope()
    {
        var cookie = await LoginAsync();
        var (id, path) = await StartElevationAsync(cookie, "sign");

        // Same nonce, different scope: refused, and the request is still usable for the scope it was made for.
        var swapped = path.Replace("scope=sign", "scope=manage-limits");
        Assert.NotEqual(path, swapped);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, swapped, cookie)).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await SendAsync(HttpMethod.Get, path, cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, path, cookie)).StatusCode); // replay
        Assert.Equal(PopupAuthStatus.Pending, await StatusAsync(id));
    }

    [Fact]
    public async Task Elevation_LoginNonceCannotStartAnElevation()
    {
        var cookie = await LoginAsync();
        var start = await SendAsync(HttpMethod.Post, "/auth/popup", cookie);
        var loginUrl = new Uri((await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("loginUrl").GetString()!);
        var forged = loginUrl.PathAndQuery.Replace("/auth/login", "/auth/elevate") + "&scope=sign";
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, forged, cookie)).StatusCode);
    }

    [Fact]
    public async Task Elevation_CrossSiteNavigationIsRefused()
    {
        var cookie = await LoginAsync();
        var (_, path) = await StartElevationAsync(cookie, "sign");
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var r = new HttpRequestMessage(HttpMethod.Get, "http://" + Host + path);
        r.Headers.TryAddWithoutValidation("Cookie", $"{SessionPolicy.CookieName}={cookie}");
        r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(r)).StatusCode);
    }

    [Fact]
    public async Task Elevation_StateOfAnotherSessionIsRejected()
    {
        var attacker = await LoginAsync();
        var (_, path) = await StartElevationAsync(attacker, "sign");
        var redirect = await SendAsync(HttpMethod.Get, path, attacker);
        var (code, state) = _oidc.Authorize(redirect.Headers.Location!, "openid profile email sign");

        _oidc.Sub = "victim";
        var victim = await LoginAsync();
        var before = _vault.Items[SessionIdOf(victim)];
        var r = await SendAsync(HttpMethod.Get, $"/auth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", victim);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Same(before, _vault.Items[SessionIdOf(victim)]);
        Assert.DoesNotContain("sign", _vault.Items[SessionIdOf(victim)].Scopes);
    }

    [Fact]
    public async Task Elevation_ConsentByAnotherAccountIsRefusedAndNothingChanges()
    {
        var cookie = await LoginAsync();
        var sid = SessionIdOf(cookie);
        var before = _vault.Items[sid];
        _oidc.Sub = "someone-else"; // the popup user signed in as a different account at Biatec OIDC

        var (id, cb) = await ElevateAsync(cookie, "sign", "openid profile email sign");
        Assert.Equal(HttpStatusCode.BadRequest, cb.StatusCode);
        Assert.Equal(PopupAuthStatus.Failed, await StatusAsync(id));
        Assert.Same(before, _vault.Items[sid]);
        Assert.False((await Guard.CheckAsync(sid, ElevationScopes.Sign)).Allowed);
    }

    // BCHAT-034: a token response without a scope grants nothing that was merely requested.
    [Fact]
    public async Task Login_WhoseTokenResponseOmitsTheScope_VaultsOnlyOpenid()
    {
        var anon = TokenOf(SetCookie(await SendAsync(HttpMethod.Get, "/session/probe"))!);
        var start = await SendAsync(HttpMethod.Post, "/auth/popup", anon);
        var path = new Uri((await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("loginUrl").GetString()!).PathAndQuery;
        var redirect = await SendAsync(HttpMethod.Get, path, anon);
        var (code, state) = _oidc.Authorize(redirect.Headers.Location!);
        HttpResponseMessage cb;
        _oidc.OmitScope = true;
        try { cb = await SendAsync(HttpMethod.Get, $"/auth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", anon); }
        finally { _oidc.OmitScope = false; }

        Assert.Equal(HttpStatusCode.OK, cb.StatusCode);
        var sid = SessionIdOf(TokenOf(SetCookie(cb)!));
        Assert.Equal(["openid"], _vault.Items[sid].Scopes);
        Assert.False((await Guard.CheckAsync(sid, ElevationScopes.Sign)).Allowed);
    }

    [Fact]
    public async Task Elevation_WhoseTokenResponseHasAnEmptyScope_IsRefusedToo()
    {
        var cookie = await LoginAsync();
        var sid = SessionIdOf(cookie);
        var before = _vault.Items[sid];
        _oidc.GrantedScopeOverride = " ";
        Guid id;
        HttpResponseMessage cb;
        try { (id, cb) = await ElevateAsync(cookie, "sign", "openid profile email sign"); }
        finally { _oidc.GrantedScopeOverride = null; }
        Assert.Equal(HttpStatusCode.BadRequest, cb.StatusCode);
        Assert.Equal(PopupAuthStatus.Failed, await StatusAsync(id));
        Assert.Same(before, _vault.Items[sid]);
    }

    [Fact]
    public async Task Elevation_WhoseTokenResponseOmitsTheScope_IsRefused_AndLeavesTheEntryUntouched()
    {
        var cookie = await LoginAsync();
        var sid = SessionIdOf(cookie);
        var before = _vault.Items[sid];
        _oidc.OmitScope = true;
        Guid id;
        HttpResponseMessage cb;
        try { (id, cb) = await ElevateAsync(cookie, "sign", "openid profile email sign"); }
        finally { _oidc.OmitScope = false; }
        Assert.Equal(HttpStatusCode.BadRequest, cb.StatusCode);
        Assert.Equal(PopupAuthStatus.Failed, await StatusAsync(id));
        Assert.Same(before, _vault.Items[sid]);
        Assert.DoesNotContain("sign", _vault.Items[sid].Scopes);
    }

    [Fact]
    public async Task Elevation_DeclinedScopeLeavesTheExistingEntryUntouched()
    {
        var cookie = await LoginAsync();
        var sid = SessionIdOf(cookie);
        var before = _vault.Items[sid];
        _oidc.GrantedScopeOverride = "openid profile email"; // the server did not grant sign

        var (id, cb) = await ElevateAsync(cookie, "sign", "openid profile email sign");
        Assert.Equal(HttpStatusCode.BadRequest, cb.StatusCode);
        Assert.Equal(PopupAuthStatus.Failed, await StatusAsync(id));
        Assert.Same(before, _vault.Items[sid]);
    }

    [Fact]
    public async Task Elevation_SupersededPopupCannotStillChangeTheVault()
    {
        var cookie = await LoginAsync();
        var sid = SessionIdOf(cookie);
        var before = _vault.Items[sid];
        var (firstId, path) = await StartElevationAsync(cookie, "sign");
        var redirect = await SendAsync(HttpMethod.Get, path, cookie);
        var (code, state) = _oidc.Authorize(redirect.Headers.Location!, "openid profile email sign");
        await StartElevationAsync(cookie, "manage-limits"); // retires the first request

        var cb = await SendAsync(HttpMethod.Get, $"/auth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", cookie);
        Assert.Equal(HttpStatusCode.BadRequest, cb.StatusCode);
        Assert.Equal(PopupAuthStatus.Expired, await StatusAsync(firstId));
        Assert.Same(before, _vault.Items[sid]);
    }

    [Fact]
    public async Task Elevation_ResponseThatDropsAHeldAllowListedScopeIsRefused()
    {
        var cookie = await LoginAsync();
        var sid = SessionIdOf(cookie);
        await ElevateAsync(cookie, "sign", "openid profile email sign");
        var before = _vault.Items[sid];
        _oidc.GrantedScopeOverride = "openid profile email manage-limits"; // sign silently dropped

        var (_, cb) = await ElevateAsync(cookie, "manage-limits", "openid profile email sign manage-limits");
        Assert.Equal(HttpStatusCode.BadRequest, cb.StatusCode);
        Assert.Same(before, _vault.Items[sid]);
        Assert.True((await Guard.CheckAsync(sid, ElevationScopes.Sign)).Allowed);
    }

    [Fact]
    public async Task Elevation_StartingOneDoesNotRetireAPendingLoginPopup()
    {
        var cookie = await LoginAsync();
        var login = await SendAsync(HttpMethod.Post, "/auth/popup", cookie);
        var loginId = Guid.Parse((await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authRequestId").GetString()!);
        await StartElevationAsync(cookie, "sign");
        Assert.Equal(PopupAuthStatus.Pending, await StatusAsync(loginId));
    }

    [Fact]
    public async Task Elevation_StartingALoginDoesNotRetireAPendingElevation()
    {
        var cookie = await LoginAsync();
        var (id, _) = await StartElevationAsync(cookie, "sign");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/auth/popup", cookie)).StatusCode);
        Assert.Equal(PopupAuthStatus.Pending, await StatusAsync(id));
    }

    [Fact]
    public async Task Elevation_NeverReRequestsAHeldScopeOutsideTheAllowList()
    {
        var cookie = await LoginAsync();
        var sid = SessionIdOf(cookie);
        var e = _vault.Items[sid];
        _vault.Items[sid] = new TokenVaultEntry
        {
            AccessToken = e.AccessToken, RefreshToken = e.RefreshToken, AccessExpiresAt = e.AccessExpiresAt,
            AbsoluteExpiresAt = e.AbsoluteExpiresAt, IdToken = e.IdToken, Scopes = [.. e.Scopes, "rekey"],
        };
        var (_, path) = await StartElevationAsync(cookie, "sign");
        var redirect = await SendAsync(HttpMethod.Get, path, cookie);
        _oidc.Authorize(redirect.Headers.Location!, "openid profile email sign"); // no rekey in the request
    }

    [Fact]
    public async Task Elevation_PopupStartNeedsTheCsrfHeader()
    {
        var cookie = await LoginAsync();
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var r = new HttpRequestMessage(HttpMethod.Post, "http://" + Host + "/auth/elevate/popup") { Content = JsonContent.Create(new { scope = "sign" }) };
        r.Headers.TryAddWithoutValidation("Cookie", $"{SessionPolicy.CookieName}={cookie}");
        r.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        var res = await client.SendAsync(r);
        Assert.True((int)res.StatusCode is 400 or 401 or 403, res.StatusCode.ToString());
        await using var db = NewContext();
        Assert.False(await db.PopupAuthRequests.AnyAsync(p => p.Purpose == PopupAuthPurpose.Elevate));
    }

    [Fact]
    public async Task Elevation_KeepsTheAbsoluteSessionLifetime()
    {
        var cookie = await LoginAsync();
        var sid = SessionIdOf(cookie);
        var absolute = _vault.Items[sid].AbsoluteExpiresAt;
        _time.Now = _time.Now.AddMinutes(5);
        await ElevateAsync(cookie, "sign", "openid profile email sign");
        Assert.Equal(absolute, _vault.Items[sid].AbsoluteExpiresAt);
    }
}
