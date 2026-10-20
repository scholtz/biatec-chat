using System.Text.Json;
using System.Text.Json.Nodes;
using Algorand;
using Algorand.Algod.Model.Transactions;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Wallet.Transfer;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Algod;
using Biatec.Chat.Tools.Mcp;
using Biatec.Chat.Tools.Wallet.Transfer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Encoder = Algorand.Utils.Encoder;
using SessionTier = Biatec.Chat.Core.Data.SessionTier;
using ActionPreviewStatus = Biatec.Chat.Api.Contracts.ActionPreviewStatus;

namespace Biatec.Chat.Tests.Transfer;

/// <summary>wallet.transfer on Algorand. Needs the docker-compose postgres; each run migrates a fresh database. Biatec MCP is a mock.</summary>
public sealed class TransferTests : IClassFixture<TransferTests.DbFixture>, IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private const ulong Usdc = 10458941;
    private static readonly Address Mine = new Address("UPYAFLHSIPMJOHVXU2MPLQ46GXJKSDCEMZ6RLCQ7GWB5PRDKJUWKKXECXI");
    private static readonly Address Mine2 = new Address("KV2XGKMXGYJ6PWYQA5374BYIQBL3ONRMSIARPCFCJEAMAHQEVYPB7PL3KU");
    private static readonly Address Stranger = new Address("7ZUECA7HFLZTXENRV24SHLU4AVPUTMTTDUFUBNBD64C73F3UHRTHAIOF6Q");

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
    }

    /// <summary>A Biatec MCP stand-in that returns real unsigned transactions for what it is asked to build.</summary>
    private sealed class FakeMcp : IBiatecMcpClient
    {
        public List<string> Calls { get; } = [];
        /// <summary>The network argument of every MCP call that takes one, as "tool:network".</summary>
        public List<string> Networks { get; } = [];
        public string GenesisId { get; set; } = "testnet-v1.0";
        public Dictionary<string, string> Balances { get; } = new();
        public Func<Transaction, Transaction>? Tamper { get; set; }
        public ulong FirstValid { get; set; } = 1000;

        public static McpToolResult Ok(string json) => new(false, json, JsonDocument.Parse(json).RootElement.Clone());

        private Transaction Common(Transaction t)
        {
            t.Fee = 1000; t.FirstValid = FirstValid; t.LastValid = FirstValid + 1000; t.GenesisId = GenesisId;
            t.GenesisHash = new Digest(Convert.FromBase64String("SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI="));
            return Tamper is null ? t : Tamper(t);
        }

        private static McpToolResult Tx(Transaction t) => Ok(JsonSerializer.Serialize(new { transaction = Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(t)) }));

        public Task<McpToolResult> ListAlgorandAddressesAsync(McpCallContext ctx, CancellationToken ct = default)
        {
            Calls.Add("listAlgorandAddresses");
            return Task.FromResult(Ok($"[\"{Mine.EncodeAsString()}\",\"{Mine2.EncodeAsString()}\"]"));
        }

        public Task<McpToolResult> GetCryptoBalanceAsync(McpCallContext ctx, string network, string address, CancellationToken ct = default)
        {
            Calls.Add($"getCryptoBalance:{address}");
            Networks.Add($"getCryptoBalance:{network}");
            // An unknown address reads as the real MCP shape of an empty account.
            return Task.FromResult(Ok(Balances.TryGetValue(address, out var b) ? b : "{\"nativeBalanceBaseUnits\":\"0\",\"assets\":[]}"));
        }

        public Task<McpToolResult> CreatePaymentTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amount, ulong? assetId = null, string? note = null, CancellationToken ct = default)
        {
            Calls.Add($"createPaymentTransaction:{sender[..4]}>{receiver[..4]}:{amount}:{assetId}");
            Networks.Add($"createPaymentTransaction:{network}");
            Transaction t = assetId is { } id && id != 0
                ? new AssetTransferTransaction { Sender = new Address(sender), AssetReceiver = new Address(receiver), AssetAmount = ulong.Parse(amount), XferAsset = id }
                : new PaymentTransaction { Sender = new Address(sender), Receiver = new Address(receiver), Amount = ulong.Parse(amount) };
            return Task.FromResult(Tx(Common(t)));
        }

        public Task<McpToolResult> CreateOptInTransactionAsync(McpCallContext ctx, string network, string address, ulong assetId, CancellationToken ct = default)
        {
            Calls.Add($"createOptInTransaction:{address[..4]}:{assetId}");
            Networks.Add($"createOptInTransaction:{network}");
            return Task.FromResult(Tx(Common(new AssetTransferTransaction { Sender = new Address(address), AssetReceiver = new Address(address), AssetAmount = 0, XferAsset = assetId })));
        }

        public Task<IReadOnlyList<McpToolInfo>> GetToolsAsync(McpCallContext ctx, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> GetCryptoAddressAsync(McpCallContext ctx, string network, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateBitcoinTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amountBtc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateSwapTransactionAsync(McpCallContext ctx, string network, string address, ulong fromAssetId, ulong toAssetId, string amount, string? slippage = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> SubmitTransactionToBlockchainAsync(McpCallContext ctx, string network, string signedTransactionBase64, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>One migrated database for the whole class (migrating one per test is heavy on the shared postgres); each test uses its own conversation.</summary>
    public sealed class DbFixture : IAsyncLifetime
    {
        private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
        public string ConnectionString { get; private set; } = "";

        private static async Task AdminAsync(string sql)
        {
            await using var conn = new NpgsqlConnection(AdminCs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task InitializeAsync()
        {
            await AdminAsync($"CREATE DATABASE \"{_dbName}\"");
            ConnectionString = new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;
            await using var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(ConnectionString).Options);
            await db.Database.MigrateAsync();
        }

        public async Task DisposeAsync() => await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeMcp _mcp = new();
    private readonly string _cs;
    private Guid _conversation;

    public TransferTests(DbFixture fixture) => _cs = fixture.ConnectionString;

    private ChatDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(_cs).Options);

    public async Task InitializeAsync()
    {
        _conversation = Guid.NewGuid();
        await using var db = NewContext();
        db.Conversations.Add(new Conversation { Id = _conversation, TenantId = "t1", CreatedAt = _clock.GetUtcNow(), LastMessageAt = _clock.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static McpCallContext Ctx => new("t1", "sess", ["openid"]);

    private (TransferPreviewService Service, ActionPreviewService Previews, ChatDbContext Db) NewService(IAlgodSimulateClient? sim = null)
    {
        var db = NewContext();
        var previews = new ActionPreviewService(db, _clock);
        return (new TransferPreviewService(_mcp, previews, db, _clock, simulate: sim), previews, db);
    }

    private sealed class FakeSim(bool success) : IAlgodSimulateClient
    {
        public Task<SimulationOutcome> SimulateAsync(IReadOnlyList<Transaction> g, bool allowUnnamedResources = false, CancellationToken ct = default) =>
            Task.FromResult(new SimulationOutcome(success, success ? null : "asset missing", success ? [] : [0UL], 1005, null, g.Count));
        public Task<ulong> GetLastRoundAsync(CancellationToken ct = default) => Task.FromResult(1005UL);
    }

    [Fact]
    public async Task A_failing_simulation_means_no_preview()
    {
        FundMine();
        var (svc, _, db) = NewService(new FakeSim(false));
        var r = await svc.StartAsync(Ctx, _conversation, Req(Stranger), default);
        Assert.True(r.IsError);
        Assert.Contains("would fail on chain", r.Message);
        Assert.Equal(0, await db.ActionPreviews.CountAsync(x => x.ConversationId == _conversation));
    }

    [Fact]
    public async Task A_passing_simulation_puts_its_summary_on_the_preview()
    {
        FundMine();
        var (svc, _, _) = NewService(new FakeSim(true));
        var r = await svc.StartAsync(Ctx, _conversation, Req(Stranger), default);
        Assert.False(r.IsError, r.Message);
        Assert.Contains("Simulation succeeded", r.Simulation);
        Assert.Contains("Simulation succeeded", TransferCard.Map(r).Steps[0].SimulationSummary);
    }

    [Fact]
    public async Task The_factory_picks_the_simulate_client_of_the_request_network()
    {
        FundMine();
        var db = NewContext();
        var factory = new AlgodClientFactory(new Dictionary<string, IAlgodSimulateClient>
        {
            ["algorand-testnet"] = new FakeSim(false), ["algorand-mainnet"] = new FakeSim(true),
        });
        var svc = new TransferPreviewService(_mcp, new ActionPreviewService(db, _clock), db, _clock, factory);
        var r = await svc.StartAsync(Ctx, _conversation, Req(Stranger), default);
        Assert.True(r.IsError);
    }

    [Fact]
    public async Task An_asa_with_unknown_decimals_is_refused()
    {
        _mcp.Balances[Mine.EncodeAsString()] = $"{{\"balances\":[{{\"assetId\":0,\"amount\":\"10000000\"}},{{\"assetId\":{Usdc},\"symbol\":\"USDC\",\"amount\":\"50000000\"}}]}}";
        var (svc, _, _) = NewService();
        var r = await svc.StartAsync(Ctx, _conversation, Req(Stranger, Usdc, "2500000"), default);
        Assert.True(r.IsError);
        Assert.Contains("decimals", r.Message);
    }

    [Fact]
    public void ParseRequest_normalises_the_amount_and_rejects_a_negative_asset()
    {
        var args = Args(Stranger, amount: "0100");
        var req = WalletTransferExecutor.ParseRequest(args, out _);
        Assert.Equal("100", req!.Amount);
        args["assetId"] = -5;
        Assert.Null(WalletTransferExecutor.ParseRequest(args, out var problem));
        Assert.NotNull(problem);
    }

    /// <summary>The real Biatec MCP <c>getCryptoBalance</c> shape for an Algorand address (BiatecMCP GetCryptoBalanceResponse).</summary>
    private static string Real(string addr, string micro, string assets = "") =>
        $"{{\"network\":\"Algorand Testnet\",\"family\":\"Avm\",\"address\":\"{addr}\",\"nativeCurrencySymbol\":\"ALGO\",\"nativeBalance\":0," +
        $"\"nativeBalanceBaseUnits\":\"{micro}\",\"assets\":[{assets}],\"assetsTruncated\":false,\"error\":\"\",\"errorType\":\"\"}}";

    private static string Algo(string addr, string micro) => Real(addr, micro);

    private void FundMine(bool withUsdc = true)
    {
        var usdc = withUsdc ? $",{{\"assetId\":{Usdc},\"symbol\":\"USDC\",\"amount\":\"50000000\",\"decimals\":6}}" : "";
        _mcp.Balances[Mine.EncodeAsString()] = $"{{\"balances\":[{{\"assetId\":0,\"amount\":\"10000000\"}}{usdc}]}}";
    }

    private static TransferRequest Req(Address to, ulong assetId = 0, string amount = "1500000", string network = "testnet") =>
        new(network, Mine.EncodeAsString(), to.EncodeAsString(), assetId, amount);

    [Theory]
    [InlineData("testnet", "algorand-testnet", "testnet-v1.0")]
    [InlineData("mainnet", "algorand-mainnet", "mainnet-v1.0")]
    public async Task Biatec_mcp_is_called_with_its_own_network_codes(string network, string mcpCode, string genesis)
    {
        FundMine();
        _mcp.Balances[Mine2.EncodeAsString()] = Real(Mine2.EncodeAsString(), "5000000");
        _mcp.GenesisId = genesis;
        var (svc, _, _) = NewService();
        var r = await svc.StartAsync(Ctx, _conversation, Req(Mine2, Usdc, "2500000", network), default);

        Assert.False(r.IsError, r.Message);
        Assert.Equal(["getCryptoBalance", "getCryptoBalance", "createOptInTransaction"], _mcp.Networks.Select(n => n.Split(':')[0]));
        Assert.All(_mcp.Networks, n => Assert.Equal(mcpCode, n.Split(':')[1]));
        Assert.Equal(network, r.Request!.Network); // the model-facing name is unchanged
    }

    [Fact]
    public async Task An_mcp_error_payload_on_the_source_balance_is_a_failed_read()
    {
        _mcp.Balances[Mine.EncodeAsString()] =
            "{\"network\":\"testnet\",\"family\":\"\",\"address\":\"\",\"nativeBalanceBaseUnits\":\"10000000\",\"assets\":null,\"error\":\"Unknown network 'testnet'.\",\"errorType\":\"\"}";
        var (svc, _, db) = NewService();
        var r = await svc.StartAsync(Ctx, _conversation, Req(Stranger), default);

        Assert.True(r.IsError);
        Assert.Contains("The balance could not be read", r.Message);
        Assert.DoesNotContain(_mcp.Calls, c => c.StartsWith("create"));
        Assert.Equal(0, await db.ActionPreviews.CountAsync(x => x.ConversationId == _conversation));
    }

    [Fact]
    public async Task An_mcp_error_payload_on_the_receiver_balance_is_a_failed_read()
    {
        FundMine();
        _mcp.Balances[Mine2.EncodeAsString()] = "{\"nativeBalanceBaseUnits\":\"\",\"error\":\"Could not reach algod.\"}";
        var (svc, _, _) = NewService();
        var r = await svc.StartAsync(Ctx, _conversation, Req(Mine2, Usdc), default);

        Assert.True(r.IsError);
        Assert.Contains("The receiving address could not be read", r.Message);
        Assert.DoesNotContain(_mcp.Calls, c => c.StartsWith("create"));
    }

    [Fact]
    public async Task Native_algo_is_read_from_nativeBalanceBaseUnits_of_the_real_shape()
    {
        _mcp.Balances[Mine.EncodeAsString()] = Real(Mine.EncodeAsString(), "1000000", $"{{\"assetId\":{Usdc},\"amount\":50000000}}");
        var (svc, _, _) = NewService();

        var tooMuch = await svc.StartAsync(Ctx, _conversation, Req(Stranger, amount: "1500000"), default);
        Assert.True(tooMuch.IsError);
        Assert.Contains("holds only 1 ALGO, less than the requested 1.5", tooMuch.Message);

        var ok = await svc.StartAsync(Ctx, _conversation, Req(Stranger, amount: "900000"), default);
        Assert.False(ok.IsError, ok.Message);
        Assert.Equal(6, ok.Decimals);
        Assert.Equal("ALGO", ok.Symbol);
    }

    [Fact]
    public void Real_shape_balances_are_native_algo_plus_the_asa_holdings()
    {
        var json = JsonDocument.Parse(Real(Mine.EncodeAsString(), "123456789012345678901",
            $"{{\"assetId\":{Usdc},\"amount\":0}},{{\"assetId\":31566704,\"amount\":18446744073709551615}}")).RootElement;
        var b = McpWalletJson.Balances(json);
        Assert.Equal(3, b.Count);
        Assert.Equal(new BalanceEntry(0, "ALGO", "123456789012345678901", 6), b[0]);
        Assert.Equal(new BalanceEntry(Usdc, null, "0", null), b[1]);
        Assert.Equal(new BalanceEntry(31566704, null, "18446744073709551615", null), b[2]);
        Assert.Empty(McpWalletJson.Balances(JsonDocument.Parse("{\"nativeBalanceBaseUnits\":\"x\",\"assets\":null}").RootElement));
    }

    [Fact]
    public async Task Own_receiver_listed_in_the_real_assets_array_is_opted_in()
    {
        FundMine();
        _mcp.Balances[Mine2.EncodeAsString()] = Real(Mine2.EncodeAsString(), "0", $"{{\"assetId\":{Usdc},\"amount\":0}}");
        var (svc, _, _) = NewService();
        var r = await svc.StartAsync(Ctx, _conversation, Req(Mine2, Usdc), default);
        Assert.False(r.IsError, r.Message);
        Assert.Equal([TransferStepKind.Transfer], r.Plan.Select(p => p.Kind));
    }

    [Fact]
    public async Task An_asa_held_in_the_real_shape_without_decimals_is_refused_not_guessed()
    {
        _mcp.Balances[Mine.EncodeAsString()] = Real(Mine.EncodeAsString(), "10000000", $"{{\"assetId\":{Usdc},\"amount\":50000000}}");
        var (svc, _, _) = NewService();
        var r = await svc.StartAsync(Ctx, _conversation, Req(Stranger, Usdc, "2500000"), default);
        Assert.True(r.IsError);
        Assert.Contains("decimals", r.Message);
        Assert.DoesNotContain(_mcp.Calls, c => c.StartsWith("create"));
    }

    private static IReadOnlyList<Transaction> Decode(Biatec.Chat.Core.Data.ActionStep step) =>
        AlgorandGroup.Decode(JsonSerializer.Deserialize<List<string>>(step.UnsignedGroup!)!);

    [Fact]
    public async Task Simple_algo_transfer_is_one_step_whose_hash_is_the_group_id_of_the_built_transaction()
    {
        FundMine();
        var (svc, _, db) = NewService();
        var r = await svc.StartAsync(Ctx, _conversation, Req(Stranger), default);

        Assert.False(r.IsError, r.Message);
        Assert.Single(r.Plan);
        var step = Assert.Single(r.Snapshot!.Steps);
        var tx = Assert.IsType<PaymentTransaction>(Assert.Single(Decode(step)));
        Assert.Equal(Mine.EncodeAsString(), tx.Sender.EncodeAsString());
        Assert.Equal(Stranger.EncodeAsString(), tx.Receiver.EncodeAsString());
        Assert.Equal(1500000UL, tx.Amount);
        // The stored hash is what the transaction in the step hashes to: preview and chain agree.
        Assert.Equal(AlgorandGroup.ComputeGroupHash(Decode(step)), step.SubjectHash);
        Assert.Equal("algorand-testnet", step.Network);
        Assert.Equal(Mine.EncodeAsString(), step.SenderAddress);
        Assert.Contains(_mcp.Calls, c => c.StartsWith("createPaymentTransaction"));
        Assert.DoesNotContain(_mcp.Calls, c => c.StartsWith("createOptIn"));
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Asa_transfer_asks_mcp_for_the_asset_and_uses_the_asset_decimals()
    {
        FundMine();
        var (svc, _, _) = NewService();
        var r = await svc.StartAsync(Ctx, _conversation, Req(Stranger, Usdc, "2500000"), default);

        Assert.False(r.IsError, r.Message);
        Assert.Equal("USDC", r.Symbol);
        Assert.Equal(6, r.Decimals);
        var tx = Assert.IsType<AssetTransferTransaction>(Assert.Single(Decode(r.Snapshot!.Steps[0])));
        Assert.Equal(Usdc, tx.XferAsset);
        Assert.Equal(2500000UL, tx.AssetAmount);
        var card = TransferCard.Map(r);
        Assert.Equal("Send 2.5 USDC from " + Mine.EncodeAsString()[..6] + "..." + Mine.EncodeAsString()[^6..] + " to " + Stranger.EncodeAsString()[..6] + "..." + Stranger.EncodeAsString()[^6..], card.Intent);
        Assert.Equal(r.Snapshot.Steps[0].SubjectHash, card.Steps[0].GroupHash);
        Assert.Equal("1000", card.Steps[0].FeeAmount);
    }

    [Fact]
    public async Task Own_unopted_receiver_with_little_algo_gives_a_numbered_three_step_card_with_only_step_one_built()
    {
        FundMine();
        _mcp.Balances[Mine2.EncodeAsString()] = Algo(Mine2.EncodeAsString(), "0");
        var (svc, _, db) = NewService();
        var r = await svc.StartAsync(Ctx, _conversation, Req(Mine2, Usdc), default);

        Assert.False(r.IsError, r.Message);
        Assert.Equal([TransferStepKind.Fund, TransferStepKind.OptIn, TransferStepKind.Transfer], r.Plan.Select(p => p.Kind));
        // Only step 1 is built: one builder call, one stored step.
        Assert.Single(r.Snapshot!.Steps);
        Assert.Single(_mcp.Calls, c => c.StartsWith("create"));
        var fund = Assert.IsType<PaymentTransaction>(Assert.Single(Decode(r.Snapshot.Steps[0])));
        Assert.Equal(Mine2.EncodeAsString(), fund.Receiver.EncodeAsString());
        Assert.Equal(TransferPlanner.FundingMicroAlgos, fund.Amount);
        Assert.Equal(1, await db.ActionSteps.CountAsync(x => db.ActionPreviews.Any(p => p.Id == x.PreviewId && p.ConversationId == _conversation)));

        var card = TransferCard.Map(r);
        Assert.Equal(3, card.Steps.Count);
        Assert.StartsWith("Step 1 of 3", card.Steps[0].Title);
        Assert.StartsWith("Step 2 of 3", card.Steps[1].Title);
        Assert.StartsWith("Step 3 of 3", card.Steps[2].Title);
        Assert.NotEmpty(card.Steps[0].GroupHash);
        Assert.Equal("", card.Steps[1].GroupHash);
        Assert.Equal("", card.Steps[2].GroupHash);
        Assert.Equal(Mine2.EncodeAsString(), card.Steps[1].From); // the opt-in is from the receiving address
        Assert.Equal(Mine2.EncodeAsString(), card.Steps[1].To);
        Assert.Empty(card.Steps[1].Assets);
        Assert.Equal(Mine.EncodeAsString(), card.Steps[2].From);
    }

    [Fact]
    public async Task Own_unopted_receiver_with_enough_algo_skips_the_funding_step()
    {
        FundMine();
        _mcp.Balances[Mine2.EncodeAsString()] = Algo(Mine2.EncodeAsString(), "5000000");
        var (svc, _, _) = NewService();
        var r = await svc.StartAsync(Ctx, _conversation, Req(Mine2, Usdc), default);

        Assert.False(r.IsError, r.Message);
        Assert.Equal([TransferStepKind.OptIn, TransferStepKind.Transfer], r.Plan.Select(p => p.Kind));
        var optIn = Assert.IsType<AssetAcceptTransaction>(Assert.Single(Decode(r.Snapshot!.Steps[0])));
        Assert.Equal(Mine2.EncodeAsString(), optIn.Sender.EncodeAsString());
        Assert.Equal(Usdc, optIn.XferAsset);
        Assert.Equal(Mine2.EncodeAsString(), r.Snapshot.Steps[0].SenderAddress);
    }

    [Fact]
    public async Task Own_receiver_that_already_opted_in_is_a_single_step()
    {
        FundMine();
        _mcp.Balances[Mine2.EncodeAsString()] = $"{{\"balances\":[{{\"assetId\":0,\"amount\":\"5000000\"}},{{\"assetId\":{Usdc},\"amount\":\"0\",\"decimals\":6}}]}}";
        var (svc, _, _) = NewService();
        var r = await svc.StartAsync(Ctx, _conversation, Req(Mine2, Usdc), default);
        Assert.Single(r.Plan);
        Assert.Equal(TransferStepKind.Transfer, r.Plan[0].Kind);
    }

    [Fact]
    public async Task A_later_step_is_built_only_after_the_earlier_one_has_landed()
    {
        FundMine();
        _mcp.Balances[Mine2.EncodeAsString()] = Algo(Mine2.EncodeAsString(), "0");
        var (svc, previews, db) = NewService();
        var first = await svc.StartAsync(Ctx, _conversation, Req(Mine2, Usdc), default);
        var previewId = first.Snapshot!.Preview.Id;
        var builds = _mcp.Calls.Count(c => c.StartsWith("create"));

        // Asking again while step 1 is unconfirmed builds nothing new and returns the same preview.
        var again = await svc.StartAsync(Ctx, _conversation, Req(Mine2, Usdc), default);
        Assert.Equal(previewId, again.Snapshot!.Preview.Id);
        Assert.Single(again.Snapshot.Steps);
        Assert.Equal(builds, _mcp.Calls.Count(c => c.StartsWith("create")));

        // The service-level guard: a step cannot be added while the previous one has not landed.
        var ex = await Assert.ThrowsAsync<PreviewException>(() => previews.AddNextStepAsync(_conversation, previewId,
            new NewStepSpec(ActionStepKind.Sign, new string('a', 64), _clock.GetUtcNow().AddMinutes(30), "[]", SenderAddress: "x", Network: "algorand-testnet")));
        Assert.Equal(PreviewError.PreviousStepNotLanded, ex.Error);

        // Step 1 confirmed by the user and landed (the confirm flow's job): now step 2 is built.
        await previews.ConfirmAsync(_conversation, previewId, first.Snapshot.Steps[0].SubjectHash);
        await previews.MarkStepLandedAsync(_conversation, previewId, 0, ["TX1"]);
        var second = await svc.StartAsync(Ctx, _conversation, Req(Mine2, Usdc), default);
        Assert.False(second.IsError, second.Message);
        Assert.Equal(2, second.Snapshot!.Steps.Count);
        Assert.Equal(builds + 1, _mcp.Calls.Count(c => c.StartsWith("create")));
        Assert.Contains(_mcp.Calls, c => c.StartsWith("createOptInTransaction"));
        Assert.NotEqual(second.Snapshot.Steps[0].SubjectHash, second.Snapshot.Steps[1].SubjectHash);
        Assert.Equal(Mine2.EncodeAsString(), second.Snapshot.Steps[1].SenderAddress);
        // The card shows step 1 landed, step 2 awaiting confirmation with its own hash, step 3 not built.
        var card = TransferCard.Map(second);
        Assert.Equal(ActionPreviewStatus.Completed, card.Steps[0].Status);
        Assert.Equal(second.Snapshot.Steps[1].SubjectHash, card.Steps[1].GroupHash);
        Assert.Equal("", card.Steps[2].GroupHash);

        // And so on for the transfer itself, which is the final step.
        await previews.ConfirmAsync(_conversation, previewId, second.Snapshot.Steps[1].SubjectHash);
        await previews.MarkStepLandedAsync(_conversation, previewId, 1, ["TX2"]);
        var third = await svc.StartAsync(Ctx, _conversation, Req(Mine2, Usdc), default);
        Assert.Equal(3, third.Snapshot!.Steps.Count);
        var xfer = Assert.IsType<AssetTransferTransaction>(Assert.Single(Decode(third.Snapshot.Steps[2])));
        Assert.Equal(Mine.EncodeAsString(), xfer.Sender.EncodeAsString());
        Assert.Equal(Mine2.EncodeAsString(), xfer.AssetReceiver.EncodeAsString());
        await db.DisposeAsync();
    }

    [Fact]
    public async Task An_expired_preview_is_rebuilt_as_a_new_linked_preview_with_a_new_hash()
    {
        FundMine();
        var (svc, _, db) = NewService();
        var first = await svc.StartAsync(Ctx, _conversation, Req(Stranger), default);
        // Expire it, and let the chain move on so the rebuilt group has new validity rounds.
        var p = await db.ActionPreviews.SingleAsync(x => x.ConversationId == _conversation);
        p.Status = Biatec.Chat.Core.Data.ActionPreviewStatus.Expired;
        await db.SaveChangesAsync();
        _mcp.FirstValid = 5000;
        var (svc2, _, db2) = NewService();
        var again = await svc2.StartAsync(Ctx, _conversation, Req(Stranger), default);

        Assert.False(again.IsError, again.Message);
        Assert.NotEqual(first.Snapshot!.Preview.Id, again.Snapshot!.Preview.Id);
        Assert.NotEqual(first.Snapshot.Steps[0].SubjectHash, again.Snapshot.Steps[0].SubjectHash);
        Assert.Equal(again.Snapshot.Preview.Id, (await db2.ActionPreviews.AsNoTracking().SingleAsync(x => x.Id == first.Snapshot.Preview.Id)).RebuiltAsPreviewId);
        await db.DisposeAsync();
        await db2.DisposeAsync();
    }

    [Fact]
    public async Task A_source_address_that_is_not_the_users_is_refused_before_anything_is_built()
    {
        var (svc, _, _) = NewService();
        var r = await svc.StartAsync(Ctx, _conversation, new TransferRequest("testnet", Stranger.EncodeAsString(), Mine.EncodeAsString(), 0, "1"), default);
        Assert.True(r.IsError);
        Assert.DoesNotContain(_mcp.Calls, c => c.StartsWith("create"));
    }

    [Fact]
    public async Task More_than_the_balance_is_refused()
    {
        FundMine();
        var (svc, _, _) = NewService();
        var r = await svc.StartAsync(Ctx, _conversation, Req(Stranger, 0, "99000000000"), default);
        Assert.True(r.IsError);
        Assert.DoesNotContain(_mcp.Calls, c => c.StartsWith("create"));
    }

    [Fact]
    public async Task An_asset_the_source_does_not_hold_is_refused()
    {
        FundMine(withUsdc: false);
        var (svc, _, _) = NewService();
        var r = await svc.StartAsync(Ctx, _conversation, Req(Stranger, Usdc), default);
        Assert.True(r.IsError);
    }

    public static IEnumerable<object[]> Tampering() =>
    [
        [(Func<Transaction, Transaction>)(t => { ((PaymentTransaction)t).Receiver = Stranger; return t; }), "receiver"],
        [(Func<Transaction, Transaction>)(t => { ((PaymentTransaction)t).Amount = 9; return t; }), "amount"],
        [(Func<Transaction, Transaction>)(t => { ((PaymentTransaction)t).CloseRemainderTo = Stranger; return t; }), "close-out"],
        [(Func<Transaction, Transaction>)(t => { t.RekeyTo = Stranger; return t; }), "rekey"],
        [(Func<Transaction, Transaction>)(t => { t.Sender = Mine2; return t; }), "sender"],
        [(Func<Transaction, Transaction>)(t => { t.GenesisId = "mainnet-v1.0"; return t; }), "network"],
        [(Func<Transaction, Transaction>)(t => { t.Fee = 5_000_000; return t; }), "fee"],
    ];

    [Theory]
    [MemberData(nameof(Tampering))]
    public async Task A_built_transaction_that_does_not_match_the_request_is_never_previewed(Func<Transaction, Transaction> tamper, string _)
    {
        FundMine();
        _mcp.Tamper = tamper;
        var (svc, _, db) = NewService();
        var r = await svc.StartAsync(Ctx, _conversation, Req(Mine2), default);
        Assert.True(r.IsError);
        Assert.Equal(0, await db.ActionPreviews.CountAsync(x => x.ConversationId == _conversation));
    }

    [Fact]
    public async Task Bitcoin_is_not_handled_here()
    {
        var (svc, _, _) = NewService();
        var r = await svc.StartAsync(Ctx, _conversation, new TransferRequest("bitcoin", "a", "b", 0, "1"), default);
        Assert.True(r.IsError);
        Assert.Empty(_mcp.Calls);
    }

    // ---- executor

    private WalletTransferExecutor NewExecutor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBiatecMcpClient>(_mcp);
        services.AddSingleton<TimeProvider>(_clock);
        services.AddScoped(_ => NewContext());
        services.AddScoped<ActionPreviewService>();
        services.AddScoped<TransferPreviewService>();
        return new WalletTransferExecutor(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    private ToolExecutionContext ExecCtx(SessionTier tier = SessionTier.Authenticated) =>
        new(CapabilityRegistry.CreateDefault().Find("wallet.transfer")!, "tu1", _conversation, "sess", "t1", tier, ["openid"]);

    private static JsonObject Args(Address to, ulong? assetId = null, string amount = "1500000", string network = "testnet")
    {
        var o = new JsonObject { ["network"] = network, ["from"] = Mine.EncodeAsString(), ["to"] = to.EncodeAsString(), ["amount"] = amount };
        if (assetId is { } a) o["assetId"] = (long)a;
        return o;
    }

    [Fact]
    public async Task Executor_returns_a_preview_card_event_and_tells_the_model_only_the_id()
    {
        FundMine();
        var r = await NewExecutor().ExecuteAsync(ExecCtx(), Args(Stranger, Usdc), default);

        Assert.False(r.IsError, r.Content);
        var card = Assert.IsType<PreviewCardEvent>(Assert.Single(r.Events!));
        Assert.Equal("wallet.transfer", card.Preview.Capability);
        Assert.Equal("algorand-testnet", card.Preview.Network);
        Assert.Equal(card.Preview.PreviewId, (string)JsonNode.Parse(r.Content)!["previewId"]!);
        Assert.DoesNotContain(card.Preview.Steps[0].GroupHash, r.Content);
        await using var db = NewContext();
        Assert.Equal(card.Preview.Steps[0].GroupHash, (await db.ActionSteps.SingleAsync(x => db.ActionPreviews.Any(p => p.Id == x.PreviewId && p.ConversationId == _conversation))).SubjectHash);
    }

    [Fact]
    public async Task Executor_refuses_anonymous_bitcoin_and_bad_input_without_calling_mcp()
    {
        var ex = NewExecutor();
        Assert.True((await ex.ExecuteAsync(ExecCtx(SessionTier.Anonymous), Args(Stranger), default)).IsError);
        Assert.True((await ex.ExecuteAsync(ExecCtx(), Args(Stranger, network: "bitcoin"), default)).IsError);
        Assert.True((await ex.ExecuteAsync(ExecCtx(), Args(Stranger, amount: "0"), default)).IsError);
        var bad = Args(Stranger);
        bad["to"] = "NOTANADDRESS";
        Assert.True((await ex.ExecuteAsync(ExecCtx(), bad, default)).IsError);
        Assert.Empty(_mcp.Calls);
    }

    [Fact]
    public void Gate_requires_a_signed_in_user_and_exposes_the_sign_scope_for_wallet_transfer()
    {
        var gate = new ToolGate(CapabilityRegistry.CreateDefault(), new InMemoryPolicyEventSink());
        Assert.False(gate.Check("wallet.transfer", Args(Stranger), new GateContext(SessionTier.Anonymous, ["openid"])).Allowed);
    }

    [Fact]
    public void Amounts_are_formatted_from_base_units()
    {
        Assert.Equal("1.5", TransferPlanner.FormatAmount("1500000", 6));
        Assert.Equal("0.000042", TransferPlanner.FormatAmount("42", 6));
        Assert.Equal("7", TransferPlanner.FormatAmount("7", 0));
        Assert.Equal("2", TransferPlanner.FormatAmount("2000000", 6));
    }
}
