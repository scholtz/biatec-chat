using System.Net;
using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Auth.Logout;
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

namespace Biatec.Chat.Tests.Sweeper;

/// <summary>Logout end to end through the HTTP pipeline. Needs the docker-compose postgres.</summary>
public sealed class LogoutTests : IAsyncLifetime
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

    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private const string Host = "chat.biatec.io";
    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly FakeTime _time = new();
    private readonly MockOidc _oidc = new();
    private sealed class ThrowingRevocation : IRevocationService
    {
        public bool IsConfigured => false;
        public Task RevokeOrParkAsync(Tenant tenant, string refreshToken, CancellationToken ct) => throw new InvalidOperationException("db down");
        public Task<int> RetryPendingAsync(CancellationToken ct) => Task.FromResult(0);
    }
    private sealed class ThrowingMetadata : IOidcMetadataProvider
    {
        public Task<OidcMetadata> GetAsync(CancellationToken ct) => throw new HttpRequestException("discovery down");
        public Task<IReadOnlyList<Microsoft.IdentityModel.Tokens.SecurityKey>> GetSigningKeysAsync(bool forceRefresh, CancellationToken ct) => throw new HttpRequestException("down");
    }
    private Action<IServiceCollection>? _extra;
    private readonly SweeperTests.MemoryVault _vault = new();
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
        BuildFactory();
    }

    private void BuildFactory(Action<IServiceCollection>? extra = null)
    {
        _extra = extra;
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
                ["Sweeper:Enabled"] = "false",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ITenantStore>();
                s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat()));
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(_time);
                s.RemoveAll<ITokenVault>();
                s.AddSingleton<ITokenVault>(_vault);
                s.AddHttpClient(OidcMetadataProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _oidc);
                _extra?.Invoke(s);
            });
        });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private ChatDbContext NewContext() => new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);

    private async Task<HttpResponseMessage> GetAsync(string path, string? cookie, Action<HttpRequestMessage>? tweak = null)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var r = new HttpRequestMessage(HttpMethod.Get, "http://" + Host + path);
        if (cookie is not null) r.Headers.TryAddWithoutValidation("Cookie", $"{SessionPolicy.CookieName}={cookie}");
        tweak?.Invoke(r);
        return await client.SendAsync(r);
    }

    private async Task<string> NewSessionAsync()
    {
        var res = await GetAsync("/session/probe", null);
        var setCookie = res.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(SessionPolicy.CookieName + "="));
        return setCookie[(SessionPolicy.CookieName.Length + 1)..].Split(';')[0];
    }

    private void Vault(string cookie, string? idToken = "ID-TOKEN-VALUE") =>
        _vault.Items[SessionService.IdFromToken(cookie).ToString("N")] = new TokenVaultEntry
        {
            AccessToken = "A", AccessExpiresAt = _time.Now.AddMinutes(10), RefreshToken = "REFRESH-LOGOUT", IdToken = idToken,
            AbsoluteExpiresAt = _time.Now.AddHours(8),
        };

    [Fact]
    public async Task Sweeper_Logout_ClearsCookieVaultAndSession_ParksToken_RedirectsToEndSession()
    {
        var cookie = await NewSessionAsync();
        Vault(cookie);

        var res = await GetAsync("/auth/logout", cookie);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var loc = res.Headers.Location!;
        Assert.Equal("https://oidc.example.test/connect/endsession", loc.GetLeftPart(UriPartial.Path));
        var q = System.Web.HttpUtility.ParseQueryString(loc.Query);
        Assert.Equal("ID-TOKEN-VALUE", q["id_token_hint"]);
        Assert.Equal("biatec-chat", q["client_id"]);
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());

        var cleared = res.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(SessionPolicy.CookieName + "="));
        Assert.StartsWith(SessionPolicy.CookieName + "=;", cleared);
        Assert.Contains("expires=Thu, 01 Jan 1970", cleared);

        Assert.Empty(_vault.Items);
        await using var db = NewContext();
        Assert.False(await db.ChatSessions.AnyAsync());
        Assert.Single(await db.PendingRevocations.ToListAsync()); // no revocation URL yet: parked
    }

    [Fact]
    public async Task Sweeper_Logout_AnonymousOrUnknownSession_IsIdempotentNoContent()
    {
        var cookie = await NewSessionAsync();
        var first = await GetAsync("/auth/logout", cookie);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Contains(first.Headers.GetValues("Set-Cookie"), x => x.StartsWith(SessionPolicy.CookieName + "=;"));

        var again = await GetAsync("/auth/logout", cookie);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        var none = await GetAsync("/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);
    }

    [Fact]
    public async Task Sweeper_Logout_CrossSiteNavigationIsRefused_AndKeepsSession()
    {
        var cookie = await NewSessionAsync();
        Vault(cookie);

        var res = await GetAsync("/auth/logout", cookie, r => r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site"));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.NotEmpty(_vault.Items);
        await using var db = NewContext();
        Assert.True(await db.ChatSessions.AnyAsync());
    }

    [Fact]
    public async Task Sweeper_Logout_OtherTenantsCookieDoesNotEndSession()
    {
        var cookie = await NewSessionAsync();
        await using (var db = NewContext())
            await db.ChatSessions.ExecuteUpdateAsync(u => u.SetProperty(s => s.TenantId, "someone-else"));
        Vault(cookie);

        var res = await GetAsync("/auth/logout", cookie);

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.NotEmpty(_vault.Items);
        await using var db2 = NewContext();
        Assert.True(await db2.ChatSessions.AnyAsync());
    }

    private async Task<(string Cookie, HttpResponseMessage Res)> LogoutAsync(Action<SweeperTests.MemoryVault>? arrange = null)
    {
        var cookie = await NewSessionAsync();
        Vault(cookie);
        arrange?.Invoke(_vault);
        return (cookie, await GetAsync("/auth/logout", cookie));
    }

    private static void AssertCookieCleared(HttpResponseMessage res) =>
        Assert.Contains(res.Headers.GetValues("Set-Cookie"), x => x.StartsWith(SessionPolicy.CookieName + "=;"));

    [Fact]
    public async Task Sweeper_Logout_UnreadableVaultEntry_StillEndsSessionAndClearsCookie()
    {
        var (_, res) = await LogoutAsync(v => v.CorruptReads = true);

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        AssertCookieCleared(res);
        Assert.Empty(_vault.Items);
        await using var db = NewContext();
        Assert.False(await db.ChatSessions.AnyAsync());
    }

    [Fact]
    public async Task Sweeper_Logout_VaultOutage_ClearsCookie_LeavesSessionForSweeper()
    {
        var (_, res) = await LogoutAsync(v => v.FailReads = true);

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        AssertCookieCleared(res);
        Assert.NotEmpty(_vault.Items);
        await using var db = NewContext();
        Assert.True(await db.ChatSessions.AnyAsync());
    }

    [Fact]
    public async Task Sweeper_Logout_VaultDeleteFails_ClearsCookie_KeepsSession()
    {
        var (_, res) = await LogoutAsync(v => v.FailDeletes = true);

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        AssertCookieCleared(res);
        await using var db = NewContext();
        Assert.True(await db.ChatSessions.AnyAsync());
    }

    [Fact]
    public async Task Sweeper_Logout_RevocationAndParkFail_StillDeletesEntryAndSession_Redirects()
    {
        _factory.Dispose();
        BuildFactory(s => { s.RemoveAll<IRevocationService>(); s.AddScoped<IRevocationService, ThrowingRevocation>(); });
        var (_, res) = await LogoutAsync();

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        AssertCookieCleared(res);
        Assert.Empty(_vault.Items);
        await using var db = NewContext();
        Assert.False(await db.ChatSessions.AnyAsync());
    }

    [Fact]
    public async Task Sweeper_Logout_EndSessionDiscoveryFails_Returns204AfterSessionDeleted()
    {
        _factory.Dispose();
        BuildFactory(s => { s.RemoveAll<IOidcMetadataProvider>(); s.AddSingleton<IOidcMetadataProvider, ThrowingMetadata>(); });
        var (_, res) = await LogoutAsync();

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        AssertCookieCleared(res);
        Assert.Empty(_vault.Items);
        await using var db = NewContext();
        Assert.False(await db.ChatSessions.AnyAsync());
    }
}
