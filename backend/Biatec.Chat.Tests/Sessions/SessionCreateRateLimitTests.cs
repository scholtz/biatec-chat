using Microsoft.AspNetCore.Hosting;
using System.Net;
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

    private async Task<HttpStatusCode> SendAsync(HttpMethod m, string path, string? forwardedFor = null)
    {
        using var client = _factory.CreateClient();
        var r = new HttpRequestMessage(m, "http://chat.biatec.io" + path);
        r.Headers.TryAddWithoutValidation("Origin", Origin);
        if (m != HttpMethod.Get)
        {
            r.Headers.TryAddWithoutValidation("X-Biatec-Chat", "1");
            r.Headers.TryAddWithoutValidation("X-Biatec-Client-Key", "biatec-web");
        }
        return (await client.SendAsync(r)).StatusCode;
    }

    private async Task<int> SessionRowsAsync()
    {
        await using var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);
        return await db.ChatSessions.CountAsync();
    }

    [Fact]
    public async Task The_request_after_the_limit_from_one_address_gets_429_and_creates_no_session()
    {
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, await SendAsync(HttpMethod.Get, "/session/probe"));
        Assert.Equal(3, await SessionRowsAsync());

        Assert.Equal(HttpStatusCode.TooManyRequests, await SendAsync(HttpMethod.Get, "/session/probe"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SendAsync(HttpMethod.Get, "/session"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SendAsync(HttpMethod.Post, "/auth/popup"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SendAsync(HttpMethod.Post, "/session/adopt"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SendAsync(HttpMethod.Post, "/chat/messages"));
        Assert.Equal(3, await SessionRowsAsync());
    }
}
