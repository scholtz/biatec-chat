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
    private const string LegacyId = "chat.legacy-bank.com";

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
        var legacy = Bank();
        legacy.Id = legacy.Host = LegacyId;
        legacy.ClientSecretRef = "secret://tenants/chat.legacy-bank.com/oidc-client-secret";
        // A row written before the T-025 save rule: cap below the floor, legacy safe-cap key, budget with trailing zeros.
        legacy.SoftCapEurPerHour = 500;
        legacy.X402Enabled = true;
        legacy.LimitsUpdateEnabled = true;
        legacy.SafeApprovalCaps = "{\"perHour\":5}";
        legacy.BedrockBudgets = "{\"usdPerWindow\":10.00}";
        db.Tenants.AddRange(TenantSeed.BiatecChat(), Bank(), legacy);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var f in _factories) f.Dispose();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private HttpClient Client(string? operatorKey = OperatorKey) => Factory(operatorKey).CreateClient();

    private WebApplicationFactory<Program> Factory(string? operatorKey = OperatorKey, Dictionary<string, string?>? extra = null, bool realTenantStore = false)
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
                ["Evidence:AllowEphemeralKeys"] = "true",
                ["Onboarding:OperatorKey"] = operatorKey,
            }.Concat(extra ?? []).ToDictionary()));
            b.ConfigureServices(s =>
            {
                if (!realTenantStore)
                {
                    s.RemoveAll<ITenantStore>();
                    s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat()));
                }
                s.AddSingleton<ILoggerProvider>(_logs);
            });
        });
        _factories.Add(f);
        return f;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, string? bearer = OperatorKey, string? origin = null)
    {
        var r = new HttpRequestMessage(method, "http://chat.biatec.io" + path);
        if (body is not null) r.Content = JsonContent.Create(body);
        if (bearer is not null) r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        if (origin is not null) r.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(r);
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient c, object body, string? bearer = OperatorKey, string id = BankId) =>
        SendAsync(c, HttpMethod.Patch, $"/onboarding/operator/tenants/{id}/policy", body, bearer);

    private async Task<Tenant> BankAsync(string id = BankId)
    {
        await using var db = NewContext();
        return await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    private static async Task<HttpResponseMessage> RawPatchAsync(HttpClient c, string id, string json, string? bearer = OperatorKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, $"http://chat.biatec.io/onboarding/operator/tenants/{id}/policy")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        if (bearer is not null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        return await c.SendAsync(req);
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

    [Fact]
    public async Task UnauthenticatedPatch_IsRejectedBeforeTheBodyIsRead()
    {
        // An unparseable body and unknown fields would be a 400 if binding ran first.
        var c = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await RawPatchAsync(c, BankId, "{not json", bearer: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RawPatchAsync(c, BankId, "{\"clientSecretRef\":1}", bearer: "nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RawPatchAsync(Client(operatorKey: null), BankId, "{not json", bearer: null)).StatusCode);
        Assert.Contains(_logs.Lines, l => l.Contains("rejected unauthenticated PATCH"));
    }

    [Fact]
    public async Task LegacyRow_CanStillBePatched_FeaturesSwitchOff()
    {
        var c = Client();
        var r = await PatchAsync(c, new { x402Enabled = false, limitsUpdateEnabled = false, reason = "kill switch" }, id: LegacyId);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var t = await BankAsync(LegacyId);
        Assert.False(t.X402Enabled);
        Assert.False(t.LimitsUpdateEnabled);
        Assert.Equal(500m, t.SoftCapEurPerHour);
        // Touching the cap applies the rule: the floor still holds.
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(c, new { softCapEurPerHour = 600, reason = "r" }, id: LegacyId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(c, new { softCapEurPerHour = 1200, reason = "r" }, id: LegacyId)).StatusCode);
    }

    [Fact]
    public async Task NumericValues_CompareAsDecimals_AndAuditUsesEffectiveOldValues()
    {
        var c = Client();
        var same = await PatchAsync(c, new { bedrockUsdPerWindow = 10, safeApprovalCaps = new { approvalsPerHour = 5 }, reason = "noop" }, id: LegacyId);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(0, JsonDocument.Parse(await same.Content.ReadAsStringAsync()).RootElement.GetProperty("changes").GetArrayLength());

        var r = await PatchAsync(c, new { bedrockUsdPerWindow = 7.5m, safeApprovalCaps = new { approvalsPerHour = 3 }, reason = "tighten" }, id: LegacyId);
        var changes = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("changes").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("bedrockBudgets.usdPerWindow: 10 -> 7.5", changes);
        Assert.Contains("safeApprovalCaps.approvalsPerHour: 5 -> 3", changes);
        Assert.Equal(3, Biatec.Chat.Tools.Caps.SafeCaps.Parse((await BankAsync(LegacyId)).SafeApprovalCaps).ApprovalsPerHour);
    }

    [Fact]
    public async Task Change_EvictsTheTenantCache_OnThisReplica()
    {
        var f = Factory(realTenantStore: true);
        var store = f.Services.GetRequiredService<ITenantStore>();
        Assert.False((await store.FindByHostAsync(BankId, default))!.X402Enabled); // now cached
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(f.CreateClient(), new { x402Enabled = true, reason = "on" })).StatusCode);
        Assert.True((await store.FindByHostAsync(BankId, default))!.X402Enabled);
    }

    [Fact]
    public async Task AdminRoutes_HaveTheirOwnRateLimitPartition()
    {
        var c = Factory(extra: new() { ["Admin:RateLimitPermits"] = "2", ["Onboarding:RateLimitPermits"] = "1" }).CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(c, HttpMethod.Get, "/onboarding/operator/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(c, HttpMethod.Get, "/onboarding/operator/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(c, HttpMethod.Get, "/onboarding/operator/tenants")).StatusCode);
        // Public onboarding still has its own (unused) permit: its answer is not a 429.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await SendAsync(c, HttpMethod.Post, "/onboarding/challenges", new { host = "x.example.com" }, bearer: null)).StatusCode);
    }
}
