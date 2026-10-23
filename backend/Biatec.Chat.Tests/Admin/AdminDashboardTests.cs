using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Admin;
using Biatec.Chat.Api.Admin.Dashboard;
using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Retention;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Biatec.Chat.Tests.Admin;

/// <summary>
/// The administrator dashboard (T-126, docs/admin-dashboard.md): admin sign-in through a mock Biatec OIDC, the guard, the
/// statistics and user list, read-only impersonation with its evidence trail, and the static app. Needs the docker-compose postgres.
/// </summary>
public sealed class AdminDashboardTests : IAsyncLifetime
{
    private const string Host = "chat.biatec.io";
    private const string Origin = "https://chat.biatec.io";
    private const string Admins = "Alice@Example.test; bob@example.test ;;not-an-email";

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

    /// <summary>Round-trips through JSON like the Redis store, so a property that does not serialize fails here.</summary>
    private sealed class MemoryAdminSessions : IAdminSessionStore
    {
        public readonly Dictionary<string, byte[]> Items = new();
        public Task PutAsync(string h, AdminSession s, TimeSpan ttl, CancellationToken ct) { Items[h] = JsonSerializer.SerializeToUtf8Bytes(s); return Task.CompletedTask; }
        public Task<bool> ReplaceAsync(string h, AdminSession s, TimeSpan ttl, CancellationToken ct)
        {
            if (!Items.ContainsKey(h)) return Task.FromResult(false);
            Items[h] = JsonSerializer.SerializeToUtf8Bytes(s);
            return Task.FromResult(true);
        }
        public Task<AdminSession?> GetAsync(string h, CancellationToken ct) =>
            Task.FromResult(Items.TryGetValue(h, out var b) ? JsonSerializer.Deserialize<AdminSession>(b) : null);
        public Task TouchAsync(string h, TimeSpan ttl, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string h, CancellationToken ct) { Items.Remove(h); return Task.CompletedTask; }
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

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly FakeTime _time = new();
    private readonly MockOidc _oidc = new();
    private readonly MemoryStates _states = new();
    private readonly MemoryAdminSessions _sessions = new();
    private readonly MemoryVault _vault = new();
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly DirectoryInfo _ui = Directory.CreateTempSubdirectory("admin-ui-");
    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;

    private async Task AdminSqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private ChatDbContext NewContext() => new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);

    public async Task InitializeAsync()
    {
        _oidc.Now = _time.Now;
        await AdminSqlAsync($"CREATE DATABASE \"{_dbName}\"");
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        db.Tenants.Add(TenantSeed.BiatecChat());
        await db.SaveChangesAsync();
        await File.WriteAllTextAsync(Path.Combine(_ui.FullName, "index.html"), "<!doctype html><title>Biatec Admin</title><script src=\"assets/app.js\"></script>");
        Directory.CreateDirectory(Path.Combine(_ui.FullName, "assets"));
        await File.WriteAllTextAsync(Path.Combine(_ui.FullName, "assets", "app.js"), "console.log('admin');");
    }

    public async Task DisposeAsync()
    {
        foreach (var f in _factories) f.Dispose();
        try { _ui.Delete(true); } catch (IOException) { }
        await AdminSqlAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private WebApplicationFactory<Program> Factory(string? admins = Admins, bool requireVerified = true)
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
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
                ["Evidence:AllowEphemeralKeys"] = "true",
                ["Secrets:biatec-chat:oidc-client-secret"] = MockOidc.ClientSecret,
                ["Admin:Admins"] = admins,
                ["Admin:RequireVerifiedEmail"] = requireVerified ? "true" : "false",
                ["Admin:UiDirectory"] = _ui.FullName,
                ["Admin:RateLimitPermits"] = "100000",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ITenantStore>();
                s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat()));
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(_time);
                s.RemoveAll<IAuthStateStore>();
                s.AddSingleton<IAuthStateStore>(_states);
                s.RemoveAll<IAdminSessionStore>();
                s.AddSingleton<IAdminSessionStore>(_sessions);
                s.RemoveAll<ITokenVault>();
                s.AddSingleton<ITokenVault>(_vault);
                s.AddHttpClient(OidcMetadataProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _oidc);
            });
        });
        _factories.Add(f);
        return f;
    }

    private static async Task<HttpResponseMessage> SendAsync(WebApplicationFactory<Program> f, HttpMethod m, string path, string? cookie = null,
        object? body = null, Action<HttpRequestMessage>? tweak = null, bool sameOrigin = true)
    {
        using var client = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var r = new HttpRequestMessage(m, "http://" + Host + path);
        if (sameOrigin) r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        if (m != HttpMethod.Get)
        {
            r.Headers.TryAddWithoutValidation("Origin", Origin);
            r.Headers.TryAddWithoutValidation("X-Biatec-Admin", "1");
        }
        if (body is not null) r.Content = JsonContent.Create(body);
        if (cookie is not null) r.Headers.TryAddWithoutValidation("Cookie", cookie);
        tweak?.Invoke(r);
        return await client.SendAsync(r);
    }

    private static string? SetCookie(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues("Set-Cookie", out var v) ? v.FirstOrDefault(x => x.StartsWith(name + "=")) : null;

    private static string CookieHeader(string setCookie) => setCookie.Split(';')[0];

    /// <summary>Plays the whole browser flow for the signed-in OIDC user and returns the callback response.</summary>
    private async Task<HttpResponseMessage> SignInAsync(WebApplicationFactory<Program> f, string? email = null, object? verified = null, bool explicitVerified = false, bool sendLoginCookie = true)
    {
        _oidc.Email = email ?? "alice@example.test";
        _oidc.EmailVerified = explicitVerified ? verified : true;
        var start = await SendAsync(f, HttpMethod.Get, "/admin/auth/login", sameOrigin: false, tweak: r => r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none"));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var (code, state) = _oidc.Authorize(start.Headers.Location!, "openid profile email");
        var login = SetCookie(start, AdminAuth.LoginCookieName);
        Assert.NotNull(login);
        return await SendAsync(f, HttpMethod.Get, $"/auth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}",
            sendLoginCookie ? CookieHeader(login) : null, sameOrigin: false);
    }

    private async Task<string> AdminCookieAsync(WebApplicationFactory<Program> f, string email = "alice@example.test")
    {
        var cb = await SignInAsync(f, email);
        Assert.Equal(HttpStatusCode.Redirect, cb.StatusCode);
        var set = SetCookie(cb, AdminAuth.CookieName);
        Assert.NotNull(set);
        return CookieHeader(set);
    }

    private async Task<(Guid ConversationId, string Sub)> SeedUserAsync(string sub, string tenant = "biatec-chat", params string[] userTexts)
    {
        await using var db = NewContext();
        var conv = new Conversation { Id = Guid.NewGuid(), TenantId = tenant, OidcSub = sub, CreatedAt = _time.Now.AddHours(-3), LastMessageAt = _time.Now.AddMinutes(-5) };
        db.Conversations.Add(conv);
        db.KnownUsers.Add(new KnownUser { Id = Guid.NewGuid(), TenantId = tenant, OidcSub = sub, FirstSeenAt = _time.Now.AddDays(-2), LastSeenAt = _time.Now.AddHours(-1), SignInCount = 3 });
        var seq = 1;
        foreach (var t in userTexts)
        {
            db.Messages.Add(new Message { ConversationId = conv.Id, Seq = seq++, Role = MessageRole.User, Content = t, CreatedAt = _time.Now.AddMinutes(-30 + seq) });
            db.Messages.Add(new Message
            {
                ConversationId = conv.Id, Seq = seq++, Role = MessageRole.Assistant, Content = "Reply to: " + t, CreatedAt = _time.Now.AddMinutes(-30 + seq),
                ModelId = "eu.anthropic.claude-sonnet-4-5-20250929-v1:0",
                ToolCalls = """[{"id":"t1","name":"wallet.balance","input":"{\"secret\":\"RAW-INPUT\"}"}]""",
                Usage = """{"modelId":"eu.anthropic.claude-sonnet-4-5-20250929-v1:0","inputTokens":100,"outputTokens":40,"cacheReadInputTokens":10,"cacheWriteInputTokens":5,"latencyMs":900}""",
            });
        }
        await db.SaveChangesAsync();
        return (conv.Id, sub);
    }

    // ---- allow-list -------------------------------------------------------------------------------------------------

    [Fact]
    public void AllowList_ParsesSemicolonSeparatedEmailsCaseInsensitively()
    {
        var emails = AdminAllowList.Parse("  Alice@Example.test ; bob@example.test;;\nCarol@x.io;bad;two@@x;@x;x@;a b@x.io;d@x.io,e@x.io", out var ignored);
        Assert.Equal(["alice@example.test", "bob@example.test", "carol@x.io"], emails);
        Assert.Equal(6, ignored);
        var list = new AdminAllowList(Microsoft.Extensions.Options.Options.Create(new AdminOptions { Admins = "Alice@Example.test" }), NullLogger<AdminAllowList>.Instance);
        Assert.True(list.Contains(" ALICE@example.TEST "));
        Assert.False(list.Contains("alice@example.test.evil.io"));
        Assert.False(list.Contains(null));
        Assert.False(new AdminAllowList(Microsoft.Extensions.Options.Options.Create(new AdminOptions()), NullLogger<AdminAllowList>.Instance).Enabled);
    }

    [Fact]
    public async Task NoAdminsConfigured_DashboardDoesNotExist()
    {
        var f = Factory(admins: "");
        foreach (var path in new[] { "/admin/auth/login", "/admin/api/me", "/admin/", "/admin/assets/app.js" })
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(f, HttpMethod.Get, path)).StatusCode);
    }

    // ---- sign-in ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SignIn_AdminGetsOpaqueSessionAndNoTokenIsKept()
    {
        var f = Factory();
        var start = await SendAsync(f, HttpMethod.Get, "/admin/auth/login", tweak: r => r.Headers.Remove("Sec-Fetch-Site"));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var login = SetCookie(start, AdminAuth.LoginCookieName)!;
        Assert.Contains("httponly", login, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", login, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", login, StringComparison.OrdinalIgnoreCase);
        var (code, state) = _oidc.Authorize(start.Headers.Location!, "openid profile email"); // no sign scope, no offline access
        _oidc.Email = "ALICE@example.test";
        _oidc.EmailVerified = true;

        var cb = await SendAsync(f, HttpMethod.Get, $"/auth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", CookieHeader(login), sameOrigin: false);
        Assert.Equal(HttpStatusCode.Redirect, cb.StatusCode);
        Assert.Equal("https://chat.biatec.io/admin/", cb.Headers.Location!.ToString());
        Assert.Equal("no-store", cb.Headers.CacheControl?.ToString());
        var set = SetCookie(cb, AdminAuth.CookieName)!;
        foreach (var flag in new[] { "httponly", "secure", "samesite=strict", "path=/" })
            Assert.Contains(flag, set, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", set, StringComparison.OrdinalIgnoreCase);

        // Nothing of the OIDC response survives: no vault entry, and the browser saw neither a token nor the code.
        Assert.Empty(_vault.Items);
        var everything = string.Join("\n", cb.Headers.Select(h => h.Key + ": " + string.Join(",", h.Value)));
        foreach (var secret in new[] { _oidc.AccessToken, _oidc.RefreshToken, "eyJ", MockOidc.ClientSecret })
            Assert.DoesNotContain(secret, everything);
        Assert.DoesNotContain(_oidc.AccessToken, string.Concat(_sessions.Items.Values.Select(System.Text.Encoding.UTF8.GetString)));

        var me = await SendAsync(f, HttpMethod.Get, "/admin/api/me", CookieHeader(set));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("no-store", me.Headers.CacheControl?.ToString());
        var j = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("alice@example.test", j.GetProperty("email").GetString());
        Assert.Equal("biatec-chat", j.GetProperty("tenantId").GetString());
        Assert.Equal(JsonValueKind.Null, j.GetProperty("impersonation").ValueKind);

        // The sign-in is in the signed admin evidence chain.
        await using var db = NewContext();
        var entry = await db.EvidenceEntries.AsNoTracking().SingleAsync(e => e.ConversationId == AdminEvidence.ChainId);
        var payload = JsonNode.Parse(entry.Payload!)!;
        Assert.Equal(AdminEvidence.DashboardSignIn, payload["action"]!.GetValue<string>());
        Assert.Equal("alice@example.test", payload["operator"]!.GetValue<string>());
        Assert.Equal("oidc-dashboard", payload["operatorAuth"]!.GetValue<string>());
    }

    [Fact]
    public async Task SignIn_NonAdminIsRefusedWithoutSession()
    {
        var f = Factory();
        var cb = await SignInAsync(f, "mallory@example.test");
        Assert.Equal(HttpStatusCode.Redirect, cb.StatusCode);
        Assert.Equal("https://chat.biatec.io/admin/?error=not_admin", cb.Headers.Location!.ToString());
        Assert.Null(SetCookie(cb, AdminAuth.CookieName));
        Assert.Empty(_sessions.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    [InlineData("no")]
    public async Task SignIn_UnverifiedEmailIsRefused(object? claim)
    {
        var f = Factory();
        var cb = await SignInAsync(f, "alice@example.test", verified: claim, explicitVerified: true);
        Assert.Equal("https://chat.biatec.io/admin/?error=email_not_verified", cb.Headers.Location!.ToString());
        Assert.Empty(_sessions.Items);
    }

    [Theory]
    [InlineData(true)]
    [InlineData("true")]
    public async Task SignIn_VerifiedClaimAsBooleanOrStringIsAccepted(object claim)
    {
        var f = Factory();
        Assert.NotNull(SetCookie(await SignInAsync(f, "bob@example.test", verified: claim, explicitVerified: true), AdminAuth.CookieName));
    }

    [Fact]
    public async Task SignIn_VerifiedEmailCanBeMadeOptionalByConfiguration()
    {
        var f = Factory(requireVerified: false);
        Assert.NotNull(SetCookie(await SignInAsync(f, "alice@example.test", verified: null, explicitVerified: true), AdminAuth.CookieName));
    }

    [Fact]
    public async Task SignIn_LoginCsrf_StateWithoutTheLoginCookieIsRefusedBeforeTheCodeIsExchanged()
    {
        var f = Factory();
        var cb = await SignInAsync(f, sendLoginCookie: false);
        Assert.Equal("https://chat.biatec.io/admin/?error=login_failed", cb.Headers.Location!.ToString());
        Assert.Empty(_oidc.TokenRequests);
        Assert.Empty(_sessions.Items);

        // A login cookie from a different attempt does not match either.
        var first = await SendAsync(f, HttpMethod.Get, "/admin/auth/login", tweak: r => r.Headers.Remove("Sec-Fetch-Site"));
        var other = await SendAsync(f, HttpMethod.Get, "/admin/auth/login", tweak: r => r.Headers.Remove("Sec-Fetch-Site"));
        var (code, state) = _oidc.Authorize(first.Headers.Location!, "openid profile email");
        var cb2 = await SendAsync(f, HttpMethod.Get, $"/auth/callback?code={code}&state={Uri.EscapeDataString(state)}", CookieHeader(SetCookie(other, AdminAuth.LoginCookieName)!), sameOrigin: false);
        Assert.EndsWith("error=login_failed", cb2.Headers.Location!.ToString());
        Assert.Empty(_oidc.TokenRequests);
    }

    [Fact]
    public async Task SignIn_StateWorksOnlyOnceAndUnknownStateIsRefused()
    {
        var f = Factory();
        var start = await SendAsync(f, HttpMethod.Get, "/admin/auth/login", tweak: r => r.Headers.Remove("Sec-Fetch-Site"));
        var (code, state) = _oidc.Authorize(start.Headers.Location!, "openid profile email");
        var login = CookieHeader(SetCookie(start, AdminAuth.LoginCookieName)!);
        _oidc.EmailVerified = true;
        var url = $"/auth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}";
        Assert.NotNull(SetCookie(await SendAsync(f, HttpMethod.Get, url, login, sameOrigin: false), AdminAuth.CookieName));
        var replay = await SendAsync(f, HttpMethod.Get, url, login, sameOrigin: false);
        Assert.Null(SetCookie(replay, AdminAuth.CookieName));
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode); // the state is gone: the dashboard says so itself
        Assert.EndsWith("error=login_failed", replay.Headers.Location!.ToString());
    }

    [Fact]
    public async Task SignIn_ErrorFromOidcAndForeignIssuerAreRefused()
    {
        var f = Factory();
        var start = await SendAsync(f, HttpMethod.Get, "/admin/auth/login", tweak: r => r.Headers.Remove("Sec-Fetch-Site"));
        var (_, state) = _oidc.Authorize(start.Headers.Location!, "openid profile email");
        var login = CookieHeader(SetCookie(start, AdminAuth.LoginCookieName)!);
        var denied = await SendAsync(f, HttpMethod.Get, $"/auth/callback?error=access_denied&state={Uri.EscapeDataString(state)}", login, sameOrigin: false);
        Assert.EndsWith("error=access_denied", denied.Headers.Location!.ToString());

        var start2 = await SendAsync(f, HttpMethod.Get, "/admin/auth/login", tweak: r => r.Headers.Remove("Sec-Fetch-Site"));
        var (code2, state2) = _oidc.Authorize(start2.Headers.Location!, "openid profile email");
        var mixup = await SendAsync(f, HttpMethod.Get, $"/auth/callback?code={code2}&state={Uri.EscapeDataString(state2)}&iss={Uri.EscapeDataString("https://evil.example")}",
            CookieHeader(SetCookie(start2, AdminAuth.LoginCookieName)!), sameOrigin: false);
        Assert.EndsWith("error=login_failed", mixup.Headers.Location!.ToString());
        Assert.Empty(_oidc.TokenRequests);
    }

    [Fact]
    public async Task SignIn_TamperedIdTokenIsRefused()
    {
        var f = Factory();
        _oidc.IdTokenSignedWithOtherKey = true;
        var cb = await SignInAsync(f);
        Assert.EndsWith("error=login_failed", cb.Headers.Location!.ToString());
        Assert.Empty(_sessions.Items);
    }

    [Fact]
    public async Task Login_RefusesSubresourceNavigation()
    {
        var f = Factory();
        var r = await SendAsync(f, HttpMethod.Get, "/admin/auth/login", sameOrigin: false, tweak: x => x.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "iframe"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    // ---- guard ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Guard_RefusesMissingUnknownExpiredAndRemovedSessions()
    {
        var f = Factory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(f, HttpMethod.Get, "/admin/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(f, HttpMethod.Get, "/admin/api/me", AdminAuth.CookieName + "=forged")).StatusCode);

        var cookie = await AdminCookieAsync(f);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(f, HttpMethod.Get, "/admin/api/me", cookie)).StatusCode);

        // A session whose email is no longer on the list (the secret changed) is dead on the next request and removed.
        var key = _sessions.Items.Keys.Single();
        var s = JsonSerializer.Deserialize<AdminSession>(_sessions.Items[key])!;
        _sessions.Items[key] = JsonSerializer.SerializeToUtf8Bytes(new AdminSession { Email = "mallory@example.test", Subject = s.Subject, TenantId = s.TenantId, CreatedAt = s.CreatedAt, ExpiresAt = s.ExpiresAt });
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(f, HttpMethod.Get, "/admin/api/me", cookie)).StatusCode);
        Assert.Empty(_sessions.Items);

        // Absolute expiry.
        var cookie2 = await AdminCookieAsync(f);
        _time.Now = _time.Now.AddHours(9);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(f, HttpMethod.Get, "/admin/api/me", cookie2)).StatusCode);
    }

    [Fact]
    public async Task Guard_IsSameOriginOnly()
    {
        var f = Factory();
        var cookie = await AdminCookieAsync(f);

        // Reads: cross-site and same-site (another origin of the same site) are refused, even with the cookie.
        foreach (var site in new[] { "cross-site", "same-site" })
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(f, HttpMethod.Get, "/admin/api/me", cookie, sameOrigin: false, tweak: r => r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", site))).StatusCode);

        // Writes need the custom header and the backend's own Origin.
        var noHeader = await SendAsync(f, HttpMethod.Post, "/admin/api/logout", cookie, tweak: r => r.Headers.Remove("X-Biatec-Admin"));
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);
        foreach (var origin in new[] { "https://web.biatec.io", "http://chat.biatec.io", "https://chat.biatec.io.evil.test", "null" })
        {
            var r = await SendAsync(f, HttpMethod.Post, "/admin/api/logout", cookie, tweak: x => { x.Headers.Remove("Origin"); x.Headers.TryAddWithoutValidation("Origin", origin); });
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(f, HttpMethod.Post, "/admin/api/logout", cookie, tweak: r => r.Headers.Remove("Origin"))).StatusCode);
        Assert.Single(_sessions.Items); // none of those logged out
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f, HttpMethod.Post, "/admin/api/logout", cookie)).StatusCode);
        Assert.Empty(_sessions.Items);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(f, HttpMethod.Get, "/admin/api/me", cookie)).StatusCode);
    }

    [Fact]
    public async Task Cors_NeverAllowsAnotherOriginToReadTheDashboard()
    {
        var f = Factory();
        var cookie = await AdminCookieAsync(f);
        // https://web.biatec.io is on the tenant's allow-list for the chat API; on /admin it gets nothing.
        var r = await SendAsync(f, HttpMethod.Get, "/admin/api/me", cookie, tweak: x => x.Headers.TryAddWithoutValidation("Origin", "https://web.biatec.io"));
        Assert.False(r.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(r.Headers.Contains("Access-Control-Allow-Credentials"));
        var pre = await SendAsync(f, HttpMethod.Options, "/admin/api/me", tweak: x =>
        {
            x.Headers.TryAddWithoutValidation("Origin", "https://web.biatec.io");
            x.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
        });
        Assert.Equal(HttpStatusCode.Forbidden, pre.StatusCode);
        Assert.False(pre.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // ---- statistics and users ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Stats_CountsUsersMessagesTokensAndFillsEveryDay()
    {
        await SeedUserAsync("sub-1", userTexts: ["hello", "again"]);
        await SeedUserAsync("sub-2", userTexts: ["hi"]);
        await using (var db = NewContext())
        {
            var cid = (await db.Conversations.FirstAsync()).Id;
            db.ActionPreviews.Add(new ActionPreview { Id = Guid.NewGuid(), ConversationId = cid, Capability = "wallet.transfer", CanonicalJson = "{}", ExpiresAt = _time.Now.AddMinutes(5), Status = ActionPreviewStatus.Completed });
            db.ActionPreviews.Add(new ActionPreview { Id = Guid.NewGuid(), ConversationId = cid, Capability = "wallet.transfer", CanonicalJson = "{}", ExpiresAt = _time.Now.AddMinutes(5), Status = ActionPreviewStatus.Cancelled });
            await db.SaveChangesAsync();
        }
        var f = Factory();
        var cookie = await AdminCookieAsync(f);

        var j = await (await SendAsync(f, HttpMethod.Get, "/admin/api/stats?days=7", cookie)).Content.ReadFromJsonAsync<JsonElement>();
        var totals = j.GetProperty("totals");
        Assert.Equal(2, totals.GetProperty("users").GetInt32());
        Assert.Equal(2, totals.GetProperty("activeUsers7d").GetInt32());
        Assert.Equal(2, totals.GetProperty("conversations").GetInt32());
        Assert.Equal(6, totals.GetProperty("messages").GetInt64());
        Assert.Equal(2, totals.GetProperty("previews").GetInt32());
        Assert.Equal(1, totals.GetProperty("tenants").GetInt32());
        Assert.Equal(7, j.GetProperty("days").GetInt32());

        var tokens = j.GetProperty("tokens");
        Assert.Equal(300, tokens.GetProperty("input").GetInt64()); // three assistant messages x 100
        Assert.Equal(120, tokens.GetProperty("output").GetInt64());
        Assert.Equal(30, tokens.GetProperty("cacheRead").GetInt64());
        Assert.Equal(15, tokens.GetProperty("cacheWrite").GetInt64());
        var model = Assert.Single(tokens.GetProperty("byModel").EnumerateArray());
        Assert.Equal("eu.anthropic.claude-sonnet-4-5-20250929-v1:0", model.GetProperty("modelId").GetString());
        Assert.Equal(3, model.GetProperty("messages").GetInt64());

        var status = j.GetProperty("previewsByStatus").EnumerateArray().ToDictionary(e => e.GetProperty("status").GetString()!, e => e.GetProperty("count").GetInt32());
        Assert.Equal(1, status["Completed"]);
        Assert.Equal(1, status["Cancelled"]);

        var daily = j.GetProperty("daily").EnumerateArray().ToList();
        Assert.Equal(7, daily.Count);
        Assert.Equal("2026-10-13", daily[0].GetProperty("date").GetString());
        Assert.Equal("2026-10-19", daily[6].GetProperty("date").GetString());
        Assert.Equal(6, daily[6].GetProperty("messages").GetInt64());
        Assert.Equal(2, daily[6].GetProperty("conversations").GetInt32());
        Assert.Equal(0, daily[0].GetProperty("messages").GetInt64());
        Assert.Equal(2, daily.Sum(d => d.GetProperty("newUsers").GetInt32()));

        var tenant = Assert.Single(j.GetProperty("tenants").EnumerateArray());
        Assert.Equal(2, tenant.GetProperty("users").GetInt32());

        // A real tenant filters; an unknown one is refused (it would otherwise be an uncached query per value).
        var other = await (await SendAsync(f, HttpMethod.Get, "/admin/api/stats?days=30&tenant=biatec-chat", cookie)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, other.GetProperty("totals").GetProperty("users").GetInt32());
        Assert.Equal(30, other.GetProperty("daily").GetArrayLength());

        foreach (var bad in new[] { "days=3", "days=999", "days=x" })
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(f, HttpMethod.Get, "/admin/api/stats?" + bad, cookie)).StatusCode);
    }

    [Fact]
    public async Task Users_SearchSortPageAndNeverExposeMessageText()
    {
        await SeedUserAsync("sub-aaa", userTexts: ["SECRET-TEXT-ONE"]);
        await SeedUserAsync("sub-bbb", userTexts: ["x", "y"]);
        await SeedUserAsync("sub-ccc");
        await SeedUserAsync("other-sub", tenant: "other-tenant");
        var f = Factory();
        var cookie = await AdminCookieAsync(f);

        async Task<JsonElement> Get(string q) => await (await SendAsync(f, HttpMethod.Get, "/admin/api/users" + q, cookie)).Content.ReadFromJsonAsync<JsonElement>();
        static string[] Subs(JsonElement j) => j.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("sub").GetString()!).ToArray();

        var all = await Get("");
        Assert.Equal(4, all.GetProperty("total").GetInt32());
        Assert.Equal(25, all.GetProperty("pageSize").GetInt32());

        Assert.Equal(["sub-bbb"], Subs(await Get("?q=SUB-B")));                // subject, case-insensitive
        Assert.Equal(["sub-ccc"], Subs(await Get("?q=sub-c")));                // subject
        Assert.Empty(Subs(await Get("?q=%25")));                               // LIKE wildcards are literal
        Assert.Empty(Subs(await Get("?q=_")));
        Assert.Equal(["other-sub"], Subs(await Get("?tenant=other-tenant")));
        Assert.Equal(["other-sub", "sub-aaa", "sub-bbb", "sub-ccc"], Subs(await Get("?sort=sub&dir=asc")));
        Assert.Equal(["sub-ccc", "sub-bbb", "sub-aaa", "other-sub"], Subs(await Get("?sort=sub&dir=desc")));
        Assert.Equal(4, (await Get("?sort=conversations&dir=asc")).GetProperty("items").GetArrayLength());

        var page2 = await Get("?sort=sub&dir=asc&pageSize=2&page=2");
        Assert.Equal(["sub-bbb", "sub-ccc"], Subs(page2));
        Assert.Equal(4, page2.GetProperty("total").GetInt32());
        Assert.Equal(2, page2.GetProperty("page").GetInt32());

        var first = (await Get("?q=sub-aaa")).GetProperty("items")[0];
        Assert.False(first.TryGetProperty("email", out _)); // no email is kept
        Assert.Equal(3, first.GetProperty("signInCount").GetInt32());
        Assert.Equal(1, first.GetProperty("conversations").GetInt32());

        // Metadata only: no message text anywhere in the list, the detail or the statistics.
        foreach (var path in new[] { "/admin/api/users", "/admin/api/user?tenant=biatec-chat&sub=sub-aaa", "/admin/api/stats" })
            Assert.DoesNotContain("SECRET-TEXT-ONE", await (await SendAsync(f, HttpMethod.Get, path, cookie)).Content.ReadAsStringAsync());

        foreach (var bad in new[] { "?sort=password", "?dir=sideways", "?page=0", "?pageSize=101", "?pageSize=0", "?page=x" })
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(f, HttpMethod.Get, "/admin/api/users" + bad, cookie)).StatusCode);
    }

    [Fact]
    public async Task UserDetail_ShowsProfileAndConversationMetadata()
    {
        var (convId, _) = await SeedUserAsync("sub-1", userTexts: ["hello"]);
        var f = Factory();
        var cookie = await AdminCookieAsync(f);
        var j = await (await SendAsync(f, HttpMethod.Get, "/admin/api/user?tenant=biatec-chat&sub=sub-1", cookie)).Content.ReadFromJsonAsync<JsonElement>();
        var user = j.GetProperty("user");
        Assert.Equal("sub-1", user.GetProperty("sub").GetString());
        Assert.Equal(2, user.GetProperty("messages").GetInt64());
        Assert.Equal(1, user.GetProperty("conversations").GetInt32());
        var conv = Assert.Single(j.GetProperty("conversations").EnumerateArray());
        Assert.Equal(convId.ToString(), conv.GetProperty("id").GetString());
        Assert.Equal(2, conv.GetProperty("messages").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(f, HttpMethod.Get, "/admin/api/user?tenant=biatec-chat&sub=nobody", cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(f, HttpMethod.Get, "/admin/api/user?tenant=biatec-chat", cookie)).StatusCode);
    }

    // ---- impersonation ----------------------------------------------------------------------------------------------

    private async Task<List<JsonNode>> AdminChainAsync()
    {
        await using var db = NewContext();
        var rows = await db.EvidenceEntries.AsNoTracking().Where(e => e.ConversationId == AdminEvidence.ChainId).OrderBy(e => e.Seq).ToListAsync();
        return rows.Select(r => JsonNode.Parse(r.Payload!)!).ToList();
    }

    [Fact]
    public async Task Impersonation_NeedsAReasonAndAnExistingUser()
    {
        await SeedUserAsync("sub-1", userTexts: ["hello"]);
        var f = Factory();
        var cookie = await AdminCookieAsync(f);

        Task<HttpResponseMessage> Start(object body) => SendAsync(f, HttpMethod.Post, "/admin/api/impersonation", cookie, body);
        Assert.Equal(HttpStatusCode.BadRequest, (await Start(new { tenantId = "biatec-chat", sub = "sub-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Start(new { tenantId = "biatec-chat", sub = "sub-1", reason = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Start(new { tenantId = "biatec-chat", sub = "sub-1", reason = new string('x', 501) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Start(new { tenantId = "biatec-chat", sub = "sub-1", reason = "valid reason here", extra = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Start(new { tenantId = "biatec-chat", sub = "ghost", reason = "valid reason here" })).StatusCode);
        Assert.Single(await AdminChainAsync()); // only the sign-in: no refused attempt left an impersonation record
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(f, HttpMethod.Get, "/admin/api/view/conversations", cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(f, HttpMethod.Delete, "/admin/api/impersonation", cookie)).StatusCode);
    }

    [Fact]
    public async Task Impersonation_ViewsOnlyTheImpersonatedUserAndIsAuditedWithoutTheSubject()
    {
        var (convId, _) = await SeedUserAsync("sub-1", userTexts: ["Show my balance please"]);
        var (otherConv, _) = await SeedUserAsync("sub-2", userTexts: ["OTHER-USERS-WORDS"]);
        await using (var db = NewContext())
        {
            var p = new ActionPreview { Id = Guid.NewGuid(), ConversationId = convId, Capability = "wallet.transfer", CanonicalJson = """{"secret":"CANON"}""", ExpiresAt = _time.Now.AddMinutes(5), Status = ActionPreviewStatus.Completed, PredictedValueEur = 12.5m };
            db.ActionPreviews.Add(p);
            db.ActionSteps.Add(new ActionStep { Id = Guid.NewGuid(), PreviewId = p.Id, Seq = 1, Kind = ActionStepKind.Sign, SubjectHash = "h", Network = "algorand-mainnet", ExpiresAt = _time.Now.AddMinutes(5), Status = StepStatus.Confirmed, TxIds = ["TX1"], UnsignedGroup = """{"raw":"GROUP"}""" });
            await db.SaveChangesAsync();
        }
        var f = Factory();
        var cookie = await AdminCookieAsync(f);

        var start = await SendAsync(f, HttpMethod.Post, "/admin/api/impersonation", cookie, new { tenantId = "biatec-chat", sub = "sub-1", reason = "  Support ticket 4711  " });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var imp = await start.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Support ticket 4711", imp.GetProperty("reason").GetString());
        Assert.Equal(_time.Now.AddMinutes(30), imp.GetProperty("expiresAt").GetDateTimeOffset());

        var me = await (await SendAsync(f, HttpMethod.Get, "/admin/api/me", cookie)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sub-1", me.GetProperty("impersonation").GetProperty("sub").GetString());

        var list = await (await SendAsync(f, HttpMethod.Get, "/admin/api/view/conversations", cookie)).Content.ReadFromJsonAsync<JsonElement>();
        var only = Assert.Single(list.GetProperty("conversations").EnumerateArray());
        Assert.Equal(convId.ToString(), only.GetProperty("id").GetString());

        var tr = await SendAsync(f, HttpMethod.Get, $"/admin/api/view/conversations/{convId}/messages", cookie);
        var body = await tr.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, tr.StatusCode);
        var j = JsonDocument.Parse(body).RootElement;
        var messages = j.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("Show my balance please", messages[0].GetProperty("content").GetString());
        Assert.Equal("User", messages[0].GetProperty("role").GetString());
        Assert.Equal(["wallet.balance"], messages[1].GetProperty("toolNames").EnumerateArray().Select(x => x.GetString()).ToArray());
        var preview = Assert.Single(j.GetProperty("previews").EnumerateArray());
        Assert.Equal("Completed", preview.GetProperty("status").GetString());
        Assert.Equal("TX1", preview.GetProperty("steps")[0].GetProperty("txIds")[0].GetString());
        // Raw tool arguments, canonical payloads and unsigned groups are never exposed.
        foreach (var hidden in new[] { "RAW-INPUT", "CANON", "GROUP" }) Assert.DoesNotContain(hidden, body);

        // Another user's conversation is a plain 404, however the id was found.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(f, HttpMethod.Get, $"/admin/api/view/conversations/{otherConv}/messages", cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(f, HttpMethod.Get, $"/admin/api/view/conversations/{Guid.NewGuid()}/messages", cookie)).StatusCode);

        // Stop: the view is closed again.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f, HttpMethod.Delete, "/admin/api/impersonation", cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(f, HttpMethod.Get, $"/admin/api/view/conversations/{convId}/messages", cookie)).StatusCode);

        // The chain: sign-in, start, one conversation viewed, stop. The user is named by a hash, never by the subject.
        var chain = await AdminChainAsync();
        Assert.Equal([AdminEvidence.DashboardSignIn, AdminEvidence.ImpersonationStart, AdminEvidence.ImpersonationViewConversation, AdminEvidence.ImpersonationStop],
            chain.Select(c => c["action"]!.GetValue<string>()).ToArray());
        Assert.All(chain, c => Assert.Equal("alice@example.test", c["operator"]!.GetValue<string>()));
        var startEntry = chain[1];
        Assert.Equal("Support ticket 4711", startEntry["reason"]!.GetValue<string>());
        await using (var db = NewContext())
        {
            var userId = (await db.KnownUsers.SingleAsync(u => u.OidcSub == "sub-1")).Id;
            Assert.Equal($"user:biatec-chat:{userId:N}", startEntry["target"]!.GetValue<string>());
        }
        Assert.Equal(convId.ToString(), chain[2]["after"]!["conversationId"]!.GetValue<string>());
        Assert.Equal("stopped", chain[3]["after"]!["how"]!.GetValue<string>());
        var raw = string.Join("\n", chain.Select(c => c.ToJsonString()));
        Assert.DoesNotContain("sub-1", raw);
        Assert.DoesNotContain("OTHER-USERS-WORDS", body);
    }

    [Fact]
    public async Task Impersonation_EndsByTimeReplacementAndLogout()
    {
        await SeedUserAsync("sub-1", userTexts: ["hello"]);
        await SeedUserAsync("sub-2", userTexts: ["world"]);
        var f = Factory();
        var cookie = await AdminCookieAsync(f);
        Task<HttpResponseMessage> Start(string sub) => SendAsync(f, HttpMethod.Post, "/admin/api/impersonation", cookie, new { tenantId = "biatec-chat", sub, reason = "audit review 1" });

        Assert.Equal(HttpStatusCode.OK, (await Start("sub-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Start("sub-2")).StatusCode);   // replaces the first
        _time.Now = _time.Now.AddMinutes(31);
        var me = await (await SendAsync(f, HttpMethod.Get, "/admin/api/me", cookie)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, me.GetProperty("impersonation").ValueKind);             // expired
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(f, HttpMethod.Get, "/admin/api/view/conversations", cookie)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Start("sub-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f, HttpMethod.Post, "/admin/api/logout", cookie)).StatusCode);

        var hows = (await AdminChainAsync()).Where(c => c["action"]!.GetValue<string>() == AdminEvidence.ImpersonationStop).Select(c => c["after"]!["how"]!.GetValue<string>()).ToArray();
        Assert.Equal(["replaced", "logout"], hows); // running out is not an entry: the start entry holds expiresAt
        Assert.Equal(AdminEvidence.DashboardSignOut, (await AdminChainAsync()).Last()["action"]!.GetValue<string>());
    }

    [Fact]
    public async Task Impersonation_NeverOutlivesTheAdminSession()
    {
        await SeedUserAsync("sub-1", userTexts: ["hello"]);
        var f = Factory();
        var cookie = await AdminCookieAsync(f);
        _time.Now = _time.Now.AddMinutes(7 * 60 + 50); // 10 minutes before the 8 hour session ends
        var imp = await (await SendAsync(f, HttpMethod.Post, "/admin/api/impersonation", cookie, new { tenantId = "biatec-chat", sub = "sub-1", reason = "end of session" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_time.Now.AddMinutes(10), imp.GetProperty("expiresAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Impersonation_HasNoWritePathAndRequiresAnAdmin()
    {
        await SeedUserAsync("sub-1", userTexts: ["hello"]);
        var f = Factory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(f, HttpMethod.Post, "/admin/api/impersonation", null, new { tenantId = "biatec-chat", sub = "sub-1", reason = "no session here" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(f, HttpMethod.Get, "/admin/api/view/conversations")).StatusCode);

        var cookie = await AdminCookieAsync(f);
        await SendAsync(f, HttpMethod.Post, "/admin/api/impersonation", cookie, new { tenantId = "biatec-chat", sub = "sub-1", reason = "checking write paths" });
        // Whatever the view is asked to do beyond reading, there is nothing there.
        foreach (var m in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
            Assert.True((await SendAsync(f, m, "/admin/api/view/conversations", cookie)).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        foreach (var path in new[] { "/admin/api/view/chat", "/admin/api/view/confirm", "/admin/api/view/sign" })
            Assert.True((await SendAsync(f, HttpMethod.Post, path, cookie)).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }

    // ---- the static app ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ui_ServesTheBuiltAppWithStrictHeaders()
    {
        var f = Factory();
        var redirect = await SendAsync(f, HttpMethod.Get, "/admin");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/admin/", redirect.Headers.Location!.ToString());

        var index = await SendAsync(f, HttpMethod.Get, "/admin/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("text/html", index.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", index.Headers.CacheControl?.ToString());
        var csp = string.Join(";", index.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("nosniff", index.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", index.Headers.GetValues("Referrer-Policy").Single());

        var asset = await SendAsync(f, HttpMethod.Get, "/admin/assets/app.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Contains("immutable", asset.Headers.CacheControl!.ToString());
        Assert.Contains("javascript", asset.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData("/admin/missing.js")]
    [InlineData("/admin/%2e%2e/%2e%2e/appsettings.json")]
    [InlineData("/admin/..%5c..%5cappsettings.json")]
    [InlineData("/admin/assets/")]
    [InlineData("/admin/api/nothing-here")]
    [InlineData("/admin/auth/other")]
    public async Task Ui_UnknownAndTraversingPathsAreNotFound(string path)
    {
        var f = Factory();
        var r = await SendAsync(f, HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    // ---- user bookkeeping -------------------------------------------------------------------------------------------

    [Fact]
    public async Task KnownUsers_SignInCreatesThenBumpsTheRow()
    {
        await using var db = NewContext();
        var t0 = _time.Now;
        await KnownUsers.RecordSignInAsync(db, "biatec-chat", new IdTokenIdentity("sub-9", "nine@example.test"), t0, NullLogger.Instance, default);
        await KnownUsers.RecordSignInAsync(db, "biatec-chat", new IdTokenIdentity("sub-9", null), t0.AddHours(1), NullLogger.Instance, default);
        await KnownUsers.RecordSignInAsync(db, "other", new IdTokenIdentity("sub-9", "x@example.test"), t0, NullLogger.Instance, default);
        var u = await db.KnownUsers.AsNoTracking().SingleAsync(x => x.TenantId == "biatec-chat" && x.OidcSub == "sub-9");
        Assert.Equal(2, u.SignInCount);
        Assert.Equal(t0, u.FirstSeenAt);
        Assert.Equal(t0.AddHours(1), u.LastSeenAt);
        Assert.Equal(2, await db.KnownUsers.CountAsync());
    }

    [Fact]
    public async Task ErasingAUser_RemovesTheDashboardRow()
    {
        await SeedUserAsync("sub-1", userTexts: ["hello"]);
        await SeedUserAsync("sub-2", userTexts: ["hello"]);
        var f = Factory();
        using (var scope = f.Services.CreateScope())
        {
            var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
            Assert.Equal(ErasureOutcome.Erased, (await retention.EraseAsync("biatec-chat", "sub-1", null, default)).Outcome);
        }
        await using var db = NewContext();
        Assert.Equal(["sub-2"], await db.KnownUsers.Select(u => u.OidcSub).ToListAsync());

        // A user who never had a conversation is removed too.
        db.KnownUsers.Add(new KnownUser { Id = Guid.NewGuid(), TenantId = "biatec-chat", OidcSub = "sub-3", FirstSeenAt = _time.Now, LastSeenAt = _time.Now, SignInCount = 1 });
        await db.SaveChangesAsync();
        using var scope2 = f.Services.CreateScope();
        await scope2.ServiceProvider.GetRequiredService<RetentionService>().EraseAsync("biatec-chat", "sub-3", null, default);
        Assert.False(await db.KnownUsers.AnyAsync(u => u.OidcSub == "sub-3"));
    }

    [Fact]
    public async Task Retention_RemovesDashboardRowsOfUsersWithNothingLeftPastTheRecordWindow()
    {
        await SeedUserAsync("sub-keep", userTexts: ["hello"]);
        await using (var db = NewContext())
        {
            var old = _time.Now.AddYears(-8);
            db.KnownUsers.Add(new KnownUser { Id = Guid.NewGuid(), TenantId = "biatec-chat", OidcSub = "sub-old", FirstSeenAt = old, LastSeenAt = old, SignInCount = 1 });
            // Old, but still has a conversation: stays until that conversation goes.
            db.KnownUsers.Add(new KnownUser { Id = Guid.NewGuid(), TenantId = "biatec-chat", OidcSub = "sub-old-with-conversation", FirstSeenAt = old, LastSeenAt = old, SignInCount = 1 });
            db.Conversations.Add(new Conversation { Id = Guid.NewGuid(), TenantId = "biatec-chat", OidcSub = "sub-old-with-conversation", CreatedAt = _time.Now, LastMessageAt = _time.Now });
            await db.SaveChangesAsync();
        }
        using (var scope = Factory().Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RetentionService>().ApplyAsync(default);
        await using var check = NewContext();
        Assert.Equal(["sub-keep", "sub-old-with-conversation"], await check.KnownUsers.OrderBy(u => u.OidcSub).Select(u => u.OidcSub).ToListAsync());
    }

    [Fact]
    public async Task Users_PagingIsStableWhenSortKeysTie()
    {
        // Same subject in two tenants and identical first-seen times: the order must still be total.
        await using (var db = NewContext())
        {
            for (var i = 0; i < 6; i++)
                db.KnownUsers.Add(new KnownUser { Id = Guid.NewGuid(), TenantId = i % 2 == 0 ? "biatec-chat" : "other", OidcSub = "same-" + (i / 2), FirstSeenAt = _time.Now, LastSeenAt = _time.Now, SignInCount = 1 });
            await db.SaveChangesAsync();
        }
        var f = Factory();
        var cookie = await AdminCookieAsync(f);
        foreach (var sort in new[] { "firstSeen", "sub", "lastActivity", "conversations" })
        {
            var seen = new List<string>();
            for (var page = 1; page <= 3; page++)
            {
                var j = await (await SendAsync(f, HttpMethod.Get, $"/admin/api/users?sort={sort}&dir=asc&pageSize=2&page={page}", cookie)).Content.ReadFromJsonAsync<JsonElement>();
                seen.AddRange(j.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("tenantId").GetString() + "/" + i.GetProperty("sub").GetString()));
            }
            Assert.Equal(6, seen.Distinct().Count());
        }
    }

    [Fact]
    public async Task Transcript_OfALongConversationIsTheNewestMessagesInOrder()
    {
        var (convId, _) = await SeedUserAsync("sub-long");
        await using (var db = NewContext())
        {
            for (var i = 1; i <= 505; i++)
                db.Messages.Add(new Message { ConversationId = convId, Seq = i, Role = MessageRole.User, Content = "m" + i, CreatedAt = _time.Now });
            await db.SaveChangesAsync();
        }
        var f = Factory();
        var cookie = await AdminCookieAsync(f);
        await SendAsync(f, HttpMethod.Post, "/admin/api/impersonation", cookie, new { tenantId = "biatec-chat", sub = "sub-long", reason = "long conversation" });
        var j = await (await SendAsync(f, HttpMethod.Get, $"/admin/api/view/conversations/{convId}/messages", cookie)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(j.GetProperty("truncated").GetBoolean());
        var seqs = j.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("seq").GetInt64()).ToList();
        Assert.Equal(500, seqs.Count);
        Assert.Equal(6, seqs[0]);
        Assert.Equal(505, seqs[^1]);
        Assert.Equal(seqs.OrderBy(x => x), seqs);
    }

    [Fact]
    public async Task Stats_UnknownTenantValuesAreNotCachedAndOverlongOnesRefused()
    {
        var f = Factory();
        var cookie = await AdminCookieAsync(f);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(f, HttpMethod.Get, "/admin/api/stats?tenant=nobody", cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(f, HttpMethod.Get, "/admin/api/stats?tenant=" + new string('x', 129), cookie)).StatusCode);
        using var scope = f.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        Assert.False(cache.TryGetValue("admin-stats|30|nobody", out _));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(f, HttpMethod.Get, "/admin/api/stats?tenant=biatec-chat", cookie)).StatusCode);
        Assert.True(cache.TryGetValue("admin-stats|30|biatec-chat", out _));
    }

    [Fact]
    public async Task SignIn_UnverifiedEmailNeverRevealsWhoIsAnAdmin()
    {
        var f = Factory();
        var admin = await SignInAsync(f, "alice@example.test", verified: false, explicitVerified: true);
        var stranger = await SignInAsync(f, "mallory@example.test", verified: false, explicitVerified: true);
        Assert.Equal(admin.Headers.Location, stranger.Headers.Location);
        Assert.EndsWith("error=email_not_verified", stranger.Headers.Location!.ToString());
    }

    [Fact]
    public async Task SignIn_AStatePresentedWithoutTheLoginCookieIsNotBurned()
    {
        var f = Factory();
        var start = await SendAsync(f, HttpMethod.Get, "/admin/auth/login", tweak: r => r.Headers.Remove("Sec-Fetch-Site"));
        var (code, state) = _oidc.Authorize(start.Headers.Location!, "openid profile email");
        _oidc.EmailVerified = true;
        var url = $"/auth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}";
        // Someone who saw the URL in a log opens it elsewhere, without the browser's login cookie.
        Assert.EndsWith("error=login_failed", (await SendAsync(f, HttpMethod.Get, url, sameOrigin: false)).Headers.Location!.ToString());
        Assert.Empty(_oidc.TokenRequests);
        // The real admin's sign-in still works.
        Assert.NotNull(SetCookie(await SendAsync(f, HttpMethod.Get, url, CookieHeader(SetCookie(start, AdminAuth.LoginCookieName)!), sameOrigin: false), AdminAuth.CookieName));
    }

    [Fact]
    public async Task Me_NeverExposesTheInternalUserId()
    {
        await SeedUserAsync("sub-1", userTexts: ["hello"]);
        var f = Factory();
        var cookie = await AdminCookieAsync(f);
        var start = await (await SendAsync(f, HttpMethod.Post, "/admin/api/impersonation", cookie, new { tenantId = "biatec-chat", sub = "sub-1", reason = "case 4711" })).Content.ReadAsStringAsync();
        var me = await (await SendAsync(f, HttpMethod.Get, "/admin/api/me", cookie)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("userId", start, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userId", me, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ui_AnswersHeadAndDeadCookiesAreCleared()
    {
        var f = Factory();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(f, HttpMethod.Head, "/admin/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(f, HttpMethod.Head, "/admin/assets/app.js")).StatusCode);
        // A cookie for a session that is gone (idle timeout) is answered with a 401 that also expires the cookie.
        var r = await SendAsync(f, HttpMethod.Get, "/admin/api/me", AdminAuth.CookieName + "=" + new string('a', 43));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains(r.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AdminAuth.CookieName + "=;"));
    }

    [Fact]
    public async Task Queries_RepeatedParametersAreRefusedNotReadAsNoFilter()
    {
        var f = Factory();
        var cookie = await AdminCookieAsync(f);
        foreach (var path in new[] { "/admin/api/stats?tenant=a&tenant=b", "/admin/api/users?tenant=a&tenant=b", "/admin/api/user?tenant=a&tenant=b&sub=x" })
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(f, HttpMethod.Get, path, cookie)).StatusCode);
    }

    [Fact]
    public async Task SignIn_AGoneStateLandsOnTheDashboard()
    {
        var f = Factory();
        var start = await SendAsync(f, HttpMethod.Get, "/admin/auth/login", tweak: r => r.Headers.Remove("Sec-Fetch-Site"));

        // A state that is gone (waited too long) ends on the dashboard's message, not on the chat popup page.
        var gone = await SendAsync(f, HttpMethod.Get, "/auth/callback?code=x&state=" + new string('z', 43), CookieHeader(SetCookie(start, AdminAuth.LoginCookieName)!), sameOrigin: false);
        Assert.Equal(HttpStatusCode.Redirect, gone.StatusCode);
        Assert.EndsWith("?error=login_failed", gone.Headers.Location!.ToString());
        var denied = await SendAsync(f, HttpMethod.Get, "/auth/callback?error=access_denied", CookieHeader(SetCookie(start, AdminAuth.LoginCookieName)!), sameOrigin: false);
        Assert.EndsWith("?error=access_denied", denied.Headers.Location!.ToString());
    }

    [Fact]
    public void Reason_DropsControlFormatAndSeparatorCharacters()
    {
        Assert.Equal("case 4711 ok", AdminImpersonations.CleanReason("  case 4711 \u202E\u2028ok  "));
        Assert.Equal("ticket4711", AdminImpersonations.CleanReason("ticket\u200B4711"));
        Assert.Null(AdminImpersonations.CleanReason("ab\u202E\u200B\u2029cd")); // four visible characters are not enough
        Assert.Equal("ticket 1234", AdminImpersonations.CleanReason("ticket 1234" + char.ConvertFromUtf32(0xE0041) + char.ConvertFromUtf32(0xE0001))); // invisible tag characters
    }
}
