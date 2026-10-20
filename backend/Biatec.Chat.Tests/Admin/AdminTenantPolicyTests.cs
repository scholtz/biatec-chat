using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Biatec.Chat.Tests.Admin;

/// <summary>Admin API for tenant policy (T-086). Needs the docker-compose postgres.</summary>
public sealed class AdminTenantPolicyTests : IAsyncLifetime
{
    private const string OperatorKey = "test-operator-key-0123456789-abcdefghij";
    private const string BankId = "chat.example-bank.com";

    private sealed class MemoryTenantStore(params Tenant[] tenants) : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) => Task.FromResult(tenants.FirstOrDefault(t => t.Host == host));
    }

    private sealed class CaptureLogs : ILoggerProvider
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public ILogger CreateLogger(string categoryName) => new L(this);
        public void Dispose() { }
        private sealed class L(CaptureLogs p) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                p.Lines.Enqueue(formatter(state, exception));
        }
    }

    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly CaptureLogs _logs = new();
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;

    private async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private ChatDbContext NewContext() => new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);

    private static Tenant Bank() => new()
    {
        Id = BankId,
        Host = BankId,
        OidcClientId = "bank-client",
        ClientSecretRef = "secret://tenants/chat.example-bank.com/oidc-client-secret",
        RedirectUri = "https://chat.example-bank.com/auth/callback",
        AllowedOrigins = ["https://chat.example-bank.com"],
        ClientKeys = "[\"chat.example-bank.com\"]",
        ConsentThresholdCeilingEur = 1000,
        SoftCapEurPerHour = 1500,
        BedrockBudgets = "{\"note\":\"keep\"}",
        TrustAnchorVerified = false,
    };

    public async Task InitializeAsync()
    {
        await AdminAsync($"CREATE DATABASE \"{_dbName}\"");
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        db.Tenants.AddRange(TenantSeed.BiatecChat(), Bank());
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var f in _factories) f.Dispose();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private HttpClient Client(string? operatorKey = OperatorKey)
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
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
                ["Onboarding:OperatorKey"] = operatorKey,
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ITenantStore>();
                s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat()));
                s.AddSingleton<ILoggerProvider>(_logs);
            });
        });
        _factories.Add(f);
        return f.CreateClient();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, string? bearer = OperatorKey, string? origin = null)
    {
        var r = new HttpRequestMessage(method, "http://chat.biatec.io" + path);
        if (body is not null) r.Content = JsonContent.Create(body);
        if (bearer is not null) r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        if (origin is not null) r.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(r);
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient c, object body, string? bearer = OperatorKey) =>
        SendAsync(c, HttpMethod.Patch, $"/onboarding/operator/tenants/{BankId}/policy", body, bearer);

    private async Task<Tenant> BankAsync()
    {
        await using var db = NewContext();
        return await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == BankId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key-0123456789-0123456789-0123456789")]
    public async Task NonOperator_IsRejected(string? bearer)
    {
        var c = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(c, HttpMethod.Get, "/onboarding/operator/tenants", bearer: bearer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(c, HttpMethod.Get, $"/onboarding/operator/tenants/{BankId}", bearer: bearer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PatchAsync(c, new { x402Enabled = true, reason = "r" }, bearer)).StatusCode);
        Assert.False((await BankAsync()).X402Enabled);
    }

    [Fact]
    public async Task WithoutOperatorKeyConfigured_TheApiDoesNotExist()
    {
        var c = Client(operatorKey: null);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(c, HttpMethod.Get, "/onboarding/operator/tenants")).StatusCode);
    }

    [Fact]
    public async Task BrowserOrigin_GetsNoWildcardAndNoAccess()
    {
        var c = Client();
        var r = await SendAsync(c, HttpMethod.Get, "/onboarding/operator/tenants", bearer: null, origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.False(r.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Read_NeverReturnsSecretReferences()
    {
        var c = Client();
        var list = await (await SendAsync(c, HttpMethod.Get, "/onboarding/operator/tenants")).Content.ReadAsStringAsync();
        var one = await SendAsync(c, HttpMethod.Get, $"/onboarding/operator/tenants/{BankId}");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        var body = await one.Content.ReadAsStringAsync();
        foreach (var text in new[] { list, body })
        {
            Assert.DoesNotContain("secret://", text);
            Assert.DoesNotContain("clientSecretRef", text, StringComparison.OrdinalIgnoreCase);
        }
        var doc = JsonDocument.Parse(body).RootElement;
        Assert.Equal(1500m, doc.GetProperty("effectiveSoftCapEurPerHour").GetDecimal());
        Assert.False(doc.GetProperty("trustAnchorVerified").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(c, HttpMethod.Get, "/onboarding/operator/tenants/nope")).StatusCode);
    }

    [Fact]
    public async Task RaisingSoftCap_IsRefusedWithoutTrustAnchor()
    {
        var r = await PatchAsync(Client(), new { softCapEurPerHour = 3000, reason = "customer request" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("trust-anchored", await r.Content.ReadAsStringAsync());
        Assert.Equal(1500m, (await BankAsync()).SoftCapEurPerHour);
    }

    [Fact]
    public async Task RaisingSoftCap_IsAllowedForTrustAnchoredTenant_AndAudited()
    {
        var c = Client();
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(c, new { trustAnchorVerified = true, reason = "federation chain checked" })).StatusCode);
        var r = await PatchAsync(c, new { softCapEurPerHour = 3000, reason = "ticket 42" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var t = await BankAsync();
        Assert.Equal(3000m, t.SoftCapEurPerHour);
        Assert.True(t.TrustAnchorVerified);

        var audit = _logs.Lines.Single(l => l.Contains("changed tenant policy") && l.Contains("ticket 42"));
        Assert.Contains("softCapEurPerHour: 1500 -> 3000", audit);
        Assert.All(_logs.Lines, l => { Assert.DoesNotContain(OperatorKey, l); Assert.DoesNotContain("secret://", l); });

        // Dropping the trust anchor while the cap is raised would loosen an unverified tenant: refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(c, new { trustAnchorVerified = false, reason = "revoked" })).StatusCode);
        Assert.True((await BankAsync()).TrustAnchorVerified);
    }

    [Fact]
    public async Task SoftCapBelowFloor_IsRefused()
    {
        var r = await PatchAsync(Client(), new { softCapEurPerHour = 999, reason = "lower" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(1500m, (await BankAsync()).SoftCapEurPerHour);
    }

    [Theory]
    [InlineData("{\"clientSecretRef\":\"secret://x\",\"reason\":\"r\"}")]
    [InlineData("{\"host\":\"evil.example\",\"reason\":\"r\"}")]
    [InlineData("{\"x402Enabled\":true}")]
    [InlineData("{\"defaultModel\":\"eu.anthropic.claude-sonnet-5-0\",\"reason\":\"r\"}")]
    [InlineData("{\"bedrockUsdPerWindow\":-1,\"reason\":\"r\"}")]
    [InlineData("{\"safeApprovalCaps\":{\"approvalsPerHour\":-1},\"reason\":\"r\"}")]
    public async Task InvalidPatch_IsRefusedAndChangesNothing(string json)
    {
        var c = Client();
        var req = new HttpRequestMessage(HttpMethod.Patch, $"http://chat.biatec.io/onboarding/operator/tenants/{BankId}/policy")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + OperatorKey);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(req)).StatusCode);
        var t = await BankAsync();
        Assert.Equal(BankId, t.Host);
        Assert.Equal("secret://tenants/chat.example-bank.com/oidc-client-secret", t.ClientSecretRef);
        Assert.False(t.X402Enabled);
        Assert.Null(t.DefaultModel);
    }

    [Fact]
    public async Task FlagsBudgetsCapsAndModel_AreUpdatedAndMerged()
    {
        var r = await PatchAsync(Client(), new
        {
            limitsUpdateEnabled = true,
            x402Enabled = true,
            bedrockUsdPerWindow = 12.5m,
            safeApprovalCaps = new { approvalsPerHour = 3 },
            defaultModel = "eu.anthropic.claude-sonnet-4-5-20250929-v1:0",
            reason = "pilot",
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var t = await BankAsync();
        Assert.True(t.LimitsUpdateEnabled);
        Assert.True(t.X402Enabled);
        Assert.Equal("eu.anthropic.claude-sonnet-4-5-20250929-v1:0", t.DefaultModel);
        var budgets = JsonDocument.Parse(t.BedrockBudgets).RootElement;
        Assert.Equal(12.5m, budgets.GetProperty("usdPerWindow").GetDecimal());
        Assert.Equal("keep", budgets.GetProperty("note").GetString());
        var caps = Biatec.Chat.Tools.Caps.SafeCaps.Parse(t.SafeApprovalCaps);
        Assert.Equal(3, caps.ApprovalsPerHour);
        Assert.Equal(Biatec.Chat.Tools.Caps.CapDefaults.SafeExecutionsPerHour, caps.ExecutionsPerHour);
    }
}
