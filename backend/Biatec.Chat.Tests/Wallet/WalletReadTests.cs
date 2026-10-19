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
        Assert.Contains($"getCryptoBalance:testnet:{Mine}", mcp.Calls);
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
}
