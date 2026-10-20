using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Biatec.Chat.Api.Admin;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Biatec.Chat.Tests.Admin;

/// <summary>Named operator identities and the signed admin evidence chain (T-097). Needs the docker-compose postgres.</summary>
public sealed class AdminOperatorIdentityTests : IAsyncLifetime
{
    private const string AliceKey = "alice-operator-key-0123456789-abcdefghij";
    private const string BobKey = "bob-operator-key-0123456789-abcdefghijkl";
    private const string LegacyKey = "test-operator-key-0123456789-abcdefghij";
    private const string BankId = "chat.example-bank.com";
    private const string PageText = "# Zebra staking\n\nZebra stripe holders stake here.\n";

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
    private readonly DirectoryInfo _seed = Directory.CreateTempSubdirectory("kb-seed-");
    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;

    private static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private ChatDbContext NewContext() => new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);

    public async Task InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_seed.FullName, "first.md"), "# First\n\nAardvark facts.\n");
        await AdminAsync($"CREATE DATABASE \"{_dbName}\"");
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        db.Tenants.AddRange(TenantSeed.BiatecChat(), new Tenant
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
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var f in _factories) f.Dispose();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
        _seed.Delete(recursive: true);
    }

    /// <summary>Alice and Bob as named operators (hashes only), plus the legacy shared key unless it is null.</summary>
    private WebApplicationFactory<Program> Factory(string? legacyKey = LegacyKey, Dictionary<string, string?>? operators = null)
    {
        operators ??= new()
        {
            ["Admin:Operators:alice:KeyHash"] = Hash(AliceKey),
            ["Admin:Operators:bob:KeyHash"] = Hash(BobKey).ToUpperInvariant(),
        };
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
                ["Onboarding:OperatorKey"] = legacyKey,
                ["Kb:Path"] = _seed.FullName,
                ["Kb:RefreshSeconds"] = "0",
            }.Concat(operators).ToDictionary()));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ITenantStore>();
                s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat()));
                s.AddSingleton<ILoggerProvider>(_logs);
            });
        });
        _factories.Add(f);
        return f;
    }

    private static async Task<HttpResponseMessage> SendAsync(WebApplicationFactory<Program> f, HttpMethod method, string path, string? bearer,
        HttpContent? content = null, string? reason = null)
    {
        var r = new HttpRequestMessage(method, "http://chat.biatec.io/onboarding/operator" + path) { Content = content };
        if (bearer is not null) r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        if (reason is not null) r.Headers.TryAddWithoutValidation("X-Admin-Reason", reason);
        using var client = f.CreateClient();
        return await client.SendAsync(r);
    }

    private static Task<HttpResponseMessage> PatchAsync(WebApplicationFactory<Program> f, string bearer, object body) =>
        SendAsync(f, HttpMethod.Patch, $"/tenants/{BankId}/policy", bearer, JsonContent.Create(body));

    private async Task<IReadOnlyList<EvidenceEntry>> ChainAsync()
    {
        await using var db = NewContext();
        return await db.EvidenceEntries.AsNoTracking().Where(e => e.ConversationId == AdminEvidence.ChainId).OrderBy(e => e.Seq).ToListAsync();
    }

    private static JsonElement Payload(EvidenceEntry e) => JsonDocument.Parse(e.Payload!).RootElement;

    [Fact]
    public async Task PolicyChange_RecordsTheNamedOperator_InTheSignedChain()
    {
        var f = Factory();
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(f, AliceKey, new { x402Enabled = true, softCapEurPerHour = 1200, reason = "ticket 42" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(f, BobKey, new { x402Enabled = false, reason = "ticket 43" })).StatusCode);

        var chain = await ChainAsync();
        Assert.Equal(2, chain.Count);
        Assert.All(chain, e => Assert.Equal(AdminEvidence.Kind, e.Kind));
        var first = Payload(chain[0]);
        Assert.Equal("alice", first.GetProperty("operator").GetString());
        Assert.Equal("named-key", first.GetProperty("operatorAuth").GetString());
        Assert.Equal(AdminEvidence.TenantPolicyUpdate, first.GetProperty("action").GetString());
        Assert.Equal("tenant:" + BankId, first.GetProperty("target").GetString());
        Assert.Equal("ticket 42", first.GetProperty("reason").GetString());
        Assert.Equal("false", first.GetProperty("before").GetProperty("x402Enabled").GetString());
        Assert.Equal("true", first.GetProperty("after").GetProperty("x402Enabled").GetString());
        Assert.Equal("1500", first.GetProperty("before").GetProperty("softCapEurPerHour").GetString());
        Assert.Equal("1200", first.GetProperty("after").GetProperty("softCapEurPerHour").GetString());
        Assert.Equal("bob", Payload(chain[1]).GetProperty("operator").GetString());

        // The audit log line names the operator too; no key appears anywhere.
        Assert.Contains(_logs.Lines, l => l.Contains("operator alice") && l.Contains("changed tenant policy") && l.Contains("ticket 42"));
        Assert.Contains(_logs.Lines, l => l.Contains("operator bob") && l.Contains("ticket 43"));
        foreach (var text in _logs.Lines.Concat(chain.Select(e => e.Payload!)))
            foreach (var key in new[] { AliceKey, BobKey, LegacyKey, Hash(AliceKey), Hash(BobKey) })
                Assert.DoesNotContain(key, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminChain_Verifies_AndTamperingIsDetected()
    {
        var f = Factory();
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(f, AliceKey, new { limitsUpdateEnabled = true, reason = "pilot" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f, HttpMethod.Put, "/kb/pages/zebra-staking", BobKey,
            new StringContent(PageText, Encoding.UTF8, "text/markdown"), reason: "new page")).StatusCode);

        var r = await SendAsync(f, HttpMethod.Get, "/evidence", AliceKey);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("verification").GetProperty("valid").GetBoolean());
        var entries = body.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(entries[0].GetProperty("entryHash").GetString(), entries[1].GetProperty("prevHash").GetString());

        // Independent check: the same chain verifies against the published keys.
        using (var scope = f.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<IEvidenceLog>().VerifyAsync(AdminEvidence.ChainId)).Valid);

        // Rewriting who made the first change breaks the chain.
        await using (var db = NewContext())
        {
            var e = await db.EvidenceEntries.SingleAsync(x => x.ConversationId == AdminEvidence.ChainId && x.Seq == 1);
            e.Payload = e.Payload!.Replace("alice", "mallory");
            await db.SaveChangesAsync();
        }
        var tampered = JsonDocument.Parse(await (await SendAsync(f, HttpMethod.Get, "/evidence", AliceKey)).Content.ReadAsStringAsync()).RootElement;
        Assert.False(tampered.GetProperty("verification").GetProperty("valid").GetBoolean());
        Assert.Equal(1, tampered.GetProperty("verification").GetProperty("failedSeq").GetInt64());
    }

    [Fact]
    public async Task KbChanges_RecordOperator_AndHashOnly_NeverContent()
    {
        var f = Factory();
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f, HttpMethod.Put, "/kb/pages/zebra-staking", AliceKey,
            new StringContent(PageText, Encoding.UTF8, "text/markdown"), reason: "upload")).StatusCode);
        // Same content again: unchanged, nothing to record.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(f, HttpMethod.Put, "/kb/pages/zebra-staking", AliceKey,
            new StringContent(PageText, Encoding.UTF8, "text/markdown"), reason: "again")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(f, HttpMethod.Put, "/kb/pages/zebra-staking", BobKey,
            new StringContent(PageText + "More.\n", Encoding.UTF8, "text/plain"), reason: "replace")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(f, HttpMethod.Delete, "/kb/pages/zebra-staking", BobKey, reason: "remove")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(f, HttpMethod.Delete, "/kb/pages/zebra-staking", BobKey, reason: "remove")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(f, HttpMethod.Post, "/kb/reload", AliceKey, reason: "seed volume changed")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(f, HttpMethod.Post, "/kb/reload", AliceKey)).StatusCode);

        var chain = (await ChainAsync()).Select(Payload).ToList();
        Assert.Equal(["kb_page_upload", "kb_page_upload", "kb_page_delete", "kb_reload", "kb_reload"], chain.Select(p => p.GetProperty("action").GetString()));
        Assert.Equal(["alice", "bob", "bob", "alice", "alice"], chain.Select(p => p.GetProperty("operator").GetString()));
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PageText)));
        Assert.Equal(JsonValueKind.Null, chain[0].GetProperty("before").ValueKind);
        Assert.Equal(sha, chain[0].GetProperty("after").GetProperty("sha256").GetString());
        Assert.Equal(sha, chain[1].GetProperty("before").GetProperty("sha256").GetString());
        Assert.Equal("text/plain", chain[1].GetProperty("after").GetProperty("contentType").GetString());
        Assert.Equal("kb-page:zebra-staking", chain[2].GetProperty("target").GetString());
        Assert.Equal(JsonValueKind.Null, chain[2].GetProperty("after").ValueKind);
        Assert.Equal("seed volume changed", chain[3].GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, chain[4].GetProperty("reason").ValueKind);
        Assert.All(chain, p => Assert.DoesNotContain("stripe holders", p.GetRawText()));
        Assert.Contains(_logs.Lines, l => l.Contains("operator bob") && l.Contains("deleted kb page zebra-staking"));

        // An invalid optional reason on reload is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(f, HttpMethod.Post, "/kb/reload", AliceKey, reason: new string('x', 201))).StatusCode);
    }

    [Fact]
    public async Task LegacySharedKey_StillWorks_AsTheDeprecatedLegacyOperator()
    {
        var f = Factory();
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(f, LegacyKey, new { x402Enabled = true, reason = "old script" })).StatusCode);
        var p = Payload((await ChainAsync()).Single());
        Assert.Equal("legacy", p.GetProperty("operator").GetString());
        Assert.Equal("legacy-shared-key", p.GetProperty("operatorAuth").GetString());
        Assert.Contains(_logs.Lines, l => l.Contains("deprecated") && l.Contains("Onboarding:OperatorKey"));

        // Without named operators the legacy key alone keeps the API on, as before.
        var only = Factory(operators: []);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(only, HttpMethod.Get, "/tenants", LegacyKey)).StatusCode);
    }

    [Fact]
    public async Task NamedKeysAlone_EnableTheApi_AndAnythingElseIsRejectedBeforeTheBody()
    {
        var f = Factory(legacyKey: null);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(f, HttpMethod.Get, "/tenants", AliceKey)).StatusCode);
        foreach (var bad in new string?[] { null, LegacyKey, Hash(AliceKey), AliceKey + "x" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(f, HttpMethod.Get, "/evidence", bad)).StatusCode);
            var raw = new StringContent("{not json", Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(f, HttpMethod.Patch, $"/tenants/{BankId}/policy", bad, raw)).StatusCode);
        }
        Assert.Empty(await ChainAsync());

        // No operator key of any kind: the API does not exist.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(Factory(legacyKey: null, operators: []), HttpMethod.Get, "/evidence", AliceKey)).StatusCode);
    }

    [Fact]
    public async Task RefusedOrNoOpChanges_AreNotRecorded()
    {
        var f = Factory();
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(f, AliceKey, new { softCapEurPerHour = 999, reason = "below floor" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(f, AliceKey, new { x402Enabled = false, reason = "already off" })).StatusCode);
        Assert.Empty(await ChainAsync());
    }

    [Theory]
    [InlineData("alice", "not-a-hash")]
    [InlineData("legacy", null)]
    [InlineData("bad name", null)]
    [InlineData("carol", "same-as-alice")]
    public void InvalidOperatorConfig_StopsTheStart(string name, string? hash)
    {
        var operators = new Dictionary<string, string?>
        {
            ["Admin:Operators:alice:KeyHash"] = Hash(AliceKey),
            [$"Admin:Operators:{name}:KeyHash"] = hash switch { null => Hash(BobKey), "same-as-alice" => Hash(AliceKey), _ => hash },
        };
        var f = Factory(operators: operators);
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("Admin:Operators", ex.ToString());
    }
}
