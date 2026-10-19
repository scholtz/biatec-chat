using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Biatec.Chat.Api.Auth;
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

/// <summary>Needs the docker-compose postgres. The OIDC server is the in-process <see cref="MockOidc"/>; vault and pending state are in memory.</summary>
public sealed class AuthTests : IAsyncLifetime
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
    private readonly List<string> _browserBodies = [];
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
            db.Tenants.Add(TenantSeed.BiatecChat()); // the refresher reads the tenant's client from the table
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
                ["Evidence:AllowEphemeralKeys"] = "true",
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

    private async Task<HttpResponseMessage> SendAsync(HttpMethod m, string pathAndQuery, string? cookie = null, Action<HttpRequestMessage>? tweak = null)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var r = new HttpRequestMessage(m, "http://" + Host + pathAndQuery);
        if (m != HttpMethod.Get)
        {
            r.Headers.TryAddWithoutValidation("Origin", Origin);
            r.Headers.TryAddWithoutValidation("X-Biatec-Chat", "1");
            r.Headers.TryAddWithoutValidation("X-Biatec-Client-Key", "biatec-web");
        }
        if (cookie is not null) r.Headers.TryAddWithoutValidation("Cookie", $"{SessionPolicy.CookieName}={cookie}");
        tweak?.Invoke(r);
        var res = await client.SendAsync(r);
        _browserBodies.Add(await res.Content.ReadAsStringAsync());
        _browserBodies.Add(res.Headers.ToString());
        return res;
    }

    private static string? SetCookie(HttpResponseMessage r) =>
        r.Headers.TryGetValues("Set-Cookie", out var v) ? v.SingleOrDefault(x => x.StartsWith(SessionPolicy.CookieName + "=")) : null;

    private static string TokenOf(string setCookie) => setCookie[(SessionPolicy.CookieName.Length + 1)..].Split(';')[0];

    private async Task<string> NewSessionAsync() => TokenOf(SetCookie(await SendAsync(HttpMethod.Get, "/session/probe"))!);

    private async Task<(Guid Id, string LoginPath)> StartPopupAsync(string cookie)
    {
        var r = await SendAsync(HttpMethod.Post, "/auth/popup", cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        var url = new Uri(j.GetProperty("loginUrl").GetString()!);
        Assert.Equal("https://chat.biatec.io", url.GetLeftPart(UriPartial.Authority));
        return (Guid.Parse(j.GetProperty("authRequestId").GetString()!), url.PathAndQuery);
    }

    /// <summary>Opens the login URL in the popup and plays the user at Biatec OIDC. Returns the callback path.</summary>
    private async Task<string> LoginToOidcAsync(string cookie, string loginPath)
    {
        var r = await SendAsync(HttpMethod.Get, loginPath, cookie);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var (code, state) = _oidc.Authorize(r.Headers.Location!);
        return $"/auth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}";
    }

    private async Task<PopupAuthStatus> PopupStatusAsync(Guid id)
    {
        await using var db = NewContext();
        return (await db.PopupAuthRequests.SingleAsync(p => p.Id == id)).Status;
    }

    private void AssertNoSecretsInBrowser()
    {
        var all = string.Join("\n", _browserBodies);
        foreach (var secret in new[] { _oidc.AccessToken, _oidc.RefreshToken, "ACCESS-TOKEN-SECRET", "REFRESH-TOKEN-SECRET", "code-", MockOidc.ClientSecret, "eyJ" })
            Assert.DoesNotContain(secret, all);
    }

    [Fact]
    public async Task FullFlow_LogsInRotatesSessionAndVaultsTokens()
    {
        var oldCookie = await NewSessionAsync();
        var (reqId, loginPath) = await StartPopupAsync(oldCookie);
        Assert.Equal(PopupAuthStatus.Pending, await PopupStatusAsync(reqId));

        var callback = await LoginToOidcAsync(oldCookie, loginPath);
        var cb = await SendAsync(HttpMethod.Get, callback, oldCookie);
        Assert.Equal(HttpStatusCode.OK, cb.StatusCode);
        var body = await cb.Content.ReadAsStringAsync();
        Assert.Contains("window.close()", body);
        Assert.Equal("no-store", cb.Headers.CacheControl?.ToString());
        Assert.Contains("no-referrer", cb.Headers.GetValues("Referrer-Policy"));

        // New session id and cookie; the old id is dead.
        var newCookie = TokenOf(SetCookie(cb)!);
        Assert.NotEqual(oldCookie, newCookie);
        var oldId = SessionService.IdFromToken(oldCookie);
        var newId = SessionService.IdFromToken(newCookie);
        await using (var db = NewContext())
        {
            Assert.False(await db.ChatSessions.AnyAsync(s => s.Id == oldId));
            var s = await db.ChatSessions.SingleAsync(x => x.Id == newId);
            Assert.Equal(Biatec.Chat.Core.Data.SessionTier.Authenticated, s.Tier);
            Assert.Equal("user-123", s.OidcSub);

            // The tier change is evidence (T-035): from/to, no addresses.
            var tier = await db.EvidenceEntries.AsNoTracking().Where(e => e.ConversationId == s.ConversationId && e.Kind == "tier_changed").SingleAsync();
            var payload = System.Text.Json.Nodes.JsonNode.Parse(tier.Payload!)!;
            Assert.Equal("Anonymous", payload["from"]!.GetValue<string>());
            Assert.Equal("Authenticated", payload["to"]!.GetValue<string>());
            Assert.Equal("sign_in", payload["reason"]!.GetValue<string>());
            Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"(\d{1,3}\.){3}\d{1,3}"), tier.Payload!);
        }
        Assert.Equal(PopupAuthStatus.Completed, await PopupStatusAsync(reqId));

        // Tokens only in the vault, under the new id.
        Assert.Single(_vault.Items);
        var entry = _vault.Items[newId.ToString("N")];
        Assert.Equal("ACCESS-TOKEN-SECRET", entry.AccessToken);
        Assert.Equal("REFRESH-TOKEN-SECRET", entry.RefreshToken);
        Assert.False(_vault.Items.ContainsKey(oldId.ToString("N")));

        // The old cookie no longer resolves; the new one reports an authenticated session.
        var stale = await SendAsync(HttpMethod.Get, "/session", oldCookie);
        Assert.NotNull(SetCookie(stale)); // old value is not live, a fresh anonymous session is issued
        var state = await (await SendAsync(HttpMethod.Get, "/session", newCookie)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authenticated", state.GetProperty("tier").GetString(), ignoreCase: true);

        // The widget's status poll.
        var poll = await SendAsync(HttpMethod.Get, $"/auth/popup/{reqId}", newCookie);
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        Assert.Equal("complete", (await poll.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString(), ignoreCase: true);

        // Code exchange carried the secret, the verifier and the resource (the mock enforces them).
        var tokenReq = Assert.Single(_oidc.TokenRequests);
        Assert.Equal("authorization_code", tokenReq["grant_type"]);
        Assert.Equal(MockOidc.Resource, tokenReq["resource"]);
        AssertNoSecretsInBrowser();
    }

    [Fact]
    public async Task LoginCsrf_StateFromAnotherSessionIsRejected()
    {
        var attacker = await NewSessionAsync();
        var victim = await NewSessionAsync();
        var (_, attackerLogin) = await StartPopupAsync(attacker);
        var attackerCallback = await LoginToOidcAsync(attacker, attackerLogin); // attacker's own code and state

        // The attacker gets the victim's browser to load the attacker's callback URL.
        var r = await SendAsync(HttpMethod.Get, attackerCallback, victim);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(SetCookie(r));
        Assert.Empty(_vault.Items);
        Assert.Empty(_oidc.TokenRequests); // the code was never even exchanged
        await using var db = NewContext();
        Assert.All(await db.ChatSessions.ToListAsync(), s => Assert.Equal(Biatec.Chat.Core.Data.SessionTier.Anonymous, s.Tier));
        // A callback with no session cookie at all is rejected the same way.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, attackerCallback)).StatusCode);
        AssertNoSecretsInBrowser();
    }

    [Fact]
    public async Task State_IsSingleUse()
    {
        var cookie = await NewSessionAsync();
        var (_, login) = await StartPopupAsync(cookie);
        var callback = await LoginToOidcAsync(cookie, login);
        var first = await SendAsync(HttpMethod.Get, callback, cookie);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var replay = await SendAsync(HttpMethod.Get, callback, TokenOf(SetCookie(first)!));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Single(_oidc.TokenRequests);
    }

    [Fact]
    public async Task PopupNonce_IsRequiredBoundToTheSessionAndOneTime()
    {
        var cookie = await NewSessionAsync();
        var other = await NewSessionAsync();
        var (_, login) = await StartPopupAsync(cookie);
        var nonce = System.Web.HttpUtility.ParseQueryString(new Uri("https://x" + login).Query)["nonce"]!;
        var req = System.Web.HttpUtility.ParseQueryString(new Uri("https://x" + login).Query)["req"]!;

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, $"/auth/login?req={req}", cookie)).StatusCode);          // no nonce
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, $"/auth/login?req={req}&nonce=wrong", cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, login, other)).StatusCode);                             // another session's popup
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, login)).StatusCode);                                    // no cookie
        // A cross-site navigation and an iframe load are refused without burning the nonce.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, login, cookie, r => r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, login, cookie, r => r.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "iframe"))).StatusCode);

        var ok = await SendAsync(HttpMethod.Get, login, cookie, r =>
        {
            r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
            r.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
            r.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        });
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal(MockOidc.Issuer, ok.Headers.Location!.GetLeftPart(UriPartial.Authority)); // only ever the discovered endpoint
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, login, cookie)).StatusCode);                             // replay
    }

    [Fact]
    public async Task ExpiredPopupRequest_CannotStartLogin_AndStatusReportsFailed()
    {
        var cookie = await NewSessionAsync();
        var (id, login) = await StartPopupAsync(cookie);
        _time.Now = _time.Now.AddMinutes(6);
        var s = await SendAsync(HttpMethod.Get, "/session/probe", cookie); // keep the session itself alive (idle 15 min)
        Assert.Equal(HttpStatusCode.OK, s.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, login, cookie)).StatusCode);
        var poll = await SendAsync(HttpMethod.Get, $"/auth/popup/{id}", cookie);
        Assert.Equal("failed", (await poll.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString(), ignoreCase: true);
    }

    [Fact]
    public async Task PopupStatus_IsOnlyVisibleToItsOwnConversation()
    {
        var cookie = await NewSessionAsync();
        var other = await NewSessionAsync();
        var (id, _) = await StartPopupAsync(cookie);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/auth/popup/{id}", other)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, $"/auth/popup/{id}")).StatusCode);
    }

    [Fact]
    public async Task PopupStart_NeedsTheCsrfHeader()
    {
        var cookie = await NewSessionAsync();
        var r = await SendAsync(HttpMethod.Post, "/auth/popup", cookie, req => req.Headers.Remove("X-Biatec-Chat"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    public static TheoryData<string> BadIdTokens => ["issuer", "audience", "nonce", "expired", "signature"];

    [Theory]
    [MemberData(nameof(BadIdTokens))]
    public async Task InvalidIdToken_FailsTheLoginAndLeavesSessionAnonymous(string defect)
    {
        switch (defect)
        {
            case "issuer": _oidc.IdTokenIssuer = "https://evil.example.test"; break;
            case "audience": _oidc.IdTokenAudience = "someone-else"; break;
            case "nonce": _oidc.IdTokenNonceOverride = "attacker-nonce"; break;
            case "expired": _oidc.IdTokenExpired = true; break;
            case "signature": _oidc.IdTokenSignedWithOtherKey = true; break;
        }
        var cookie = await NewSessionAsync();
        var (id, login) = await StartPopupAsync(cookie);
        var r = await SendAsync(HttpMethod.Get, await LoginToOidcAsync(cookie, login), cookie);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(SetCookie(r));
        Assert.Equal(PopupAuthStatus.Failed, await PopupStatusAsync(id));
        Assert.Empty(_vault.Items);
        await using var db = NewContext();
        var s = await db.ChatSessions.SingleAsync(); // not rotated, not upgraded
        Assert.Equal(SessionService.IdFromToken(cookie), s.Id);
        Assert.Equal(Biatec.Chat.Core.Data.SessionTier.Anonymous, s.Tier);
        AssertNoSecretsInBrowser();
    }

    [Fact]
    public async Task ErrorFromTheProvider_FailsThePopup()
    {
        var cookie = await NewSessionAsync();
        var (id, login) = await StartPopupAsync(cookie);
        var (_, state) = _oidc.Authorize((await SendAsync(HttpMethod.Get, login, cookie)).Headers.Location!);
        var r = await SendAsync(HttpMethod.Get, $"/auth/callback?error=access_denied&state={Uri.EscapeDataString(state)}", cookie);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(PopupAuthStatus.Failed, await PopupStatusAsync(id));
        Assert.Empty(_oidc.TokenRequests);
    }

    [Fact]
    public async Task CallbackNamingAnotherIssuer_IsNotExchanged()
    {
        var cookie = await NewSessionAsync();
        var (id, login) = await StartPopupAsync(cookie);
        var callback = await LoginToOidcAsync(cookie, login);
        var r = await SendAsync(HttpMethod.Get, callback + "&iss=" + Uri.EscapeDataString("https://evil.example.test"), cookie);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(PopupAuthStatus.Failed, await PopupStatusAsync(id));
        Assert.Empty(_oidc.TokenRequests);
    }

    [Fact]
    public async Task Relogin_MovesTheVaultEntryAndInvalidatesTheOldOne()
    {
        var c1 = await NewSessionAsync();
        var (_, l1) = await StartPopupAsync(c1);
        var cb1 = await SendAsync(HttpMethod.Get, await LoginToOidcAsync(c1, l1), c1);
        var c2 = TokenOf(SetCookie(cb1)!);
        var (_, l2) = await StartPopupAsync(c2);
        var cb2 = await SendAsync(HttpMethod.Get, await LoginToOidcAsync(c2, l2), c2);
        var c3 = TokenOf(SetCookie(cb2)!);
        Assert.Equal(SessionService.IdFromToken(c3).ToString("N"), Assert.Single(_vault.Items).Key);
    }

    [Fact]
    public async Task Refresher_UsesTheRefreshGrantWithTheTenantClient()
    {
        var cookie = await NewSessionAsync();
        var (_, login) = await StartPopupAsync(cookie);
        var cb = await SendAsync(HttpMethod.Get, await LoginToOidcAsync(cookie, login), cookie);
        var sessionId = SessionService.IdFromToken(TokenOf(SetCookie(cb)!)).ToString("N");

        var refresher = _factory.Services.GetRequiredService<ITokenRefresher>();
        var r = await refresher.RefreshAsync(sessionId, "REFRESH-TOKEN-SECRET", CancellationToken.None);
        Assert.Equal("ACCESS-TOKEN-SECRET-1", r.AccessToken);
        Assert.Equal("REFRESH-TOKEN-SECRET-1", r.RefreshToken);
        Assert.Equal(_time.Now.AddSeconds(900), r.AccessExpiresAt);
        var req = _oidc.TokenRequests.Last();
        Assert.Equal("refresh_token", req["grant_type"]);
        Assert.Equal(MockOidc.Resource, req["resource"]);
        await Assert.ThrowsAsync<OidcException>(() => refresher.RefreshAsync(Guid.NewGuid().ToString("N"), "x", CancellationToken.None));
    }
}
