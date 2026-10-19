using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Biatec.Chat.Api.Auth.Elevation;
using Biatec.Chat.Api.Chat.Confirm;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Safe.Create;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Builders;
using Biatec.Chat.Tools.Mcp;
using Biatec.Chat.Tools.Safe.Create;
using Biatec.Chat.Tools.Signing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using AlgoEncoder = Algorand.Utils.Encoder;
using ActionPreviewStatus = Biatec.Chat.Core.Data.ActionPreviewStatus;
using ActionPreview = Biatec.Chat.Api.Contracts.ActionPreview;
using CardStatus = Biatec.Chat.Api.Contracts.ActionPreviewStatus;
using SessionTier = Biatec.Chat.Core.Data.SessionTier;

namespace Biatec.Chat.Tests.Safe.Create;

/// <summary>Real algo-safe output captured from the builder sidecar (builders/sidecar, fake algod), so the C# checks are tested against the true wire shape.</summary>
internal static class SidecarFixture
{
    public static JsonElement Load([CallerFilePath] string here = "") =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", "sidecar-safe-create.json"))).RootElement.Clone();

    public static BuildResult Result(JsonElement step) => new(
        step.GetProperty("transactions").EnumerateArray().Select(t => t.GetString()!).ToList(),
        Preview: step.GetProperty("preview").Clone(), Simulation: step.TryGetProperty("simulation", out var s) ? s.Clone() : null);
}

/// <summary>SafeBuilder against the sidecar's real safe.create output: what is accepted and what is refused.</summary>
public sealed class SafeCreateBuilderTests
{
    private static readonly JsonElement Fx = SidecarFixture.Load();
    private static string Alice => Fx.GetProperty("alice").GetString()!;
    private static string Bob => Fx.GetProperty("bob").GetString()!;

    private sealed class OneShot(Func<string, JsonElement, BuildResult> build) : IBuildersClient
    {
        public readonly List<JsonElement> Params = [];
        public Task<bool> IsHealthyAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<BuildResult> BuildAsync(string builder, string network, object? parameters, CancellationToken ct = default)
        {
            var p = JsonSerializer.SerializeToElement(parameters);
            Params.Add(p);
            return Task.FromResult(build(builder, p));
        }
    }

    [Fact]
    public async Task SafeCreate_builder_accepts_the_sidecars_deploy_call_and_sends_name_and_simulate()
    {
        var client = new OneShot((_, _) => SidecarFixture.Result(Fx.GetProperty("deploy")));
        var b = await new SafeBuilder(client).BuildDeployAsync("testnet", Alice, "Team Safe");

        var tx = Assert.Single(b.Group.Transactions);
        Assert.IsType<ApplicationCreateTransaction>(tx);
        Assert.Equal(Alice, tx.Sender.EncodeAsString());
        Assert.Equal(4242UL, b.ValidatorAppId);
        Assert.True(b.Simulation!.Ok);
        var p = Assert.Single(client.Params);
        Assert.Equal("deploy", p.GetProperty("step").GetString());
        Assert.Equal("Team Safe", p.GetProperty("name").GetString());
        Assert.True(p.GetProperty("simulate").GetBoolean());
    }

    [Fact]
    public async Task SafeCreate_builder_accepts_the_sidecars_bootstrap_group_for_the_requested_signers()
    {
        var client = new OneShot((_, _) => SidecarFixture.Result(Fx.GetProperty("bootstrap")));
        var b = await new SafeBuilder(client).BuildBootstrapAsync("testnet", Alice, 778, "Signers", [Alice, Bob], 2);

        Assert.Equal(2, b.Group.Transactions.Count);
        Assert.All(b.Group.Transactions, t => Assert.Equal(Alice, t.Sender.EncodeAsString()));
        var p = Assert.Single(client.Params);
        Assert.Equal("778", p.GetProperty("appId").GetString());
        var g = p.GetProperty("groups")[0];
        Assert.Equal(2, g.GetProperty("threshold").GetInt32());
        // Payments, asset transfers and app calls (the demo's liquidity proposal) are allowed.
        Assert.Equal(7, g.GetProperty("allowedActions").GetInt32());
        Assert.Equal([Alice, Bob], g.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("address").GetString()));
    }

    [Fact]
    public async Task SafeCreate_builder_refuses_a_group_that_differs_from_the_request()
    {
        var builder = new SafeBuilder(new OneShot((_, _) => SidecarFixture.Result(Fx.GetProperty("bootstrap"))));
        var carol = new Account().Address.EncodeAsString();
        // Different signers, threshold, safe or sender: nothing is accepted.
        await Assert.ThrowsAsync<SafeBuildException>(() => builder.BuildBootstrapAsync("testnet", Alice, 778, "Signers", [Alice, carol], 2));
        await Assert.ThrowsAsync<SafeBuildException>(() => builder.BuildBootstrapAsync("testnet", Alice, 778, "Signers", [Alice, Bob], 1));
        await Assert.ThrowsAsync<SafeBuildException>(() => builder.BuildBootstrapAsync("testnet", Alice, 779, "Signers", [Alice, Bob], 2));
        await Assert.ThrowsAsync<SafeBuildException>(() => builder.BuildBootstrapAsync("testnet", Bob, 778, "Signers", [Alice, Bob], 2));

        // A deploy step answered with the bootstrap group is refused; so is a builder that values the creation above zero.
        await Assert.ThrowsAsync<SafeBuildException>(() => builder.BuildDeployAsync("testnet", Alice, "Team Safe"));
        var valued = JsonNode.Parse(Fx.GetProperty("deploy").GetRawText())!;
        valued["preview"]!["valueMicroAlgo"] = "5";
        var priced = new SafeBuilder(new OneShot((_, _) => SidecarFixture.Result(JsonDocument.Parse(valued.ToJsonString()).RootElement)));
        await Assert.ThrowsAsync<SafeBuildException>(() => priced.BuildDeployAsync("testnet", Alice, "Team Safe"));
    }
}

/// <summary>
/// safe.create and safe.list (T-032) end to end with fakes: address collection, the zero-valued two-step preview,
/// the shared confirm path, funding, app id and safe address, and the safe card with pending proposals. The builder
/// sidecar, Biatec MCP, Biatec OIDC and algod are fakes; needs the docker-compose postgres.
/// </summary>
public sealed class SafeCreateFlowTests : IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private static readonly JsonElement Fx = SidecarFixture.Load();
    private const ulong AppId = 778;

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
        public readonly List<WalletSignRequest> Calls = [];
        public Task<WalletSignOutcome> SignAsync(string bearer, WalletSignRequest request, CancellationToken ct = default)
        {
            Calls.Add(request);
            var signed = request.Transactions.Select(t => Convert.ToBase64String(AlgoEncoder.EncodeToMsgPackOrdered(Transaction.FromBase64String(t).Sign(account)))).ToList();
            return Task.FromResult<WalletSignOutcome>(new WalletSignOutcome.Signed(signed, "0.00"));
        }
    }

    private sealed class FakeConfirmer : ITransactionConfirmer
    {
        public Task<TxConfirmation> WaitAsync(string network, string txId, ulong lastValid, CancellationToken ct = default) =>
            Task.FromResult(new TxConfirmation(TxConfirmationState.Confirmed, 4242));
    }

    private sealed class FakeEvidence : IEvidenceLog
    {
        public Task<EvidenceEntry> AppendAsync(Guid conversationId, string kind, JsonObject payload, Guid? anchorRef = null, CancellationToken ct = default) => Task.FromResult(new EvidenceEntry());
        public Task<bool> DeletePayloadAsync(Guid conversationId, long seq, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeleteAllPayloadsAsync(Guid conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EvidenceEntry>> GetChainAsync(Guid conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EvidenceVerification> VerifyAsync(Guid conversationId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeMcp(Func<IReadOnlyList<string>> addresses) : IBiatecMcpClient
    {
        public readonly List<string> Submits = [];
        public Task<McpToolResult> ListAlgorandAddressesAsync(McpCallContext ctx, CancellationToken ct = default) =>
            Task.FromResult(new McpToolResult(false, "", JsonSerializer.SerializeToElement(addresses())));
        public Task<McpToolResult> SubmitTransactionToBlockchainAsync(McpCallContext ctx, string network, string blob, CancellationToken ct = default)
        {
            Submits.Add(blob);
            return Task.FromResult(new McpToolResult(false, "", null));
        }
        public Task<McpToolResult> GetCryptoBalanceAsync(McpCallContext ctx, string network, string a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpToolInfo>> GetToolsAsync(McpCallContext ctx, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> GetCryptoAddressAsync(McpCallContext ctx, string network, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreatePaymentTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amount, ulong? assetId = null, string? note = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateOptInTransactionAsync(McpCallContext ctx, string network, string address, ulong assetId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateBitcoinTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amountBtc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateSwapTransactionAsync(McpCallContext ctx, string network, string address, ulong fromAssetId, ulong toAssetId, string amount, string? slippage = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// The sidecar: safe.create answers with the captured algo-safe groups re-addressed to the test user (so the fake
    /// OIDC can sign them), safe.list and safe.proposals with chain state the test sets.
    /// </summary>
    private sealed class FakeBuilders(Func<string> sender) : IBuildersClient
    {
        public readonly List<(string Name, JsonElement Params)> Builds = [];
        public readonly List<(string Name, JsonElement Params)> Reads = [];
        public bool SimulationFails;
        /// <summary>appId → members, as on chain.</summary>
        public readonly Dictionary<ulong, HashSet<string>> Members = new();
        public JsonArray Proposals = [];

        public Task<bool> IsHealthyAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<BuildResult> BuildAsync(string builder, string network, object? parameters, CancellationToken ct = default)
        {
            var p = JsonSerializer.SerializeToElement(parameters);
            Builds.Add((builder, p));
            Assert.Equal("safe.create", builder);
            var step = p.GetProperty("step").GetString()!;
            var fx = Fx.GetProperty(step == "deploy" ? "deploy" : "bootstrap");
            var who = new Address(sender());
            // Re-address the captured algo-safe transactions to the user; the SDK assigns the new group id.
            var txns = fx.GetProperty("transactions").EnumerateArray().Select(t =>
            {
                var tx = Transaction.FromBase64String(t.GetString()!);
                tx.Sender = who;
                tx.Group = null;
                // A validity window long enough for the 600 second confirm gate.
                tx.FirstValid = 1000;
                tx.LastValid = 2000;
                return tx;
            }).ToList();
            var grouped = Biatec.Chat.Tools.Algod.AlgorandGroup.Ensure(txns).Transactions;
            var preview = JsonNode.Parse(fx.GetProperty("preview").GetRawText())!.AsObject();
            if (step == "bootstrap")
            {
                var g = p.GetProperty("groups")[0];
                preview["appId"] = p.GetProperty("appId").GetString();
                preview["groups"] = new JsonArray(new JsonObject
                {
                    ["name"] = g.GetProperty("name").GetString(),
                    ["threshold"] = g.GetProperty("threshold").GetInt32().ToString(),
                    ["members"] = new JsonArray(g.GetProperty("members").EnumerateArray().Select(m => (JsonNode?)JsonValue.Create(m.GetProperty("address").GetString())).ToArray()),
                });
            }
            else preview["name"] = p.GetProperty("name").GetString();
            var sim = new JsonObject { ["ok"] = !SimulationFails, ["failureMessage"] = SimulationFails ? "logic eval error: below min balance" : null };
            return Task.FromResult(new BuildResult(grouped.Select(t => Convert.ToBase64String(AlgoEncoder.EncodeToMsgPackOrdered(t))).ToList(),
                Preview: JsonDocument.Parse(preview.ToJsonString()).RootElement.Clone(), Simulation: JsonDocument.Parse(sim.ToJsonString()).RootElement.Clone()));
        }

        public Task<JsonElement> ReadAsync(string builder, string network, object? parameters, CancellationToken ct = default)
        {
            var p = JsonSerializer.SerializeToElement(parameters);
            Reads.Add((builder, p));
            JsonObject data;
            if (builder == "safe.list")
            {
                var member = p.GetProperty("member").GetString()!;
                var safes = new JsonArray();
                foreach (var id in p.GetProperty("appIds").EnumerateArray().Select(x => ulong.Parse(x.GetString()!)))
                    if (Members.TryGetValue(id, out var m) && m.Contains(member))
                        safes.Add(new JsonObject
                        {
                            ["appId"] = id.ToString(), ["address"] = SafeCreateService.SafeAddress(id), ["name"] = "Team Safe", ["paused"] = false,
                            ["groups"] = new JsonArray(new JsonObject
                            {
                                ["id"] = "1", ["name"] = "Signers", ["threshold"] = 2, ["memberCount"] = m.Count,
                                ["allowedActions"] = new JsonArray("payment", "assetTransfer", "appCall"), ["isAdminGroup"] = true, ["membershipEpoch"] = 1,
                            }),
                        });
                data = new JsonObject { ["member"] = member, ["safes"] = safes, ["skipped"] = new JsonArray() };
            }
            else
            {
                Assert.Equal("safe.proposals", builder);
                var id = ulong.Parse(p.GetProperty("appId").GetString()!);
                data = new JsonObject { ["appId"] = id.ToString(), ["safeAddress"] = SafeCreateService.SafeAddress(id), ["proposals"] = Proposals.DeepClone(), ["truncated"] = false };
            }
            return Task.FromResult(JsonDocument.Parse(data.ToJsonString()).RootElement.Clone());
        }
    }

    private sealed class FakeChain : ISafeChainReader
    {
        public ulong? CreatedAppId = AppId;
        public ulong Balance;
        public int AppIdLookups;
        public Task<ulong?> CreatedAppIdAsync(string network, string txId, CancellationToken ct = default) { AppIdLookups++; return Task.FromResult(CreatedAppId); }
        public Task<SafeAccountBalance?> BalanceAsync(string network, string address, CancellationToken ct = default) =>
            Task.FromResult<SafeAccountBalance?>(new SafeAccountBalance(Balance, 100_000));
    }

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly Account _alice = new();
    private readonly string _bob = new Account().Address.EncodeAsString();
    private readonly MemoryVault _vault = new();
    private readonly FakeChain _chain = new();
    private readonly CapabilityRegistry _registry = CapabilityRegistry.CreateDefault();
    private List<string> _own = [];
    private FakeMcp _mcp = null!;
    private FakeBuilders _builders = null!;
    private FakeOidc _oidc = null!;
    private Tenant _tenant = null!;
    private ChatSession _session = null!;
    private ServiceProvider _sp = null!;
    private SafeCreateExecutor _create = null!;
    private SafeListExecutor _list = null!;

    private string Alice => _alice.Address.EncodeAsString();

    private ChatDbContext NewContext()
    {
        var cs = new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;
        return new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(cs).Options);
    }

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
        await using (var db = NewContext())
        {
            await db.Database.MigrateAsync();
            _tenant = Biatec.Chat.Api.Tenancy.TenantSeed.BiatecChat();
            db.Tenants.Add(_tenant);
            var conv = new Conversation { Id = Guid.NewGuid(), TenantId = _tenant.Id, OidcSub = "alice", CreatedAt = _clock.GetUtcNow(), LastMessageAt = _clock.GetUtcNow() };
            _session = new ChatSession
            {
                Id = Guid.NewGuid(), ConversationId = conv.Id, Tier = SessionTier.Authenticated, TenantId = _tenant.Id, OidcSub = "alice",
                CreatedAt = _clock.GetUtcNow(), LastActivityAt = _clock.GetUtcNow(), AbsoluteExpiresAt = _clock.GetUtcNow().AddHours(8),
            };
            db.Conversations.Add(conv);
            db.ChatSessions.Add(_session);
            await db.SaveChangesAsync();
        }
        _vault.Items[_session.Id.ToString("N")] = new TokenVaultEntry
        {
            AccessToken = "ACCESS-TOKEN-SECRET", AccessExpiresAt = _clock.GetUtcNow().AddMinutes(10), RefreshToken = "REFRESH-TOKEN-SECRET",
            Scopes = ["openid", "sign"], AbsoluteExpiresAt = _session.AbsoluteExpiresAt,
        };
        _own = [Alice];
        _mcp = new FakeMcp(() => _own);
        _builders = new FakeBuilders(() => _own[0]);
        _oidc = new FakeOidc(_alice);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddScoped(_ => NewContext());
        services.AddScoped<ActionPreviewService>();
        services.AddSingleton<IBiatecMcpClient>(_mcp);
        services.AddSingleton<ISafeBuilder>(new SafeBuilder(_builders));
        services.AddSingleton<ISafeChainReader>(_chain);
        services.AddSingleton(new SafeCreateOptions { MinimumFundingMicroAlgo = 500_000 });
        services.AddScoped<SafeCreateService>();
        services.AddScoped<SafeListService>();
        _sp = services.BuildServiceProvider();
        _create = new SafeCreateExecutor(_sp.GetRequiredService<IServiceScopeFactory>());
        _list = new SafeListExecutor(_sp.GetRequiredService<IServiceScopeFactory>());
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private ToolExecutionContext Ctx(string capability, SessionTier tier = SessionTier.Authenticated) =>
        new(_registry.Find(capability)!, "tu-1", _session.ConversationId, _session.Id.ToString("N"), _tenant.Id, tier, ["openid", "sign"], _tenant);

    private JsonNode Args(string? name = "Team Safe", string[]? signers = null, int? threshold = 2, string? from = null)
    {
        var o = new JsonObject { ["network"] = "testnet", ["from"] = from ?? Alice };
        if (name is not null) o["name"] = name;
        if (signers is not null) o["signers"] = new JsonArray(signers.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        if (threshold is not null) o["threshold"] = threshold;
        return o;
    }

    private JsonNode Full() => Args(signers: [_bob]);

    private async Task<ConfirmResult> ConfirmAsync(ActionPreview card, int step)
    {
        await using var db = NewContext();
        var confirm = new ConfirmService(db, new ActionPreviewService(db, _clock), new ScopeGuard(_vault, _clock), _vault, _oidc, _mcp, new FakeConfirmer(), new FakeEvidence(), _clock, NullLogger<ConfirmService>.Instance);
        return await confirm.ConfirmAsync(_tenant, _session, Guid.Parse(card.PreviewId), card.Steps[step].GroupHash, default);
    }

    private static ActionPreview Card(ToolExecutionResult r) => Assert.IsType<PreviewCardEvent>(Assert.Single(r.Events!)).Preview;

    // ---- address collection -------------------------------------------------------------------------------------

    [Fact]
    public async Task SafeCreate_without_details_asks_for_name_signers_and_threshold_and_builds_nothing()
    {
        var r = await _create.ExecuteAsync(Ctx("safe.create"), Args(name: null, threshold: null), default);

        Assert.False(r.IsError);
        Assert.Null(r.Events);
        Assert.Empty(_builders.Builds);
        var o = JsonNode.Parse(r.Content)!;
        Assert.Equal("needs_details", o["status"]!.GetValue<string>());
        var text = o["instruction"]!.GetValue<string>();
        Assert.Contains("name", text);
        Assert.Contains("address of every other signer", text);
        Assert.Contains("threshold", text);
        Assert.Contains("Never invent", text);
    }

    [Fact]
    public async Task SafeCreate_refuses_bad_signers_thresholds_and_foreign_addresses()
    {
        var bad = await _create.ExecuteAsync(Ctx("safe.create"), Args(signers: ["NOT-AN-ADDRESS"]), default);
        Assert.True(bad.IsError);
        Assert.Contains("valid Algorand address", bad.Content);

        // 3-of-2 is impossible; the creator counts as a signer and duplicates collapse.
        var tooHigh = await _create.ExecuteAsync(Ctx("safe.create"), Args(signers: [_bob, _bob, Alice], threshold: 3), default);
        Assert.True(tooHigh.IsError);
        Assert.Contains("between 1 and the number of signers (2", tooHigh.Content);

        var foreign = await _create.ExecuteAsync(Ctx("safe.create"), Args(signers: [Alice], from: _bob), default);
        Assert.True(foreign.IsError);
        Assert.Contains("does not belong", foreign.Content);

        var anon = await _create.ExecuteAsync(Ctx("safe.create", SessionTier.Anonymous), Full(), default);
        Assert.True(anon.IsError);
        Assert.Empty(_builders.Builds);
    }

    [Fact]
    public async Task SafeCreate_schema_takes_signers_and_threshold()
    {
        var schema = _registry.Find("safe.create")!.InputSchema!;
        Assert.Empty(JsonSchemaValidator.Validate(schema, Full()));
        Assert.NotEmpty(JsonSchemaValidator.Validate(schema, Args(signers: [_bob], threshold: 0)));
        Assert.Empty(JsonSchemaValidator.Validate(_registry.Find("safe.list")!.InputSchema!, new JsonObject { ["network"] = "testnet", ["safeAppIds"] = new JsonArray(778) }));
    }

    // ---- the 2-of-2 creation ----------------------------------------------------------------------------------------

    [Fact]
    public async Task SafeCreate_two_of_two_deploys_funds_and_bootstraps_through_the_confirm_path_and_shows_app_id_and_address()
    {
        // Step 1: the deploy preview. Two numbered steps, valued at zero, nothing signed.
        var r1 = await _create.ExecuteAsync(Ctx("safe.create"), Full(), default);
        Assert.False(r1.IsError, r1.Content);
        var card1 = Card(r1);
        Assert.Equal("safe.create", card1.Capability);
        Assert.Equal(2, card1.Steps.Count);
        Assert.Contains("2-of-2", card1.Intent);
        Assert.Contains("valued at zero", card1.Intent);
        Assert.All(card1.Steps, s => Assert.Empty(s.Assets));
        Assert.Equal("", card1.Steps[1].GroupHash);
        Assert.Contains(_bob, card1.Steps[1].Title);
        Assert.Equal("awaiting_user_confirmation", JsonNode.Parse(r1.Content)!["status"]!.GetValue<string>());
        Assert.Empty(_oidc.Calls);
        await using (var db = NewContext())
            Assert.Equal(0m, (await db.ActionPreviews.SingleAsync()).PredictedValueEur);

        // Asking again before confirming returns the same preview.
        var again = await _create.ExecuteAsync(Ctx("safe.create"), Full(), default);
        Assert.Equal(card1.PreviewId, Card(again).PreviewId);
        Assert.Single(_builders.Builds);

        // The user confirms: Biatec OIDC signs exactly the previewed deploy call, MCP submits, it lands.
        var c1 = await ConfirmAsync(card1, 0);
        Assert.Equal(ConfirmOutcome.Landed, c1.Outcome);
        var sign1 = Assert.Single(_oidc.Calls);
        Assert.Equal(card1.Steps[0].GroupHash, Convert.ToHexStringLower(sign1.GroupId));
        Assert.Equal(Alice, sign1.Address);
        Assert.IsType<ApplicationCreateTransaction>(Transaction.FromBase64String(Assert.Single(sign1.Transactions)));

        // Called again: the app id comes from the chain, and the unfunded safe asks for a separate transfer.
        var r2 = await _create.ExecuteAsync(Ctx("safe.create"), Full(), default);
        Assert.False(r2.IsError, r2.Content);
        var o2 = JsonNode.Parse(r2.Content)!;
        var address = SafeCreateService.SafeAddress(AppId);
        Assert.Equal("safe_needs_funding", o2["status"]!.GetValue<string>());
        Assert.Equal("778", o2["appId"]!.GetValue<string>());
        Assert.Equal(address, o2["safeAddress"]!.GetValue<string>());
        Assert.Equal("500000", o2["fundingMicroAlgo"]!.GetValue<string>());
        Assert.Contains("wallet.transfer", o2["note"]!.GetValue<string>());
        Assert.Contains(address, Card(r2).Intent);
        Assert.Equal(CardStatus.Completed, Card(r2).Steps[0].Status);
        Assert.Single(_builders.Builds); // no bootstrap built yet

        // Funded (by the user's own wallet.transfer): step 2 is built against the deployed safe.
        _chain.Balance = 600_000;
        var r3 = await _create.ExecuteAsync(Ctx("safe.create"), Full(), default);
        Assert.False(r3.IsError, r3.Content);
        var card3 = Card(r3);
        Assert.Equal(card1.PreviewId, card3.PreviewId);
        Assert.NotEqual("", card3.Steps[1].GroupHash);
        Assert.Equal($"safe {AppId} ({address})", card3.Steps[1].To);
        var boot = _builders.Builds[^1].Params;
        Assert.Equal("bootstrap", boot.GetProperty("step").GetString());
        Assert.Equal("778", boot.GetProperty("appId").GetString());
        Assert.Equal([Alice, _bob], boot.GetProperty("groups")[0].GetProperty("members").EnumerateArray().Select(m => m.GetProperty("address").GetString()));
        Assert.Equal(2, boot.GetProperty("groups")[0].GetProperty("threshold").GetInt32());
        // The app id was read from the chain once, then remembered.
        Assert.Equal(1, _chain.AppIdLookups);

        var c3 = await ConfirmAsync(card3, 1);
        Assert.Equal(ConfirmOutcome.Landed, c3.Outcome);
        Assert.Equal(2, _oidc.Calls.Count);
        Assert.Equal(card3.Steps[1].GroupHash, Convert.ToHexStringLower(_oidc.Calls[1].GroupId));
        Assert.Equal(2, _mcp.Submits.Count);

        await using (var db = NewContext())
        {
            Assert.Equal(ActionPreviewStatus.Completed, (await db.ActionPreviews.SingleAsync()).Status);
            var known = await db.KnownSafes.SingleAsync();
            Assert.Equal(778L, known.AppId);
            Assert.Equal(_tenant.Id, known.TenantId);
        }

        // Done: the chat shows the application id and the safe address.
        var r4 = await _create.ExecuteAsync(Ctx("safe.create"), Full(), default);
        var o4 = JsonNode.Parse(r4.Content)!;
        Assert.Equal("safe_created", o4["status"]!.GetValue<string>());
        Assert.Equal("778", o4["appId"]!.GetValue<string>());
        Assert.Equal(address, o4["safeAddress"]!.GetValue<string>());
        Assert.Equal(CardStatus.Completed, Card(r4).Status);
        Assert.DoesNotContain("SECRET", r4.Content);
    }

    [Fact]
    public async Task SafeCreate_does_not_preview_a_bootstrap_that_fails_simulation()
    {
        var card = Card(await _create.ExecuteAsync(Ctx("safe.create"), Full(), default));
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(card, 0)).Outcome);
        _chain.Balance = 600_000;
        _builders.SimulationFails = true;

        var r = await _create.ExecuteAsync(Ctx("safe.create"), Full(), default);
        Assert.Equal("safe_needs_funding", JsonNode.Parse(r.Content)!["status"]!.GetValue<string>());
        Assert.Equal("", Card(r).Steps[1].GroupHash);
        // The sidecar's failure text is not forwarded.
        Assert.DoesNotContain("logic eval", r.Content);
        await using var db = NewContext();
        Assert.Equal(1, await db.ActionSteps.CountAsync());
    }

    [Fact]
    public async Task SafeCreate_waits_when_the_app_id_cannot_be_read_yet()
    {
        var card = Card(await _create.ExecuteAsync(Ctx("safe.create"), Full(), default));
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(card, 0)).Outcome);
        _chain.CreatedAppId = null;

        var r = await _create.ExecuteAsync(Ctx("safe.create"), Full(), default);
        Assert.True(r.IsError);
        Assert.Contains("app id could not be read", r.Content);
    }

    // ---- safe.list ---------------------------------------------------------------------------------------------------

    private async Task SeedKnownSafeAsync(ulong appId)
    {
        await using var db = NewContext();
        db.KnownSafes.Add(new KnownSafe { Id = Guid.NewGuid(), TenantId = _tenant.Id, Network = "testnet", AppId = (long)appId, CreatedAt = _clock.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    private static JsonObject Proposal(int id, string status, int approvals, bool approvedByMember) => new()
    {
        ["proposalId"] = id.ToString(), ["groupId"] = "1", ["kind"] = "transactionGroup", ["status"] = status,
        ["approvals"] = approvals.ToString(), ["threshold"] = "2", ["proposer"] = "PROPOSER", ["expiryRound"] = "9999999",
        ["payloadVersion"] = "3", ["approvedByMember"] = approvedByMember,
        ["payload"] = new JsonArray(new JsonObject { ["type"] = "assetTransfer", ["assetId"] = "0", ["receiver"] = "SAFE", ["amount"] = "0" }),
    };

    [Fact]
    public async Task SafeCreate_list_shows_the_members_safes_with_pending_proposals_progress_epoch_and_expiry()
    {
        // Bob (another user of the tenant) lists his safes: the safe Alice created is found without its id.
        _own = [_bob];
        await SeedKnownSafeAsync(AppId);
        await SeedKnownSafeAsync(999); // a safe Bob is not in: never shown
        _builders.Members[AppId] = [Alice, _bob];
        _builders.Members[999] = [Alice];
        _builders.Proposals = [Proposal(3, "executed", 2, false), Proposal(2, "ready", 2, true), Proposal(1, "active", 1, false)];

        var r = await _list.ExecuteAsync(Ctx("safe.list"), new JsonObject { ["network"] = "testnet" }, default);

        Assert.False(r.IsError, r.Content);
        var o = JsonNode.Parse(r.Content)!;
        var safe = Assert.Single(o["safes"]!.AsArray())!;
        Assert.Equal("778", safe["appId"]!.GetValue<string>());
        Assert.Equal(SafeCreateService.SafeAddress(AppId), safe["address"]!.GetValue<string>());
        Assert.Equal([_bob], safe["yourAddresses"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(1, safe["signerGroups"]![0]!["membershipEpoch"]!.GetValue<int>());
        var pending = safe["pendingProposals"]!.AsArray();
        Assert.Equal(["2", "1"], pending.Select(p => p!["proposalId"]!.GetValue<string>()));
        Assert.Equal("ready_to_execute", pending[0]!["status"]!.GetValue<string>());
        Assert.Equal("1 of 2 approvals", pending[1]!["progress"]!.GetValue<string>());
        Assert.False(pending[1]!["approvedByYou"]!.GetValue<bool>());
        Assert.Equal("3", pending[1]!["payloadVersion"]!.GetValue<string>());
        Assert.Equal("9999999", pending[1]!["expiryRound"]!.GetValue<string>());
        Assert.Contains("not instructions", o["instruction"]!.GetValue<string>());
        // Both candidates were probed for Bob's address; proposals only for the safe he is in.
        var list = Assert.Single(_builders.Reads, x => x.Name == "safe.list").Params;
        Assert.Equal(_bob, list.GetProperty("member").GetString());
        Assert.Equal(2, list.GetProperty("appIds").GetArrayLength());
        Assert.Equal("778", Assert.Single(_builders.Reads, x => x.Name == "safe.proposals").Params.GetProperty("appId").GetString());
    }

    [Fact]
    public async Task SafeCreate_list_with_a_named_safe_remembers_it_and_with_none_known_asks_for_an_id()
    {
        _own = [_bob];
        var none = await _list.ExecuteAsync(Ctx("safe.list"), new JsonObject { ["network"] = "testnet" }, default);
        Assert.False(none.IsError);
        Assert.Contains("ask for its app id", none.Content);
        Assert.Empty(_builders.Reads);

        _builders.Members[4321] = [_bob];
        var named = await _list.ExecuteAsync(Ctx("safe.list"), new JsonObject { ["network"] = "testnet", ["safeAppIds"] = new JsonArray(4321) }, default);
        Assert.Equal("4321", JsonNode.Parse(named.Content)!["safes"]![0]!["appId"]!.GetValue<string>());
        await using var db = NewContext();
        Assert.Equal(4321L, (await db.KnownSafes.SingleAsync()).AppId);
    }
}
