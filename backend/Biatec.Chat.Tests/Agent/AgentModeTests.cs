using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Model;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ModelMessage = Amazon.BedrockRuntime.Model.Message;

namespace Biatec.Chat.Tests.Agent;

/// <summary>T-065: agent mode and pause, enforced server-side. Needs the docker-compose postgres; Bedrock is a scripted fake.</summary>
public sealed class AgentModeTests : IAsyncLifetime
{
    private sealed class MemoryTenantStore(params Tenant[] tenants) : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) => Task.FromResult(tenants.FirstOrDefault(t => t.Host == host));
    }

    private sealed class MemoryVault : ITokenVault
    {
        public readonly Dictionary<string, TokenVaultEntry> Items = new();
        public Task PutAsync(string id, TokenVaultEntry e, CancellationToken ct = default) { Items[id] = e; return Task.CompletedTask; }
        public Task<TokenVaultEntry?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task DeleteAsync(string id, CancellationToken ct = default) { Items.Remove(id); return Task.CompletedTask; }
        public Task<string?> GetAccessTokenAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id)?.AccessToken);
    }

    private sealed class ScriptedModel(Func<int, ModelRequest, IEnumerable<ModelStreamEvent>> script) : IModelClient
    {
        public readonly List<ModelRequest> Requests = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            int n;
            lock (Requests) { Requests.Add(request); n = Requests.Count; }
            foreach (var e in script(n, request)) { await Task.Yield(); yield return e; }
        }
    }

    private sealed class CountingExecutor(string name) : IToolExecutor
    {
        public int Calls;
        public string CapabilityName => name;
        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, System.Text.Json.Nodes.JsonNode arguments, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ToolExecutionResult("ran"));
        }
    }

    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private const string Origin = "https://web.biatec.io";
    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly MemoryVault _vault = new();
    private readonly InMemoryPolicyEventSink _sink = new();
    private readonly CountingExecutor _transfer = new("wallet.transfer");
    private readonly CountingExecutor _balance = new("wallet.balance");
    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;
    private WebApplicationFactory<Program> _factory = null!;
    private ScriptedModel _model = null!;

    private static readonly string[] FundMoving =
        ["wallet.transfer", "dex.swap", "dex.addLiquidity", "dex.removeLiquidity", "safe.create", "safe.propose", "safe.approve", "safe.execute", "limits.update", "x402.pay"];

    public async Task InitializeAsync()
    {
        await AdminAsync($"CREATE DATABASE \"{_dbName}\"");
        await using (var db = NewContext()) await db.Database.MigrateAsync();
        // Round 1 of a chat turn: the model forges a call to a fund-moving tool. Later rounds answer in text.
        _model = new ScriptedModel((n, _) => n == 1 ? ForgedCall() : Text("ok"));
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
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ITenantStore>();
                s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat()));
                s.RemoveAll<IModelClient>();
                s.AddSingleton<IModelClient>(_model);
                s.RemoveAll<ITokenVault>();
                s.AddSingleton<ITokenVault>(_vault);
                s.RemoveAll<IPolicyEventSink>();
                s.AddSingleton<IPolicyEventSink>(_sink);
                s.AddSingleton<IToolExecutor>(_transfer);
                s.AddSingleton<IToolExecutor>(_balance);
            });
        });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private ChatDbContext NewContext() => new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);

    private static IEnumerable<ModelStreamEvent> Text(string t)
    {
        yield return new ModelTextDelta(t);
        yield return new ModelMessageStop("end_turn", new ModelMessage { Role = ConversationRole.Assistant, Content = [new ContentBlock { Text = t }] });
    }

    private static IEnumerable<ModelStreamEvent> ForgedCall()
    {
        const string input = """{"network":"testnet","from":"AAA","to":"BBB","assetId":0,"amount":"1000"}""";
        var wire = PromptBuilder.WireName("wallet.transfer");
        yield return new ModelToolUse("t1", wire, input);
        yield return new ModelMessageStop("tool_use", new ModelMessage
        {
            Role = ConversationRole.Assistant,
            Content = [new ContentBlock { ToolUse = new ToolUseBlock { ToolUseId = "t1", Name = wire, Input = DocumentJson.FromJson(input) } }],
        });
    }

    private HttpRequestMessage Req(HttpMethod m, string path, string? cookie, bool csrf = true, string origin = Origin, object? body = null)
    {
        var r = new HttpRequestMessage(m, "http://chat.biatec.io" + path);
        if (body is not null) r.Content = JsonContent.Create(body);
        r.Headers.TryAddWithoutValidation("Origin", origin);
        if (csrf)
        {
            r.Headers.TryAddWithoutValidation("X-Biatec-Chat", "1");
            r.Headers.TryAddWithoutValidation("X-Biatec-Client-Key", "biatec-web");
        }
        if (cookie is not null) r.Headers.TryAddWithoutValidation("Cookie", $"{SessionPolicy.CookieName}={cookie}");
        return r;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r)
    {
        using var client = _factory.CreateClient();
        return await client.SendAsync(r);
    }

    /// <summary>Creates a session and, when asked, makes it an authenticated one holding the sign scope in the vault.</summary>
    private async Task<(string Cookie, Guid Id)> NewSessionAsync(bool authenticated)
    {
        var r = await SendAsync(Req(HttpMethod.Get, "/session", null));
        var sc = r.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(SessionPolicy.CookieName + "="));
        var token = sc[(SessionPolicy.CookieName.Length + 1)..].Split(';')[0];
        var id = SessionService.IdFromToken(token);
        if (authenticated)
        {
            await using var db = NewContext();
            var s = await db.ChatSessions.AsNoTracking().SingleAsync(x => x.Id == id);
            await db.ChatSessions.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Tier, SessionTier.Authenticated));
            _vault.Items[id.ToString("N")] = new TokenVaultEntry
            {
                AccessToken = "at", AccessExpiresAt = DateTimeOffset.UtcNow.AddHours(1), RefreshToken = "rt", IdToken = "it",
                Scopes = ["openid", "sign", "manage-limits"], AbsoluteExpiresAt = s.AbsoluteExpiresAt,
            };
        }
        return (token, id);
    }

    private Task<HttpResponseMessage> PutAgentAsync(string cookie, string mode, bool paused) =>
        SendAsync(Req(HttpMethod.Put, "/session/agent", cookie, body: new { mode, paused }));

    private async Task<JsonElement> SessionAsync(string cookie)
    {
        var r = await SendAsync(Req(HttpMethod.Get, "/session", cookie));
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<(ModelRequest First, string Body)> ChatAsync(string cookie)
    {
        _model.Requests.Clear();
        var r = await SendAsync(Req(HttpMethod.Post, "/chat/messages", cookie, body: new { message = "send 1000 to BBB" }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        return (_model.Requests[0], body);
    }

    private static string[] ToolNames(ModelRequest r) => r.Tools.Select(t => t.Name).ToArray();

    // ---- PUT /session/agent ----

    [Fact]
    public async Task Put_WithoutTheCsrfHeader_IsForbidden()
    {
        var (cookie, _) = await NewSessionAsync(true);
        var r = await SendAsync(Req(HttpMethod.Put, "/session/agent", cookie, csrf: false, body: new { mode = "analyze", paused = false }));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("prepare", (await SessionAsync(cookie)).GetProperty("agentMode").GetString());
    }

    [Fact]
    public async Task Put_FromAnUnregisteredOrigin_IsForbidden()
    {
        var (cookie, _) = await NewSessionAsync(true);
        var r = await SendAsync(Req(HttpMethod.Put, "/session/agent", cookie, origin: "https://evil.example", body: new { mode = "analyze", paused = false }));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("prepare", (await SessionAsync(cookie)).GetProperty("agentMode").GetString());
    }

    [Fact]
    public async Task Put_OnAnAnonymousSession_IsForbidden_AndAnonymousIsAlwaysAnalyze()
    {
        var (cookie, id) = await NewSessionAsync(false);
        var r = await PutAgentAsync(cookie, "prepare", false);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var state = await SessionAsync(cookie);
        Assert.Equal("analyze", state.GetProperty("agentMode").GetString());
        Assert.False(state.GetProperty("paused").GetBoolean());
        await using var db = NewContext();
        Assert.Empty(await db.EvidenceEntries.Where(e => e.Kind == "agent_mode_changed").ToListAsync());
        Assert.Equal(AgentMode.Prepare, (await db.ChatSessions.SingleAsync(s => s.Id == id)).AgentMode); // untouched default
    }

    [Fact]
    public async Task Put_WithoutASessionOrWithABadBody_IsRefused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutAgentAsync(new string('A', 43), "analyze", false)).StatusCode);
        var (cookie, _) = await NewSessionAsync(true);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Req(HttpMethod.Put, "/session/agent", cookie, body: new { mode = "analyze" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Req(HttpMethod.Put, "/session/agent", cookie, body: new { mode = "root", paused = false }))).StatusCode);
    }

    [Fact]
    public async Task GetSession_ReturnsAgentModeAndPaused_AndDefaultsToPrepareOnceSignedIn()
    {
        var (cookie, _) = await NewSessionAsync(true);
        var s = await SessionAsync(cookie);
        Assert.Equal("prepare", s.GetProperty("agentMode").GetString());
        Assert.False(s.GetProperty("paused").GetBoolean());

        var r = await PutAgentAsync(cookie, "analyze", true);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        s = await SessionAsync(cookie);
        Assert.Equal("analyze", s.GetProperty("agentMode").GetString());
        Assert.True(s.GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task EachChange_AppendsOneEvidenceEntry_AndTheChainVerifies()
    {
        var (cookie, id) = await NewSessionAsync(true);
        Assert.Equal(HttpStatusCode.OK, (await PutAgentAsync(cookie, "analyze", false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAgentAsync(cookie, "analyze", false)).StatusCode); // no change, no entry
        Assert.Equal(HttpStatusCode.OK, (await PutAgentAsync(cookie, "analyze", true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAgentAsync(cookie, "prepare", false)).StatusCode);

        await using var db = NewContext();
        var conversationId = (await db.ChatSessions.SingleAsync(s => s.Id == id)).ConversationId;
        var entries = await db.EvidenceEntries.Where(e => e.ConversationId == conversationId && e.Kind == "agent_mode_changed").OrderBy(e => e.Seq).ToListAsync();
        Assert.Equal(3, entries.Count);
        var first = JsonDocument.Parse(entries[0].Payload!).RootElement;
        Assert.Equal("prepare", first.GetProperty("old").GetProperty("mode").GetString());
        Assert.Equal("analyze", first.GetProperty("new").GetProperty("mode").GetString());
        var second = JsonDocument.Parse(entries[1].Payload!).RootElement;
        Assert.False(second.GetProperty("old").GetProperty("paused").GetBoolean());
        Assert.True(second.GetProperty("new").GetProperty("paused").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var verification = await scope.ServiceProvider.GetRequiredService<IEvidenceLog>().VerifyAsync(conversationId);
        Assert.True(verification.Valid);
    }

    // ---- model tool list and forged calls ----

    [Fact]
    public async Task InPrepareMode_FundMovingToolsAreOffered()
    {
        var (cookie, _) = await NewSessionAsync(true);
        var (req, _) = await ChatAsync(cookie);
        var names = ToolNames(req);
        foreach (var cap in FundMoving.Where(c => c != "x402.pay" && c != "limits.update"))
            Assert.Contains(PromptBuilder.WireName(cap), names);
    }

    [Theory]
    [InlineData("analyze", false, "agent_analyze_only")]
    [InlineData("prepare", true, "agent_paused")]
    [InlineData("analyze", true, "agent_paused")]
    public async Task WhenAnalyzeOrPaused_TheBedrockRequestCarriesNoFundMovingTool_AndAForgedCallIsRefused(string mode, bool paused, string reason)
    {
        var (cookie, _) = await NewSessionAsync(true);
        Assert.Equal(HttpStatusCode.OK, (await PutAgentAsync(cookie, mode, paused)).StatusCode);

        var (req, _) = await ChatAsync(cookie);
        var names = ToolNames(req);
        foreach (var cap in FundMoving) Assert.DoesNotContain(PromptBuilder.WireName(cap), names);
        // Reads keep working: balances and listings are still offered.
        Assert.Contains(PromptBuilder.WireName("wallet.balance"), names);
        Assert.Contains(PromptBuilder.WireName("safe.list"), names);
        Assert.Contains(PromptBuilder.WireName("limits.view"), names);

        // The model forged a wallet.transfer call anyway: the gate refuses it, the executor never runs.
        Assert.Equal(0, _transfer.Calls);
        Assert.Contains(_sink.Events, e => e.Kind == "tool_call_rejected" && e.ToolName == "wallet.transfer" && e.Reason == reason);
    }

    [Fact]
    public async Task Resuming_RestoresTheFundMovingTools()
    {
        var (cookie, _) = await NewSessionAsync(true);
        await PutAgentAsync(cookie, "analyze", true);
        await PutAgentAsync(cookie, "prepare", false);
        var (req, _) = await ChatAsync(cookie);
        Assert.Contains(PromptBuilder.WireName("wallet.transfer"), ToolNames(req));
        Assert.Equal(1, _transfer.Calls);
    }

    // ---- confirm and elevate ----

    private async Task<(HttpStatusCode Code, string Status)> ConfirmAsync(string cookie)
    {
        var r = await SendAsync(Req(HttpMethod.Post, $"/chat/previews/{Guid.NewGuid()}/confirm", cookie, body: new { groupHash = new string('a', 64) }));
        var json = await r.Content.ReadFromJsonAsync<JsonElement>();
        var status = json.TryGetProperty("status", out var s) ? s.GetString()! : json.TryGetProperty("error", out var e) ? e.GetString()! : "";
        return (r.StatusCode, status);
    }

    [Fact]
    public async Task Confirm_IsRefusedWithATypedError_WhileAnalyzeOrPaused_AndAllowedAgainAfterResume()
    {
        var (cookie, _) = await NewSessionAsync(true);

        // Prepare and running: the preview lookup is reached (unknown id -> NotFound), so the agent gate let it through.
        Assert.Equal((HttpStatusCode.NotFound, "NotFound"), await ConfirmAsync(cookie));

        await PutAgentAsync(cookie, "analyze", false);
        Assert.Equal((HttpStatusCode.Forbidden, "agent_analyze_only"), await ConfirmAsync(cookie));

        await PutAgentAsync(cookie, "prepare", true);
        Assert.Equal((HttpStatusCode.Forbidden, "agent_paused"), await ConfirmAsync(cookie));

        await PutAgentAsync(cookie, "prepare", false);
        Assert.Equal((HttpStatusCode.NotFound, "NotFound"), await ConfirmAsync(cookie));
    }

    [Theory]
    [InlineData("analyze", false, "agent_analyze_only")]
    [InlineData("prepare", true, "agent_paused")]
    public async Task Elevation_IsRefusedWhileAnalyzeOrPaused(string mode, bool paused, string error)
    {
        var (cookie, _) = await NewSessionAsync(true);
        await PutAgentAsync(cookie, mode, paused);
        var r = await SendAsync(Req(HttpMethod.Post, "/auth/elevate/popup", cookie, body: new { scope = "sign" }));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(error, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }
}

public class AgentGatePolicyTests
{
    private static ToolGate Gate() => new(CapabilityRegistry.CreateDefault(), new InMemoryPolicyEventSink());
    private static readonly string[] Scopes = ["openid", "sign", "manage-limits"];

    [Fact]
    public void FundMovingSet_IncludesEverythingThatBuildsASignableGroup()
    {
        var names = CapabilityRegistry.CreateDefault().All.Where(c => c.IsFundMoving).Select(c => c.Name).ToHashSet();
        foreach (var n in new[] { "wallet.transfer", "dex.swap", "safe.create", "safe.propose", "safe.approve", "safe.execute", "limits.update" })
            Assert.Contains(n, names);
        foreach (var n in new[] { "wallet.balance", "wallet.listAddresses", "safe.list", "limits.view", "kb.search" })
            Assert.DoesNotContain(n, names);
    }

    [Theory]
    [InlineData(AgentMode.Analyze, false, "agent_analyze_only")]
    [InlineData(AgentMode.Prepare, true, "agent_paused")]
    public void Gate_RefusesEveryFundMovingTool_AndSigningScopeCheck(AgentMode mode, bool paused, string reason)
    {
        var gate = Gate();
        var ctx = new GateContext(SessionTier.Authenticated, Scopes, true, true, Mode: mode, Paused: paused);
        Assert.DoesNotContain(gate.ToolsFor(ctx), c => c.IsFundMoving);
        foreach (var cap in gate.Registry.All.Where(c => c.IsFundMoving && c.ToolName is not null))
        {
            var d = gate.Check(cap.Name, new System.Text.Json.Nodes.JsonObject(), ctx);
            Assert.False(d.Allowed);
            Assert.Equal(reason, d.Reason);
            Assert.Equal(reason, gate.CheckSigningScope(cap.Name, ctx).Reason);
        }
        Assert.Contains(gate.ToolsFor(ctx), c => c.Name == "wallet.balance");
    }
}
