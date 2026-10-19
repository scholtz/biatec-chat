using System.Text.Json.Nodes;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Tests.Capabilities;

public class ToolGateTests
{
    private static readonly string[] Openid = ["openid"];
    private static readonly string[] OpenidSign = ["openid", "sign"];

    private static (ToolGate gate, InMemoryPolicyEventSink sink) Make()
    {
        var sink = new InMemoryPolicyEventSink();
        return (new ToolGate(CapabilityRegistry.CreateDefault(), sink), sink);
    }

    private static GateContext Anon() => new(SessionTier.Anonymous, []);
    private static GateContext Auth(params string[] scopes) => new(SessionTier.Authenticated, scopes);

    private static JsonObject Transfer() => new()
    {
        ["network"] = "testnet", ["from"] = "AAA", ["to"] = "BBB", ["assetId"] = 0, ["amount"] = "1000",
    };

    [Fact]
    public void Anonymous_tool_list_is_exactly_requestSignIn_and_kb_search()
    {
        var (gate, _) = Make();
        var names = gate.ToolsFor(Anon()).Select(c => c.ToolName).OrderBy(x => x).ToArray();
        Assert.Equal(["kb.search", "requestSignIn"], names);
    }

    [Fact]
    public void Anonymous_tier_never_sees_wallet_tools_even_with_scopes_and_flags()
    {
        var (gate, _) = Make();
        var ctx = new GateContext(SessionTier.Anonymous, ["openid", "sign", "manage-limits"], true, true);
        Assert.Equal(["kb.search", "requestSignIn"], gate.ToolsFor(ctx).Select(c => c.ToolName).OrderBy(x => x).ToArray());
        foreach (var cap in gate.Registry.All.Where(c => c.MinTier != SessionTier.Anonymous))
        {
            var d = gate.Check(cap.Name, new JsonObject(), ctx);
            Assert.False(d.Allowed);
            Assert.Equal("tier_too_low", d.Reason);
        }
    }

    [Fact]
    public void Gate_rejects_wallet_tool_call_in_anonymous_tier_and_emits_policy_event()
    {
        var (gate, sink) = Make();
        var d = gate.Check("wallet.transfer", Transfer(), new GateContext(SessionTier.Anonymous, Openid, SessionId: "s1", TenantId: "t1"));
        Assert.False(d.Allowed);
        Assert.Equal("tier_too_low", d.Reason);
        Assert.Contains("requestSignIn", d.ModelError);
        var e = Assert.Single(sink.Events);
        Assert.Equal("tool_call_rejected", e.Kind);
        Assert.Equal("wallet.transfer", e.ToolName);
        Assert.Equal(SessionTier.Anonymous, e.Tier);
        Assert.Equal("s1", e.SessionId);
    }

    [Fact]
    public void Gate_rejects_schema_invalid_arguments()
    {
        var (gate, sink) = Make();
        var bad = Transfer();
        bad["amount"] = "-5";
        bad["extra"] = true;
        bad.Remove("to");
        var d = gate.Check("wallet.transfer", bad, Auth("openid"));
        Assert.False(d.Allowed);
        Assert.Equal("schema_invalid", d.Reason);
        var e = Assert.Single(sink.Events);
        Assert.NotNull(e.Violations);
        Assert.Contains(e.Violations!, v => v.Contains("$.to"));
        Assert.Contains(e.Violations!, v => v.Contains("$.extra"));
        Assert.Contains(e.Violations!, v => v.Contains("$.amount"));
    }

    [Theory]
    [InlineData("""{"network":"testnet","from":"A","to":"B","assetId":1.5,"amount":"1"}""")]
    [InlineData("""{"network":"testnet","from":"A","to":"B","assetId":-1,"amount":"1"}""")]
    [InlineData("""{"network":"regtest","from":"A","to":"B","assetId":1,"amount":"1"}""")]
    [InlineData("""{"network":"testnet","from":"A","to":"B","assetId":"1","amount":"1"}""")]
    [InlineData("""{"network":"testnet","from":"A","to":"B","assetId":1,"amount":1}""")]
    public void Gate_rejects_wrong_types_and_values(string json)
    {
        var (gate, _) = Make();
        Assert.False(gate.Check("wallet.transfer", JsonNode.Parse(json), Auth("openid")).Allowed);
    }

    [Fact]
    public void Gate_rejects_non_object_arguments()
    {
        var (gate, sink) = Make();
        Assert.False(gate.Check("kb.search", JsonNode.Parse("[1]"), Anon()).Allowed);
        Assert.False(gate.Check("kb.search", JsonValue.Create("x"), Anon()).Allowed);
        Assert.Equal(2, sink.Events.Count);
    }

    [Fact]
    public void Valid_calls_pass_and_emit_nothing()
    {
        var (gate, sink) = Make();
        Assert.True(gate.Check("kb.search", new JsonObject { ["query"] = "fees" }, Anon()).Allowed);
        Assert.True(gate.Check("requestSignIn", null, Anon()).Allowed);
        Assert.True(gate.Check("wallet.transfer", Transfer(), Auth("openid")).Allowed);
        Assert.True(gate.Check("limits.view", null, Auth("openid")).Allowed);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void Unknown_and_toolless_capabilities_are_rejected_with_events()
    {
        var (gate, sink) = Make();
        Assert.Equal("unknown_tool", gate.Check("wallet.rekey", new JsonObject(), Auth("openid", "rekey")).Reason);
        Assert.Equal("unknown_tool", gate.Check(null, null, Anon()).Reason);
        Assert.Equal("not_a_tool", gate.Check("explain.product", new JsonObject(), Anon()).Reason);
        Assert.Equal(3, sink.Events.Count);
    }

    [Fact]
    public void Authenticated_tool_list_requires_openid_and_hides_flagged_capabilities()
    {
        var (gate, _) = Make();
        Assert.Equal(["kb.search", "requestSignIn"], gate.ToolsFor(Auth()).Select(c => c.ToolName).OrderBy(x => x).ToArray());
        var names = gate.ToolsFor(Auth("openid", "sign")).Select(c => c.ToolName).ToHashSet();
        Assert.Contains("wallet.transfer", names);
        Assert.DoesNotContain("x402.pay", names);
        Assert.DoesNotContain("limits.update", names);
        Assert.DoesNotContain("explain.product", names);
    }

    [Fact]
    public void X402_is_behind_a_flag()
    {
        var (gate, sink) = Make();
        var args = new JsonObject { ["network"] = "testnet", ["from"] = "A", ["resourceUrl"] = "https://x" };
        var off = gate.Check("x402.pay", args, Auth("openid"));
        Assert.Equal("feature_disabled", off.Reason);
        Assert.Single(sink.Events);
        var on = new GateContext(SessionTier.Authenticated, Openid, X402Enabled: true);
        Assert.Contains(gate.ToolsFor(on), c => c.Name == "x402.pay");
        Assert.True(gate.Check("x402.pay", args, on).Allowed);
    }

    [Fact]
    public void Limits_update_needs_tenant_flag_and_manage_limits_scope()
    {
        var (gate, _) = Make();
        var args = new JsonObject { ["daily"] = "100" };
        Assert.Equal("feature_disabled", gate.Check("limits.update", args, new GateContext(SessionTier.Authenticated, ["manage-limits"])).Reason);
        Assert.Equal("scope_missing", gate.Check("limits.update", args, new GateContext(SessionTier.Authenticated, Openid, LimitsUpdateEnabled: true)).Reason);
        Assert.True(gate.Check("limits.update", args, new GateContext(SessionTier.Authenticated, ["manage-limits"], LimitsUpdateEnabled: true)).Allowed);
    }

    [Fact]
    public void Missing_openid_scope_rejects_authenticated_tool()
    {
        var (gate, sink) = Make();
        var d = gate.Check("wallet.balance", new JsonObject { ["network"] = "testnet", ["address"] = "A" }, Auth());
        Assert.Equal("scope_missing", d.Reason);
        Assert.Single(sink.Events);
    }

    [Fact]
    public void Signing_scope_check_requires_sign()
    {
        var (gate, sink) = Make();
        var d = gate.CheckSigningScope("wallet.transfer", Auth("openid"));
        Assert.False(d.Allowed);
        Assert.Equal("scope_missing", d.Reason);
        Assert.Single(sink.Events);
        Assert.True(gate.CheckSigningScope("wallet.transfer", Auth("openid", "sign")).Allowed);
        Assert.False(gate.CheckSigningScope("wallet.transfer", Anon()).Allowed);
    }

    [Fact]
    public void Registry_declares_funds_movement_and_sign_scope()
    {
        var reg = CapabilityRegistry.CreateDefault();
        foreach (var n in new[] { "wallet.transfer", "dex.swap", "dex.addLiquidity", "dex.removeLiquidity", "safe.approve", "safe.execute", "x402.pay" })
            Assert.True(reg.Find(n)!.MovesFunds, n);
        Assert.False(reg.Find("safe.propose")!.MovesFunds);
        Assert.All(reg.All.Where(c => c.MovesFunds), c => Assert.Equal("sign", c.SignScope));
        Assert.DoesNotContain(reg.All, c => c.BuildScopes.Contains("rekey") || c.SignScope == "rekey");
        Assert.Equal(ValuationBasis.DecodedByOidc, reg.Find("x402.pay")!.Valuation);
    }

    [Fact]
    public void Long_or_control_character_tool_names_are_sanitized_in_events()
    {
        var (gate, sink) = Make();
        gate.Check("bad\nname" + new string('x', 200), null, Anon());
        var e = Assert.Single(sink.Events);
        Assert.DoesNotContain('\n', e.ToolName);
        Assert.True(e.ToolName.Length <= 64);
    }
}
