using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Biatec.Chat.Api.Auth.Elevation;
using Biatec.Chat.Api.Chat.Confirm;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Evidence.Recording;
using Biatec.Chat.Api.Model;
using Biatec.Chat.Api.Model.Guardrails;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Algod;
using Biatec.Chat.Tools.Mcp;
using Biatec.Chat.Tools.Signing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using AlgoEncoder = Algorand.Utils.Encoder;
using ModelMessage = Amazon.BedrockRuntime.Model.Message;

namespace Biatec.Chat.Tests.Evidence.Recording;

/// <summary>Schema-driven redaction (pure).</summary>
public sealed class EvidenceRecordingRedactionTests
{
    private static JsonObject Schema(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void EvidenceRecording_RedactsFieldsMarkedSensitive_AtAnyDepth_AndLeavesTheRest()
    {
        var schema = Schema("""
            {"type":"object","properties":{
              "balance":{"type":"string"},
              "apiKey":{"type":"string","sensitive":true},
              "owner":{"type":"object","properties":{"email":{"type":"string","sensitive":true},"name":{"type":"string"}}},
              "accounts":{"type":"array","items":{"type":"object","properties":{"seed":{"type":"string","sensitive":true},"id":{"type":"string"}}}}
            }}
            """);
        var value = JsonNode.Parse("""
            {"balance":"5","apiKey":"K-123","owner":{"email":"a@b.c","name":"Ann"},"accounts":[{"seed":"S1","id":"1"},{"seed":"S2","id":"2"}],"extra":"keep"}
            """);
        var red = SensitiveRedactor.Redact(value, schema)!.ToJsonString();
        Assert.DoesNotContain("K-123", red);
        Assert.DoesNotContain("a@b.c", red);
        Assert.DoesNotContain("S1", red);
        Assert.DoesNotContain("S2", red);
        Assert.Contains("Ann", red);
        Assert.Contains("keep", red);
        Assert.Contains("\"balance\":\"5\"", red);
        // The input is not modified.
        Assert.Contains("K-123", value!.ToJsonString());
    }

    [Fact]
    public void EvidenceRecording_SensitiveCombinedSchemas_AndWholeValue()
    {
        var schema = Schema("""{"oneOf":[{"type":"object","properties":{"token":{"sensitive":true}}}]}""");
        Assert.DoesNotContain("T0K", SensitiveRedactor.Redact(JsonNode.Parse("""{"token":"T0K","a":1}"""), schema)!.ToJsonString());
        Assert.Equal("\"[redacted]\"", SensitiveRedactor.Redact(JsonNode.Parse("\"x\""), Schema("""{"sensitive":true}"""))!.ToJsonString());
    }

    [Fact]
    public void EvidenceRecording_UnparseableTextUnderASensitiveSchema_IsWithheldWhole_AndNoSchemaPassesThrough()
    {
        var schema = Schema("""{"type":"object","properties":{"k":{"sensitive":true}}}""");
        Assert.Equal(SensitiveRedactor.Placeholder, EvidencePayloads.RedactJsonText("k=SECRET not json", schema));
        Assert.Equal("plain text", EvidencePayloads.RedactJsonText("plain text", null));
        Assert.Equal("plain text", EvidencePayloads.RedactJsonText("plain text", Schema("""{"type":"object"}""")));
    }
}

/// <summary>
/// Evidence recording across chat, previews and signing (T-035). Needs the docker-compose postgres. Bedrock, Biatec
/// OIDC, Biatec MCP and algod are fakes; the evidence log, its signing and its hash chain are real.
/// </summary>
public sealed class EvidenceRecordingTests : IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private const string Network = "algorand-testnet";
    private const string Secret = "TOP-SECRET-API-KEY";

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
    }

    private sealed class MemoryVault : ITokenVault
    {
        public readonly Dictionary<string, TokenVaultEntry> Items = new();
        public Task PutAsync(string id, TokenVaultEntry e, CancellationToken ct = default) { Items[id] = e; return Task.CompletedTask; }
        public Task<TokenVaultEntry?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task DeleteAsync(string id, CancellationToken ct = default) { Items.Remove(id); return Task.CompletedTask; }
        public Task<string?> GetAccessTokenAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id)?.AccessToken);
    }

    private sealed class FakeOidc(Account account) : IWalletSigningClient
    {
        public Task<WalletSignOutcome> SignAsync(string bearer, WalletSignRequest request, CancellationToken ct = default)
        {
            var signed = request.Transactions.Select(t => Convert.ToBase64String(AlgoEncoder.EncodeToMsgPackOrdered(
                Transaction.FromBase64String(t).Sign(account)))).ToList();
            return Task.FromResult<WalletSignOutcome>(new WalletSignOutcome.Signed(signed, "12.00"));
        }
    }

    private sealed class FakeMcp : IBiatecMcpClient
    {
        public Task<McpToolResult> SubmitTransactionToBlockchainAsync(McpCallContext ctx, string network, string signedTransactionBase64, CancellationToken ct = default) =>
            Task.FromResult(new McpToolResult(false, "", null));
        public Task<IReadOnlyList<McpToolInfo>> GetToolsAsync(McpCallContext ctx, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> ListAlgorandAddressesAsync(McpCallContext ctx, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> GetCryptoAddressAsync(McpCallContext ctx, string network, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> GetCryptoBalanceAsync(McpCallContext ctx, string network, string address, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreatePaymentTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amount, ulong? assetId = null, string? note = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateOptInTransactionAsync(McpCallContext ctx, string network, string address, ulong assetId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateBitcoinTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amountBtc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateSwapTransactionAsync(McpCallContext ctx, string network, string address, ulong fromAssetId, ulong toAssetId, string amount, string? slippage = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeConfirmer : ITransactionConfirmer
    {
        public Task<TxConfirmation> WaitAsync(string network, string txId, ulong lastValid, CancellationToken ct = default) =>
            Task.FromResult(new TxConfirmation(TxConfirmationState.Confirmed, 4242));
    }

    private sealed class AllowGuard : IToolResultGuard
    {
        public Task<GuardrailVerdict> ScreenAsync(string source, string text, CancellationToken ct) => Task.FromResult(GuardrailVerdict.Allowed);
    }

    private sealed class ScriptedModel(params Func<IEnumerable<ModelStreamEvent>>[] scripts) : IModelClient
    {
        private int _n;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var e in scripts[Math.Min(_n++, scripts.Length - 1)]())
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private sealed class BalanceExecutor : IToolExecutor
    {
        public string CapabilityName => "wallet.balance";
        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, JsonNode arguments, CancellationToken ct) =>
            Task.FromResult(new ToolExecutionResult($$"""{"balance":"5000000","apiKey":"{{Secret}}"}"""));
    }

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly Account _user = new();
    private readonly MemoryVault _vault = new();
    private ServiceProvider _sp = null!;
    private Tenant _tenant = null!;
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
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options));
        services.AddSingleton<IEvidenceKeyStore>(new DevEvidenceKeyStore());
        services.AddScoped<IEvidenceLog>(sp => new EvidenceLog(sp.GetRequiredService<ChatDbContext>(), sp.GetRequiredService<IEvidenceKeyStore>()));
        services.AddScoped<IEvidenceRecorder, EvidenceRecorder>();
        _sp = services.BuildServiceProvider();
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await db.Database.MigrateAsync();
        _tenant = Biatec.Chat.Api.Tenancy.TenantSeed.BiatecChat();
        db.Tenants.Add(_tenant);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private static PaymentTransaction Pay(Address sender, ulong amount = 5_000_000) => new()
    {
        Sender = sender,
        Receiver = new Address("KV2XGKMXGYJ6PWYQA5374BYIQBL3ONRMSIARPCFCJEAMAHQEVYPB7PL3KU"),
        Amount = amount, Fee = 1000, FirstValid = 710399, LastValid = 711399,
        GenesisId = "testnet-v1.0",
        GenesisHash = new Digest(Convert.FromBase64String("SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=")),
        Note = Encoding.UTF8.GetBytes("t"),
    };

    [Fact]
    public async Task EvidenceRecording_TransferConversation_ProducesEntriesOfEveryKind_Redacted_WithoutIps_AndTheChainVerifies()
    {
        await using var scope = _sp.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ChatDbContext>();
        var recorder = sp.GetRequiredService<IEvidenceRecorder>();
        var evidenceLog = sp.GetRequiredService<IEvidenceLog>();

        // 1. Session metadata.
        var handle = await new SessionService(db, _clock, recorder).ResolveOrCreateAsync(_tenant, null, CancellationToken.None);
        var session = handle.Session;
        db.ChangeTracker.Clear();
        await db.ChatSessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.Tier, SessionTier.Authenticated).SetProperty(s => s.OidcSub, "user-1"));
        session.Tier = SessionTier.Authenticated;
        var conversationId = session.ConversationId;

        // 2. Chat: a tool call whose output schema marks apiKey sensitive, and a call the gate rejects.
        var balanceCap = CapabilityRegistry.CreateDefault().Find("wallet.balance")! with
        {
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"balance":{"type":"string"},"apiKey":{"type":"string","sensitive":true}}}""")!.AsObject(),
        };
        var registry = new CapabilityRegistry([balanceCap]);
        var gate = new ToolGate(registry, new EvidencePolicyEventSink(_sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<EvidencePolicyEventSink>.Instance));
        var model = new ScriptedModel(
            () => ToolCalls(("t1", "wallet_balance", """{"network":"testnet","address":"AAAA"}"""), ("t2", "no_such_tool", "{}")),
            () => Text("Your balance is 5 ALGO."),
            () => Text("Prepared the transfer."));
        var orchestrator = new ConversationOrchestrator(model, gate, [new BalanceExecutor()], new DefaultMessageRedactor(), new AllowGuard(), db, _clock,
            NullLogger<ConversationOrchestrator>.Instance, recorder);
        var turn = new ChatTurn(conversationId, session.Id.ToString("N"), _tenant, SessionTier.Authenticated, ["openid"], "What is my balance? Then send 5 ALGO.", null, true);
        await foreach (var _ in orchestrator.RunAsync(turn)) { }

        // 3. Preview, confirm, signing and submission.
        var group = AlgorandGroup.Ensure([Pay(_user.Address)]);
        var previews = new ActionPreviewService(db, _clock, recorder);
        var snap = await previews.CreateAsync(new NewPreviewSpec(conversationId, "wallet.transfer", "{\"intent\":\"send\"}",
        [
            new NewStepSpec(ActionStepKind.Sign, group.GroupHash, _clock.GetUtcNow().AddSeconds(1500), UnsignedGroup: JsonSerializer.Serialize(group.ToBase64()),
                SenderAddress: _user.Address.EncodeAsString(), Network: Network),
        ]));
        _vault.Items[session.Id.ToString("N")] = new TokenVaultEntry
        {
            AccessToken = "ACCESS-TOKEN-SECRET", AccessExpiresAt = _clock.GetUtcNow().AddMinutes(10), RefreshToken = "REFRESH-TOKEN-SECRET",
            Scopes = ["openid", "sign"], AbsoluteExpiresAt = session.AbsoluteExpiresAt,
        };
        var confirm = new ConfirmService(db, previews, new ScopeGuard(_vault, _clock), _vault, new FakeOidc(_user), new FakeMcp(), new FakeConfirmer(),
            evidenceLog, _clock, NullLogger<ConfirmService>.Instance, recorder: recorder);
        var result = await confirm.ConfirmAsync(_tenant, session, snap.Preview.Id, group.GroupHash, Biatec.Chat.Api.Chat.Confirm.ConfirmService.BackendOrigin(_tenant), CancellationToken.None);
        Assert.Equal(ConfirmOutcome.Landed, result.Outcome);

        // Every listed kind is there.
        var chain = await evidenceLog.GetChainAsync(conversationId);
        var kinds = chain.Select(e => e.Kind).ToList();
        foreach (var expected in new[]
        {
            EvidenceKinds.SessionStarted, EvidenceKinds.UserMessage, EvidenceKinds.AssistantMessage, EvidenceKinds.ToolCall, EvidenceKinds.ToolResult,
            EvidenceKinds.PolicyRejection, EvidenceKinds.ActionPreview, EvidenceKinds.UserConfirmed, EvidenceKinds.SigningRequested,
            EvidenceKinds.SigningOutcome, "action_submitted", "action_receipt",
        })
            Assert.Contains(expected, kinds);

        Assert.Equal(EvidenceKinds.SessionStarted, kinds[0]);
        // Order of the money path: preview, confirmation, signing request, signing outcome, submission, receipt.
        var money = kinds.Where(k => k is EvidenceKinds.ActionPreview or EvidenceKinds.UserConfirmed or EvidenceKinds.SigningRequested
            or EvidenceKinds.SigningOutcome or "action_submitted" or "action_receipt").ToList();
        Assert.Equal([EvidenceKinds.ActionPreview, EvidenceKinds.UserConfirmed, EvidenceKinds.SigningRequested, EvidenceKinds.SigningOutcome, "action_submitted", "action_receipt"], money);

        var payloads = chain.Select(e => e.Payload ?? "").ToList();
        var all = string.Concat(payloads);

        // The sensitive field is gone from evidence and from the stored transcript; the rest of the result is kept.
        Assert.DoesNotContain(Secret, all);
        Assert.DoesNotContain(Secret, string.Concat((await db.Messages.AsNoTracking().ToListAsync()).Select(m => (m.ToolResults ?? "") + m.Content + (m.ToolCalls ?? ""))));
        Assert.Contains("5000000", all);
        Assert.Contains(SensitiveRedactor.Placeholder, all);
        // Tokens never reach evidence; previews and steps carry the group hash; the policy event names the tool, not its arguments.
        Assert.DoesNotContain("TOKEN-SECRET", all);
        Assert.Contains(group.GroupHash, payloads[kinds.IndexOf(EvidenceKinds.ActionPreview)]);
        var rejection = JsonNode.Parse(payloads[kinds.IndexOf(EvidenceKinds.PolicyRejection)])!.AsObject();
        Assert.Equal("unknown_tool", rejection["reason"]!.GetValue<string>());
        Assert.Equal("12.00", JsonNode.Parse(payloads[kinds.IndexOf(EvidenceKinds.SigningOutcome)])!["valuedEur"]!.GetValue<string>());

        // No IP addresses anywhere.
        Assert.DoesNotMatch(new Regex(@"(?<![\d.])(\d{1,3}\.){3}\d{1,3}(?![\d.])"), all);
        Assert.DoesNotMatch(new Regex(@"(?i)\b(ip|ipaddress|remoteaddr|x-forwarded-for|user-?agent)\b"), all);

        // Concurrent-safe, signed and hash-chained like any other entry.
        Assert.Equal(Enumerable.Range(1, chain.Count).Select(i => (long)i), chain.Select(e => e.Seq));
        Assert.True((await evidenceLog.VerifyAsync(conversationId)).Valid);
    }

    private sealed class ScriptedOidc(WalletSignOutcome outcome) : IWalletSigningClient
    {
        public Task<WalletSignOutcome> SignAsync(string bearer, WalletSignRequest request, CancellationToken ct = default) => Task.FromResult(outcome);
    }

    private static readonly Regex IpPattern = new(@"(?<![\d.])(\d{1,3}\.){3}\d{1,3}(?![\d.])");

    private async Task<(ChatSession Session, Guid ConversationId)> NewSessionAsync(ChatDbContext db, IEvidenceRecorder recorder)
    {
        var handle = await new SessionService(db, _clock, recorder).ResolveOrCreateAsync(_tenant, null, CancellationToken.None);
        var session = handle.Session;
        db.ChangeTracker.Clear();
        await db.ChatSessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.Tier, SessionTier.Authenticated).SetProperty(s => s.OidcSub, "user-1"));
        session.Tier = SessionTier.Authenticated;
        return (session, session.ConversationId);
    }

    private NewStepSpec SignStep(GroupedTransactions group) =>
        new(ActionStepKind.Sign, group.GroupHash, _clock.GetUtcNow().AddSeconds(1500), UnsignedGroup: JsonSerializer.Serialize(group.ToBase64()),
            SenderAddress: _user.Address.EncodeAsString(), Network: Network);

    private static async Task<JsonObject> EntryAsync(IEvidenceLog log, Guid conversationId, string kind, bool last = false)
    {
        var chain = await log.GetChainAsync(conversationId);
        var matches = chain.Where(e => e.Kind == kind).ToList();
        Assert.NotEmpty(matches);
        var payload = (last ? matches[^1] : matches[0]).Payload!;
        Assert.DoesNotMatch(IpPattern, payload);
        return JsonNode.Parse(payload)!.AsObject();
    }

    [Fact]
    public async Task EvidenceRecording_AddNextStep_AndRebuild_AreRecorded_WithRebuiltFrom_AndNoIps()
    {
        await using var scope = _sp.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ChatDbContext>();
        var recorder = sp.GetRequiredService<IEvidenceRecorder>();
        var log = sp.GetRequiredService<IEvidenceLog>();
        var (_, conversationId) = await NewSessionAsync(db, recorder);
        var previews = new ActionPreviewService(db, _clock, recorder);

        // Multi-step: step 0 lands, then the next step is added.
        var g0 = AlgorandGroup.Ensure([Pay(_user.Address)]);
        var snap = await previews.CreateAsync(new NewPreviewSpec(conversationId, "wallet.transfer", "{\"intent\":\"send\"}", [SignStep(g0)]));
        await previews.ConfirmAsync(conversationId, snap.Preview.Id, g0.GroupHash);
        await previews.MarkStepLandedAsync(conversationId, snap.Preview.Id, 0, ["TXID0"]);
        var g1 = AlgorandGroup.Ensure([Pay(_user.Address, 7_000_000)]);
        await previews.AddNextStepAsync(conversationId, snap.Preview.Id, SignStep(g1));
        var added = await EntryAsync(log, conversationId, EvidenceKinds.ActionStepAdded);
        Assert.Equal(snap.Preview.Id.ToString(), added["previewId"]!.GetValue<string>());
        Assert.Contains(g1.GroupHash, added["step"]!.ToJsonString());

        // Rebuild of a second, still-proposed preview.
        var g2 = AlgorandGroup.Ensure([Pay(_user.Address, 9_000_000)]);
        var old = await previews.CreateAsync(new NewPreviewSpec(conversationId, "wallet.transfer", "{\"intent\":\"send2\"}", [SignStep(g2)]));
        var g3 = AlgorandGroup.Ensure([Pay(_user.Address, 9_500_000)]);
        var rebuilt = await previews.RebuildAsync(conversationId, old.Preview.Id, "{\"intent\":\"send2\"}", [SignStep(g3)]);
        var entry = await EntryAsync(log, conversationId, EvidenceKinds.ActionPreview, last: true);
        Assert.Equal(old.Preview.Id.ToString(), entry["rebuiltFrom"]!.GetValue<string>());
        Assert.Contains(g3.GroupHash, entry.ToJsonString());
        Assert.Equal(rebuilt.Preview.Id.ToString(), entry["previewId"]!.GetValue<string>());
        Assert.True((await log.VerifyAsync(conversationId)).Valid);
    }

    [Theory]
    [InlineData("consent_required")]
    [InlineData("rejected")]
    [InlineData("invalid_token")]
    [InlineData("unavailable")]
    public async Task EvidenceRecording_NonSignedSigningOutcomes_AreRecorded_WithoutIps(string kind)
    {
        await using var scope = _sp.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ChatDbContext>();
        var recorder = sp.GetRequiredService<IEvidenceRecorder>();
        var log = sp.GetRequiredService<IEvidenceLog>();
        var (session, conversationId) = await NewSessionAsync(db, recorder);
        var previews = new ActionPreviewService(db, _clock, recorder);
        var group = AlgorandGroup.Ensure([Pay(_user.Address)]);
        var snap = await previews.CreateAsync(new NewPreviewSpec(conversationId, "wallet.transfer", "{\"intent\":\"send\"}", [SignStep(group)]));
        _vault.Items[session.Id.ToString("N")] = new TokenVaultEntry
        {
            AccessToken = "ACCESS-TOKEN-SECRET", AccessExpiresAt = _clock.GetUtcNow().AddMinutes(10), RefreshToken = "REFRESH-TOKEN-SECRET",
            Scopes = ["openid", "sign"], AbsoluteExpiresAt = session.AbsoluteExpiresAt,
        };
        WalletSignOutcome outcome = kind switch
        {
            "consent_required" => new WalletSignOutcome.StepUp("auth-req-1", "1234", 120, 5),
            "rejected" => new WalletSignOutcome.Rejected(403, "daily_cap_exceeded"),
            "invalid_token" => new WalletSignOutcome.Rejected(401, "invalid_token"),
            _ => new WalletSignOutcome.Unavailable("oidc_down"),
        };
        var confirm = new ConfirmService(db, previews, new ScopeGuard(_vault, _clock), _vault, new ScriptedOidc(outcome), new FakeMcp(), new FakeConfirmer(),
            log, _clock, NullLogger<ConfirmService>.Instance, recorder: recorder);
        await confirm.ConfirmAsync(_tenant, session, snap.Preview.Id, group.GroupHash, Biatec.Chat.Api.Chat.Confirm.ConfirmService.BackendOrigin(_tenant), CancellationToken.None);

        var o = await EntryAsync(log, conversationId, EvidenceKinds.SigningOutcome);
        Assert.Equal(snap.Preview.Id.ToString(), o["previewId"]!.GetValue<string>());
        Assert.Equal(group.GroupHash, o["groupHash"]!.GetValue<string>());
        switch (kind)
        {
            case "consent_required":
                Assert.Equal("consent_required", o["result"]!.GetValue<string>());
                Assert.Equal(120, o["expiresInSeconds"]!.GetValue<int>());
                Assert.DoesNotContain("1234", o.ToJsonString()); // the binding code is not evidence
                await EntryAsync(log, conversationId, "consent_requested");
                break;
            case "rejected":
                Assert.Equal("rejected", o["result"]!.GetValue<string>());
                Assert.Equal("daily_cap_exceeded", o["error"]!.GetValue<string>());
                Assert.Equal(403, o["status"]!.GetValue<int>());
                break;
            case "invalid_token":
                Assert.Equal("rejected", o["result"]!.GetValue<string>());
                Assert.Equal("invalid_token", o["error"]!.GetValue<string>());
                break;
            default:
                Assert.Equal("unavailable", o["result"]!.GetValue<string>());
                break;
        }
        var all = string.Concat((await log.GetChainAsync(conversationId)).Select(e => e.Payload));
        Assert.DoesNotContain("TOKEN-SECRET", all);
        Assert.DoesNotMatch(IpPattern, all);
        Assert.True((await log.VerifyAsync(conversationId)).Valid);
    }

    [Fact]
    public async Task EvidenceRecording_AFailingEvidenceLog_NeverBreaksTheCaller()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options));
        services.AddScoped<IEvidenceLog>(_ => throw new InvalidOperationException("no key store"));
        services.AddScoped<IEvidenceRecorder, EvidenceRecorder>();
        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IEvidenceRecorder>().RecordAsync(Guid.NewGuid(), EvidenceKinds.UserMessage, new JsonObject { ["text"] = "hi" });
    }

    private static IEnumerable<ModelStreamEvent> Text(string text)
    {
        yield return new ModelTextDelta(text);
        yield return new ModelMessageStop("end_turn", new ModelMessage { Role = ConversationRole.Assistant, Content = [new ContentBlock { Text = text }] });
    }

    private static IEnumerable<ModelStreamEvent> ToolCalls(params (string Id, string Name, string Input)[] calls)
    {
        foreach (var c in calls) yield return new ModelToolUse(c.Id, c.Name, c.Input);
        yield return new ModelMessageStop("tool_use", new ModelMessage
        {
            Role = ConversationRole.Assistant,
            Content = calls.Select(c => new ContentBlock { ToolUse = new ToolUseBlock { ToolUseId = c.Id, Name = c.Name, Input = Biatec.Chat.Api.Model.DocumentJson.FromJson(c.Input) } }).ToList(),
        });
    }
}
