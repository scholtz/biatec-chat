using Microsoft.AspNetCore.Hosting;
using System.Net;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Biatec.Chat.Tests.Sessions;

/// <summary>BCHAT-004: session-creating routes are limited per client address. Needs the docker-compose postgres.</summary>
public sealed class SessionCreateRateLimitTests : IAsyncLifetime
{
    private sealed class MemoryTenantStore(params Tenant[] tenants) : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) => Task.FromResult(tenants.FirstOrDefault(t => t.Host == host));
    }

    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private const string Origin = "https://web.biatec.io";
    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
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
        await AdminAsync($"CREATE DATABASE \"{_dbName}\"");
        await using (var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options)) await db.Database.MigrateAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oidc:Issuer"] = "https://oidc.example.test",
                ["Oidc:McpResource"] = "https://mcp.example.test",
                ["Mcp:Endpoint"] = "https://mcp.example.test/mcp",
                ["Redis:ConnectionString"] = "127.0.0.1:1",
                ["Postgres:ConnectionString"] = Cs,
                ["Algod:Networks:testnet:Url"] = "https://algod.example.test",
                ["Kms:KeyId"] = "alias/test",
                ["Evidence:AllowEphemeralKeys"] = "true",
                ["Sessions:CreateRateLimit:Permits"] = "3",
                ["Sessions:CreateRateLimit:WindowSeconds"] = "3600",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ITenantStore>();
                s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat()));
            });
        });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod m, string path, string? cookie = null)
    {
        using var client = _factory.CreateClient();
        var r = new HttpRequestMessage(m, "http://chat.biatec.io" + path);
        r.Headers.TryAddWithoutValidation("Origin", Origin);
        if (m != HttpMethod.Get)
        {
            r.Headers.TryAddWithoutValidation("X-Biatec-Chat", "1");
            r.Headers.TryAddWithoutValidation("X-Biatec-Client-Key", "biatec-web");
        }
        if (m != HttpMethod.Get) r.Content = new StringContent("{\"message\":\"hello\"}", System.Text.Encoding.UTF8, "application/json");
        if (cookie is not null) r.Headers.TryAddWithoutValidation("Cookie", $"{SessionPolicy.CookieName}={cookie}");
        return await client.SendAsync(r);
    }

    private static string TokenOf(HttpResponseMessage r) =>
        r.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(SessionPolicy.CookieName + "="))[(SessionPolicy.CookieName.Length + 1)..].Split(';')[0];

    private async Task<int> SessionRowsAsync()
    {
        await using var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);
        return await db.ChatSessions.CountAsync();
    }

    [Fact]
    public async Task Creating_more_sessions_than_the_limit_from_one_address_gets_429_and_writes_nothing()
    {
        string? cookie = null;
        for (var i = 0; i < 3; i++)
        {
            var ok = await SendAsync(HttpMethod.Get, "/session/probe");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            cookie = TokenOf(ok);
        }
        Assert.Equal(3, await SessionRowsAsync());

        var limited = await SendAsync(HttpMethod.Get, "/session/probe");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        foreach (var (m, path) in new[] { (HttpMethod.Get, "/session"), (HttpMethod.Post, "/auth/popup"), (HttpMethod.Post, "/chat/messages") })
            Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(m, path)).StatusCode);
        Assert.Equal(3, await SessionRowsAsync());

        // A request that carries a live session creates nothing, so it is never counted against the budget.
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/session/probe", cookie)).StatusCode);
        Assert.Equal(3, await SessionRowsAsync());
    }
}

public sealed class SessionCreationLimiterTests
{
    private static SessionCreationLimiter Limiter(int permits) =>
        new(Microsoft.Extensions.Options.Options.Create(new SessionCreateRateLimitOptions { Permits = permits, WindowSeconds = 3600 }));

    [Fact]
    public void Each_address_has_its_own_budget()
    {
        using var l = Limiter(2);
        var a = IPAddress.Parse("203.0.113.1");
        var b = IPAddress.Parse("203.0.113.2");
        l.Acquire(a); l.Acquire(a);
        Assert.Throws<SessionCreationLimitedException>(() => l.Acquire(a));
        l.Acquire(b); // another address is unaffected
    }

    [Fact]
    public void Addresses_of_one_ipv6_allocation_and_ipv4_mapped_forms_share_a_budget()
    {
        using var l = Limiter(1);
        l.Acquire(IPAddress.Parse("2001:db8:1:2::1"));
        Assert.Throws<SessionCreationLimitedException>(() => l.Acquire(IPAddress.Parse("2001:db8:1:2:ffff:eeee:dddd:cccc")));
        l.Acquire(IPAddress.Parse("2001:db8:1:3::1")); // a different /64

        l.Acquire(IPAddress.Parse("203.0.113.7"));
        Assert.Throws<SessionCreationLimitedException>(() => l.Acquire(IPAddress.Parse("::ffff:203.0.113.7")));
    }

    [Fact]
    public void A_missing_address_shares_one_bucket_rather_than_being_unlimited()
    {
        using var l = Limiter(1);
        l.Acquire(null);
        Assert.Throws<SessionCreationLimitedException>(() => l.Acquire(null));
    }
}
