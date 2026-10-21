using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Sessions;
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
using Npgsql;

namespace Biatec.Chat.Tests.Hosting;

/// <summary>
/// The whole sign-in through a mounted host, the way the browser on https://beta.dex.biatec.io sees it: every request
/// goes to /chat/..., the URLs the backend hands out carry the prefix, the OIDC redirect URI is the prefixed callback,
/// and the host-only __Host- cookie works. Needs the docker-compose postgres; the OIDC server is the in-process
/// <see cref="MockOidc"/>.
/// </summary>
public sealed class MountedLoginTests : IAsyncLifetime
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

    private const string Host = "beta.dex.biatec.io";
    private const string Origin = "https://beta.dex.biatec.io";
    private const string Prefix = "/chat";
    private const string ClientKey = "biatec-dex";

    private static Tenant MountedTenant() => new()
    {
        Id = "mount-beta-dex-biatec-io",
        Host = Host,
        SignInPageUrl = Origin,
        OidcClientId = MockOidc.ClientId,
        ClientSecretRef = "secret://biatec-chat/oidc-client-secret",
        RedirectUri = Origin + Prefix + "/auth/callback",
        AllowedOrigins = [Origin],
        ClientKeys = "[\"" + ClientKey + "\"]",
        ConsentThresholdCeilingEur = 1000,
        SoftCapEurPerHour = 1500,
        TrustAnchorVerified = true,
    };

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
            db.Tenants.Add(MountedTenant());
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
                ["Hosting:Mounts:0:Host"] = Host,
                ["Hosting:Mounts:0:Prefix"] = Prefix,
                ["Hosting:Mounts:0:ClientKey"] = ClientKey,
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ITenantStore>();
                s.AddSingleton<ITenantStore>(new MemoryTenantStore(MountedTenant()));
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

    /// <summary>A browser request to the page's own origin: always under the prefix, same-origin Origin header on non-GET.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod m, string pathUnderPrefix, string? cookie = null)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var r = new HttpRequestMessage(m, "http://" + Host + Prefix + pathUnderPrefix);
        if (m != HttpMethod.Get)
        {
            r.Headers.TryAddWithoutValidation("Origin", Origin);
            r.Headers.TryAddWithoutValidation("X-Biatec-Chat", "1");
            r.Headers.TryAddWithoutValidation("X-Biatec-Client-Key", ClientKey);
        }
        if (cookie is not null) r.Headers.TryAddWithoutValidation("Cookie", $"{SessionPolicy.CookieName}={cookie}");
        return await client.SendAsync(r);
    }

    private static string? SetCookie(HttpResponseMessage r) =>
        r.Headers.TryGetValues("Set-Cookie", out var v) ? v.SingleOrDefault(x => x.StartsWith(SessionPolicy.CookieName + "=")) : null;

    private static string TokenOf(string setCookie) => setCookie[(SessionPolicy.CookieName.Length + 1)..].Split(';')[0];

    [Fact]
    public async Task SessionCookie_IsHostOnlyAndSecure_AndWorksUnderThePrefix()
    {
        var probe = await SendAsync(HttpMethod.Get, "/session/probe");

        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        var setCookie = SetCookie(probe)!;
        Assert.NotNull(setCookie);
        // __Host- needs Secure, Path=/ and no Domain; SameSite=Lax and HttpOnly are the product's cookie contract.
        var attrs = setCookie.Split(';').Select(a => a.Trim().ToLowerInvariant()).ToList();
        Assert.Contains("secure", attrs);
        Assert.Contains("httponly", attrs);
        Assert.Contains("samesite=lax", attrs);
        Assert.Contains("path=/", attrs);
        Assert.DoesNotContain(attrs, a => a.StartsWith("domain="));

        // The cookie comes back on the next request under the prefix.
        var session = await SendAsync(HttpMethod.Get, "/session", TokenOf(setCookie));
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Fact]
    public async Task SignIn_RunsEntirelyUnderThePrefix()
    {
        var oldCookie = TokenOf(SetCookie(await SendAsync(HttpMethod.Get, "/session/probe"))!);

        // Step 1 hands out a login URL on the page's own origin AND prefix, not the bare host (which is the DEX app).
        var start = await SendAsync(HttpMethod.Post, "/auth/popup", oldCookie);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var json = await start.Content.ReadFromJsonAsync<JsonElement>();
        var loginUrl = new Uri(json.GetProperty("loginUrl").GetString()!);
        Assert.Equal(Origin, loginUrl.GetLeftPart(UriPartial.Authority));
        Assert.Equal(Prefix + "/auth/login", loginUrl.AbsolutePath);
        var reqId = Guid.Parse(json.GetProperty("authRequestId").GetString()!);

        // Step 2: the popup navigates there; the backend sends it to Biatec OIDC with the prefixed redirect URI.
        var login = await SendAsync(HttpMethod.Get, loginUrl.PathAndQuery[Prefix.Length..], oldCookie);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var authorize = login.Headers.Location!;
        Assert.Equal(Origin + Prefix + "/auth/callback", System.Web.HttpUtility.ParseQueryString(authorize.Query)["redirect_uri"]);
        var (code, state) = _oidc.Authorize(authorize);

        // Step 3: Biatec OIDC redirects the popup to the prefixed callback; the session rotates and the popup is done.
        var callback = await SendAsync(HttpMethod.Get, $"/auth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", oldCookie);
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.Contains("window.close()", await callback.Content.ReadAsStringAsync());
        var newCookie = TokenOf(SetCookie(callback)!);
        Assert.NotEqual(oldCookie, newCookie);
        Assert.Single(_oidc.TokenRequests);
        Assert.Equal(Origin + Prefix + "/auth/callback", _oidc.TokenRequests[0]["redirect_uri"]);

        // The widget polls the popup status under the prefix and sees the login.
        var poll = await SendAsync(HttpMethod.Get, $"/auth/popup/{reqId}", newCookie);
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        Assert.Equal("complete", (await poll.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString(), ignoreCase: true);
        var state2 = await (await SendAsync(HttpMethod.Get, "/session", newCookie)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authenticated", state2.GetProperty("tier").GetString(), ignoreCase: true);

        // Tokens stay on the server.
        await using var db = NewContext();
        Assert.Equal(PopupAuthStatus.Completed, (await db.PopupAuthRequests.SingleAsync(p => p.Id == reqId)).Status);
        Assert.DoesNotContain(_oidc.AccessToken, await callback.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WithoutThePrefix_TheMountedHostServesNothingOfTheBackend()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        foreach (var path in new[] { "/session/probe", "/auth/popup", "/auth/callback?code=x&state=y" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("http://" + Host + path)).StatusCode);
    }
}
