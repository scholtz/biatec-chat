using System.Text.Json;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Wallet.Read;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Tools.Mcp;

namespace Biatec.Chat.Tests.Wallet;

public sealed class WalletReadTests
{
    private const string Mine = "MYADDRESS234567";
    private const string BtcPrimary = "bc1qprimary0000000000000000000000000000000";
    private const string BtcSecondary = "bc1qsecondary00000000000000000000000000000";

    private sealed class FakeMcp : IBiatecMcpClient
    {
        public List<string> Calls { get; } = [];
        public Func<McpToolResult> Addresses { get; set; } = () => Ok($"[\"{Mine}\",\"OTHER222\"]");
        public Func<McpToolResult> Balance { get; set; } = () => Ok("{\"amount\":\"1234567\"}");
        public Exception? Throw { get; set; }

        public static McpToolResult Ok(string json) => new(false, json, JsonDocument.Parse(json).RootElement.Clone());

        public Task<McpToolResult> ListAlgorandAddressesAsync(McpCallContext ctx, CancellationToken ct = default)
        {
            Calls.Add("listAlgorandAddresses");
            if (Throw is not null) throw Throw;
            return Task.FromResult(Addresses());
        }

        public Task<McpToolResult> GetCryptoBalanceAsync(McpCallContext ctx, string network, string address, CancellationToken ct = default)
        {
            Calls.Add($"getCryptoBalance:{network}:{address}");
            return Task.FromResult(Balance());
        }

        public Task<IReadOnlyList<McpToolInfo>> GetToolsAsync(McpCallContext ctx, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> GetCryptoAddressAsync(McpCallContext ctx, string network, CancellationToken ct = default) => throw new NotSupportedException();

        // Real getCryptoAddress shape: one {network, family, address, error} per slot.
        public Func<int, McpToolResult> BtcAddress { get; set; } = slot => Ok($"{{\"network\":\"Bitcoin\",\"family\":\"Btc\",\"address\":\"{(slot == 0 ? BtcPrimary : BtcSecondary)}\",\"error\":\"\"}}");

        public Task<McpToolResult> GetCryptoAddressAsync(McpCallContext ctx, string network, int slot, CancellationToken ct = default)
        {
            Calls.Add($"getCryptoAddress:{network}:{slot}");
            return Task.FromResult(BtcAddress(slot));
        }
        public Task<McpToolResult> CreatePaymentTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amount, ulong? assetId = null, string? note = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateOptInTransactionAsync(McpCallContext ctx, string network, string address, ulong assetId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateBitcoinTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amountBtc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateSwapTransactionAsync(McpCallContext ctx, string network, string address, ulong fromAssetId, ulong toAssetId, string amount, string? slippage = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> SubmitTransactionToBlockchainAsync(McpCallContext ctx, string network, string signedTransactionBase64, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static ToolExecutionContext Ctx(string name, SessionTier tier = SessionTier.Authenticated) =>
        new(CapabilityRegistry.CreateDefault().Find(name)!, "tu1", Guid.NewGuid(), "sess", "tenant", tier, ["openid"]);

    private static JsonObject Args(string address = Mine) => new() { ["network"] = "testnet", ["address"] = address };

    [Fact]
    public async Task ListAddresses_returns_the_users_addresses()
    {
        var mcp = new FakeMcp();
        var r = await new WalletListAddressesExecutor(mcp).ExecuteAsync(Ctx("wallet.listAddresses"), new JsonObject { ["network"] = "testnet" }, default);
        Assert.False(r.IsError);
        var o = JsonNode.Parse(r.Content)!;
        Assert.Equal(2, (int)o["count"]!);
        Assert.Equal(Mine, (string)o["addresses"]![0]!);
        Assert.Equal(["listAlgorandAddresses"], mcp.Calls);
    }

    [Fact]
    public async Task ListAddresses_accepts_object_shaped_results()
    {
        var mcp = new FakeMcp { Addresses = () => FakeMcp.Ok($"{{\"addresses\":[{{\"address\":\"{Mine}\",\"name\":\"x\"}}]}}") };
        var r = await new WalletListAddressesExecutor(mcp).ExecuteAsync(Ctx("wallet.listAddresses"), new JsonObject { ["network"] = "testnet" }, default);
        Assert.Equal(Mine, (string)JsonNode.Parse(r.Content)!["addresses"]![0]!);
    }

    [Fact]
    public async Task Balance_returns_amount_with_decimals_for_native_algo()
    {
        var mcp = new FakeMcp();
        var r = await new WalletBalanceExecutor(mcp).ExecuteAsync(Ctx("wallet.balance"), Args(), default);
        Assert.False(r.IsError);
        var b = JsonNode.Parse(r.Content)!["balances"]![0]!;
        Assert.Equal("1234567", (string)b["amount"]!);
        Assert.Equal(6, (int)b["decimals"]!);
        Assert.Equal("1.234567", (string)b["display"]!);
        Assert.Contains($"getCryptoBalance:algorand-testnet:{Mine}", mcp.Calls);
    }

    [Fact]
    public async Task Balance_does_not_assume_algo_decimals_for_a_symbol_only_entry()
    {
        var mcp = new FakeMcp
        {
            Balance = () => FakeMcp.Ok("{\"balances\":[{\"symbol\":\"TOK\",\"amount\":\"1234567\"},{\"symbol\":\"ALGO\",\"amount\":\"1234567\"}]}"),
        };
        var r = await new WalletBalanceExecutor(mcp).ExecuteAsync(Ctx("wallet.balance"), Args(), default);
        var list = JsonNode.Parse(r.Content)!["balances"]!.AsArray();
        Assert.Null(list[0]!["display"]);
        Assert.Equal("1234567", (string)list[0]!["amount"]!);
        Assert.Equal("1.234567", (string)list[1]!["display"]!);
    }

    [Fact]
    public async Task Balance_uses_the_decimals_of_each_asset()
    {
        var mcp = new FakeMcp
        {
            Balance = () => FakeMcp.Ok("{\"balances\":[{\"assetId\":0,\"amount\":5000000},{\"assetId\":31566704,\"symbol\":\"USDC\",\"amount\":\"42\",\"decimals\":6},{\"assetId\":7,\"amount\":\"9\",\"decimals\":0}]}"),
        };
        var r = await new WalletBalanceExecutor(mcp).ExecuteAsync(Ctx("wallet.balance"), Args(), default);
        var list = JsonNode.Parse(r.Content)!["balances"]!.AsArray();
        Assert.Equal("5.000000", (string)list[0]!["display"]!);
        Assert.Equal("0.000042", (string)list[1]!["display"]!);
        Assert.Equal("9", (string)list[2]!["display"]!);
    }

    [Fact]
    public async Task Balance_of_an_address_the_user_does_not_own_is_refused_without_reading_it()
    {
        var mcp = new FakeMcp();
        var r = await new WalletBalanceExecutor(mcp).ExecuteAsync(Ctx("wallet.balance"), Args("STRANGER999"), default);
        Assert.True(r.IsError);
        Assert.DoesNotContain(mcp.Calls, c => c.StartsWith("getCryptoBalance"));
    }

    [Fact]
    public async Task Executors_refuse_an_anonymous_session_and_make_no_mcp_call()
    {
        var mcp = new FakeMcp();
        var a = await new WalletListAddressesExecutor(mcp).ExecuteAsync(Ctx("wallet.listAddresses", SessionTier.Anonymous), new JsonObject { ["network"] = "testnet" }, default);
        var b = await new WalletBalanceExecutor(mcp).ExecuteAsync(Ctx("wallet.balance", SessionTier.Anonymous), Args(), default);
        Assert.True(a.IsError);
        Assert.True(b.IsError);
        Assert.Empty(mcp.Calls);
    }

    [Fact]
    public void Gate_hides_and_rejects_the_wallet_read_tools_for_anonymous_and_scope_less_sessions()
    {
        var sink = new InMemoryPolicyEventSink();
        var gate = new ToolGate(CapabilityRegistry.CreateDefault(), sink);
        var anon = new GateContext(SessionTier.Anonymous, ["openid"]);
        Assert.DoesNotContain(gate.ToolsFor(anon), c => c.Name.StartsWith("wallet."));
        Assert.False(gate.Check("wallet.listAddresses", new JsonObject { ["network"] = "testnet" }, anon).Allowed);
        Assert.False(gate.Check("wallet.balance", Args(), anon).Allowed);
        Assert.False(gate.Check("wallet.listAddresses", new JsonObject { ["network"] = "testnet" }, new GateContext(SessionTier.Authenticated, [])).Allowed);

        var auth = new GateContext(SessionTier.Authenticated, ["openid"]);
        Assert.True(gate.Check("wallet.listAddresses", new JsonObject { ["network"] = "testnet" }, auth).Allowed);
        Assert.True(gate.Check("wallet.balance", Args(), auth).Allowed);
    }

    [Fact]
    public async Task Missing_bearer_and_mcp_errors_become_tool_errors_not_exceptions()
    {
        var noAuth = new FakeMcp { Throw = new McpAuthenticationException("sess") };
        var r1 = await new WalletListAddressesExecutor(noAuth).ExecuteAsync(Ctx("wallet.listAddresses"), new JsonObject { ["network"] = "testnet" }, default);
        Assert.True(r1.IsError);

        var failing = new FakeMcp { Addresses = () => new McpToolResult(true, "boom with secret", null) };
        var r2 = await new WalletListAddressesExecutor(failing).ExecuteAsync(Ctx("wallet.listAddresses"), new JsonObject { ["network"] = "testnet" }, default);
        Assert.True(r2.IsError);
        Assert.DoesNotContain("secret", r2.Content);
    }

    [Fact]
    public void Executors_serve_the_registered_capability_names_with_status_lines()
    {
        var registry = CapabilityRegistry.CreateDefault();
        var mcp = new FakeMcp();
        foreach (IToolExecutor e in new IToolExecutor[] { new WalletListAddressesExecutor(mcp), new WalletBalanceExecutor(mcp) })
        {
            Assert.NotNull(registry.Find(e.CapabilityName));
            Assert.False(string.IsNullOrWhiteSpace(e.StatusLabel(new JsonObject())));
        }
    }

    [Fact]
    public async Task ListAddresses_on_bitcoin_returns_labelled_primary_and_secondary_addresses_via_getCryptoAddress()
    {
        const string network = "bitcoin";
        var mcp = new FakeMcp();
        var r = await new WalletListAddressesExecutor(mcp).ExecuteAsync(Ctx("wallet.listAddresses"), new JsonObject { ["network"] = network }, default);
        Assert.False(r.IsError);
        var o = JsonNode.Parse(r.Content)!;
        Assert.Equal(network, (string)o["network"]!);
        Assert.Equal(2, (int)o["count"]!);
        Assert.Equal(BtcPrimary, (string)o["addresses"]![0]!["address"]!);
        Assert.Equal("primary", (string)o["addresses"]![0]!["label"]!);
        Assert.Equal(BtcSecondary, (string)o["addresses"]![1]!["address"]!);
        Assert.Equal("secondary", (string)o["addresses"]![1]!["label"]!);
        Assert.Equal([$"getCryptoAddress:{network}:0", $"getCryptoAddress:{network}:1"], mcp.Calls);
    }

    [Fact]
    public async Task ListAddresses_on_bitcoin_drops_a_secondary_that_repeats_the_primary_or_fails()
    {
        var same = new FakeMcp { BtcAddress = _ => FakeMcp.Ok($"{{\"address\":\"{BtcPrimary}\"}}") };
        var r1 = await new WalletListAddressesExecutor(same).ExecuteAsync(Ctx("wallet.listAddresses"), new JsonObject { ["network"] = "bitcoin" }, default);
        Assert.Equal(1, (int)JsonNode.Parse(r1.Content)!["count"]!);

        var failing = new FakeMcp { BtcAddress = slot => slot == 0 ? FakeMcp.Ok($"{{\"address\":\"{BtcPrimary}\"}}") : FakeMcp.Ok("{\"address\":\"\",\"error\":\"boom\"}") };
        var r2 = await new WalletListAddressesExecutor(failing).ExecuteAsync(Ctx("wallet.listAddresses"), new JsonObject { ["network"] = "bitcoin" }, default);
        Assert.Equal(1, (int)JsonNode.Parse(r2.Content)!["count"]!);
    }

    [Fact]
    public async Task ListAddresses_on_bitcoin_is_an_error_when_the_primary_cannot_be_read()
    {
        var mcp = new FakeMcp { BtcAddress = _ => FakeMcp.Ok("{\"network\":\"bitcoin\",\"address\":\"\",\"error\":\"secret detail\"}") };
        var r = await new WalletListAddressesExecutor(mcp).ExecuteAsync(Ctx("wallet.listAddresses"), new JsonObject { ["network"] = "bitcoin" }, default);
        Assert.True(r.IsError);
        Assert.DoesNotContain("secret", r.Content);
    }

    [Fact]
    public async Task Balance_on_bitcoin_returns_sats_with_8_decimals_and_the_address_label()
    {
        var mcp = new FakeMcp
        {
            Balance = () => FakeMcp.Ok($"{{\"network\":\"Bitcoin\",\"family\":\"Btc\",\"address\":\"{BtcSecondary}\",\"nativeCurrencySymbol\":\"BTC\",\"nativeBalance\":0.00012345,\"nativeBalanceBaseUnits\":\"12345\",\"error\":\"\"}}"),
        };
        var r = await new WalletBalanceExecutor(mcp).ExecuteAsync(Ctx("wallet.balance"), new JsonObject { ["network"] = "bitcoin", ["address"] = BtcSecondary }, default);
        Assert.False(r.IsError);
        var o = JsonNode.Parse(r.Content)!;
        Assert.Equal("secondary", (string)o["label"]!);
        var b = o["balances"]![0]!;
        Assert.Equal("BTC", (string)b["symbol"]!);
        Assert.Equal("sats", (string)b["unit"]!);
        Assert.Equal("12345", (string)b["amount"]!);
        Assert.Equal(8, (int)b["decimals"]!);
        Assert.Equal("0.00012345", (string)b["display"]!);
        Assert.Contains($"getCryptoBalance:bitcoin:{BtcSecondary}", mcp.Calls);
        Assert.DoesNotContain("listAlgorandAddresses", mcp.Calls);
    }

    [Fact]
    public async Task Balance_on_bitcoin_keeps_amounts_above_2_pow_53_exact_as_strings()
    {
        var mcp = new FakeMcp { Balance = () => FakeMcp.Ok("{\"nativeBalanceBaseUnits\":\"9007199254740993\"}") };
        var r = await new WalletBalanceExecutor(mcp).ExecuteAsync(Ctx("wallet.balance"), new JsonObject { ["network"] = "bitcoin", ["address"] = BtcPrimary }, default);
        var b = JsonNode.Parse(r.Content)!["balances"]![0]!;
        Assert.Equal(JsonValueKind.String, b["amount"]!.GetValueKind());
        Assert.Equal("9007199254740993", (string)b["amount"]!);
        Assert.Equal("90071992.54740993", (string)b["display"]!);
    }

    [Fact]
    public async Task Balance_on_bitcoin_refuses_an_address_the_user_does_not_own_without_reading_it()
    {
        var mcp = new FakeMcp();
        var r = await new WalletBalanceExecutor(mcp).ExecuteAsync(Ctx("wallet.balance"), new JsonObject { ["network"] = "bitcoin", ["address"] = "bc1qstranger" }, default);
        Assert.True(r.IsError);
        Assert.DoesNotContain(mcp.Calls, c => c.StartsWith("getCryptoBalance"));
    }

    [Fact]
    public async Task Balance_on_bitcoin_reports_an_mcp_error_field_as_a_tool_error()
    {
        var mcp = new FakeMcp { Balance = () => FakeMcp.Ok("{\"error\":\"Could not reach the block explorer\",\"errorType\":\"DataSourceUnavailable\"}") };
        var r = await new WalletBalanceExecutor(mcp).ExecuteAsync(Ctx("wallet.balance"), new JsonObject { ["network"] = "bitcoin", ["address"] = BtcPrimary }, default);
        Assert.True(r.IsError);
    }

    [Fact]
    public void Gate_accepts_the_wallet_transfer_bitcoin_network_names_for_the_read_tools()
    {
        // With Bitcoin transfers enabled the read tools and wallet.transfer share one list of network names; by default
        // wallet.transfer leaves bitcoin out (BCHAT-037) while the read tools keep it.
        var registry = CapabilityRegistry.CreateDefault(bitcoinTransfers: true);
        var gate = new ToolGate(registry, new InMemoryPolicyEventSink());
        var auth = new GateContext(SessionTier.Authenticated, ["openid"]);
        var defaultTransferNetworks = CapabilityRegistry.CreateDefault().Find("wallet.transfer")!.InputSchema!["properties"]!["network"]!["enum"]!.AsArray().Select(n => (string)n!).ToArray();
        Assert.Equal(["mainnet", "testnet"], defaultTransferNetworks);
        var transferNetworks = registry.Find("wallet.transfer")!.InputSchema!["properties"]!["network"]!["enum"]!.AsArray().Select(n => (string)n!).ToArray();
        foreach (var name in new[] { "wallet.listAddresses", "wallet.balance" })
            Assert.Equal(transferNetworks, registry.Find(name)!.InputSchema!["properties"]!["network"]!["enum"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.True(gate.Check("wallet.listAddresses", new JsonObject { ["network"] = "bitcoin" }, auth).Allowed);
        Assert.True(gate.Check("wallet.balance", new JsonObject { ["network"] = "bitcoin", ["address"] = BtcPrimary }, auth).Allowed);
        Assert.False(gate.Check("wallet.balance", new JsonObject { ["network"] = "bitcoin", ["address"] = BtcPrimary }, new GateContext(SessionTier.Anonymous, ["openid"])).Allowed);
        // Biatec MCP serves no Bitcoin testnet, so none is offered (read tools or wallet.transfer).
        Assert.DoesNotContain("bitcoin-testnet", transferNetworks);
        Assert.False(gate.Check("wallet.listAddresses", new JsonObject { ["network"] = "bitcoin-testnet" }, auth).Allowed);
    }

    [Fact]
    public async Task ListAddresses_on_bitcoin_leaves_out_a_secondary_the_client_cannot_read()
    {
        var mcp = new FakeMcp { BtcAddress = slot => slot == 0 ? FakeMcp.Ok($"{{\"address\":\"{BtcPrimary}\"}}") : throw new NotSupportedException() };
        var r = await new WalletListAddressesExecutor(mcp).ExecuteAsync(Ctx("wallet.listAddresses"), new JsonObject { ["network"] = "bitcoin" }, default);
        Assert.False(r.IsError);
        Assert.Equal(1, (int)JsonNode.Parse(r.Content)!["count"]!);
    }

    [Fact]
    public async Task Balance_on_bitcoin_matches_bech32_addresses_case_insensitively()
    {
        var mcp = new FakeMcp { Balance = () => FakeMcp.Ok("{\"nativeBalanceBaseUnits\":\"1\"}") };
        var r = await new WalletBalanceExecutor(mcp).ExecuteAsync(Ctx("wallet.balance"), new JsonObject { ["network"] = "bitcoin", ["address"] = BtcSecondary.ToUpperInvariant() }, default);
        Assert.False(r.IsError, r.Content);
        Assert.Equal(BtcSecondary, (string)JsonNode.Parse(r.Content)!["address"]!);
        Assert.Contains($"getCryptoBalance:bitcoin:{BtcSecondary}", mcp.Calls);
    }

    [Fact]
    public async Task Balance_on_algorand_shapes_the_real_mcp_response_native_algo_and_asas()
    {
        var mcp = new FakeMcp
        {
            Balance = () => FakeMcp.Ok($"{{\"network\":\"Algorand Testnet\",\"family\":\"Avm\",\"address\":\"{Mine}\",\"nativeCurrencySymbol\":\"ALGO\",\"nativeBalance\":1.234567,\"nativeBalanceBaseUnits\":\"1234567\",\"assets\":[{{\"assetId\":31566704,\"amount\":18446744073709551615}}],\"assetsTruncated\":true,\"error\":\"\",\"errorType\":\"\"}}"),
        };
        var r = await new WalletBalanceExecutor(mcp).ExecuteAsync(Ctx("wallet.balance"), Args(), default);
        Assert.False(r.IsError, r.Content);
        var o = JsonNode.Parse(r.Content)!;
        Assert.Equal("testnet", (string)o["network"]!);
        Assert.True((bool)o["assetsTruncated"]!);
        var list = o["balances"]!.AsArray();
        Assert.Equal(2, list.Count);
        Assert.Equal("ALGO", (string)list[0]!["symbol"]!);
        Assert.Equal("1234567", (string)list[0]!["amount"]!);
        Assert.Equal("1.234567", (string)list[0]!["display"]!);
        Assert.Equal("31566704", (string)list[1]!["assetId"]!);
        Assert.Equal(JsonValueKind.String, list[1]!["amount"]!.GetValueKind());
        Assert.Equal("18446744073709551615", (string)list[1]!["amount"]!);
    }

    [Fact]
    public async Task Balance_on_algorand_treats_a_non_empty_error_field_as_a_failed_read()
    {
        var mcp = new FakeMcp { Balance = () => FakeMcp.Ok("{\"network\":\"algorand-testnet\",\"error\":\"account not found secret\"}") };
        var r = await new WalletBalanceExecutor(mcp).ExecuteAsync(Ctx("wallet.balance"), Args(), default);
        Assert.True(r.IsError);
        Assert.DoesNotContain("secret", r.Content);
    }
}
