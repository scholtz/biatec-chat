using System.Text.Json.Nodes;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Tests.Policy;

/// <summary>
/// Policy regression net, gate level (no database): the anonymous tier can never reach a wallet tool, signing needs the
/// sign scope, and anything not in the registry is refused. Every rejection must be logged as a policy event.
/// </summary>
public class TierPolicyGateTests
{
    private static readonly string[] AllScopes = ["openid", "sign", "manage-limits"];

    private static (ToolGate gate, InMemoryPolicyEventSink sink) Make()
    {
        var sink = new InMemoryPolicyEventSink();
        return (new ToolGate(CapabilityRegistry.CreateDefault(), sink), sink);
    }

    private static IEnumerable<Capability> Registry() => CapabilityRegistry.CreateDefault().All;

    public static IEnumerable<object[]> NonAnonymousTools() =>
        Registry().Where(c => c.MinTier != SessionTier.Anonymous).Select(c => new object[] { c.Name });

    public static IEnumerable<object[]> SigningTools() =>
        Registry().Where(c => c.SignScope == "sign").Select(c => new object[] { c.Name });

    private static GateContext Anon(string[]? scopes = null) =>
        new(SessionTier.Anonymous, scopes ?? AllScopes, X402Enabled: true, LimitsUpdateEnabled: true, SessionId: "s-anon", TenantId: "t1");

    private static GateContext Auth(params string[] scopes) =>
        new(SessionTier.Authenticated, scopes, X402Enabled: true, LimitsUpdateEnabled: true, SessionId: "s-auth", TenantId: "t1");

    [Fact]
    public void Registry_has_wallet_tools_to_protect()
    {
        // Guards the guard: if the registry were empty the theories below would pass vacuously.
        Assert.True(NonAnonymousTools().Count() >= 10);
        Assert.True(SigningTools().Count() >= 5);
    }

    [Theory]
    [MemberData(nameof(NonAnonymousTools))]
    public void Forged_wallet_call_in_anonymous_tier_is_rejected_and_logged(string tool)
    {
        var (gate, sink) = Make();
        // Even with every scope and flag set, and with arguments that would otherwise be schema-valid.
        var args = new JsonObject { ["network"] = "testnet", ["from"] = "AAA", ["to"] = "BBB", ["assetId"] = 0, ["amount"] = "1" };

        var d = gate.Check(tool, args, Anon());

        Assert.False(d.Allowed);
        Assert.Equal("tier_too_low", d.Reason);
        var e = Assert.Single(sink.Events);
        Assert.Equal("tool_call_rejected", e.Kind);
        Assert.Equal(tool, e.ToolName);
        Assert.Equal("tier_too_low", e.Reason);
        Assert.Equal(SessionTier.Anonymous, e.Tier);
        Assert.Equal("s-anon", e.SessionId);
    }

    [Theory]
    [MemberData(nameof(SigningTools))]
    public void Signing_check_in_anonymous_tier_is_refused_even_with_sign_scope(string tool)
    {
        var (gate, sink) = Make();
        var d = gate.CheckSigningScope(tool, Anon());
        Assert.False(d.Allowed);
        Assert.Equal("tier_too_low", d.Reason);
        Assert.Single(sink.Events);
    }

    [Fact]
    public void Anonymous_model_tool_list_never_contains_a_wallet_tool()
    {
        var (gate, _) = Make();
        var allowed = new[] { "kb.search", "market.snapshot", "requestSignIn" };
        Assert.Equal(allowed, gate.ToolsFor(Anon()).Select(c => c.ToolName!).OrderBy(x => x).ToArray());
        Assert.Equal(allowed, gate.ToolsFor(new GateContext(SessionTier.Anonymous, null!)).Select(c => c.ToolName!).OrderBy(x => x).ToArray());
    }

    [Theory]
    [MemberData(nameof(SigningTools))]
    public void Signing_without_sign_scope_is_refused(string tool)
    {
        var (gate, sink) = Make();
        var d = gate.CheckSigningScope(tool, Auth("openid"));
        Assert.False(d.Allowed);
        Assert.Equal("scope_missing", d.Reason);
        Assert.Contains("re-authorize", d.ModelError);
        var e = Assert.Single(sink.Events);
        Assert.Equal("scope_missing", e.Reason);
        Assert.Equal(tool, e.ToolName);
    }

    [Theory]
    [MemberData(nameof(SigningTools))]
    public void Signing_with_sign_scope_is_allowed_only_for_authenticated(string tool)
    {
        var (gate, sink) = Make();
        Assert.True(gate.CheckSigningScope(tool, Auth("openid", "sign")).Allowed);
        Assert.Empty(sink.Events);
        Assert.False(gate.CheckSigningScope(tool, Anon(["openid", "sign"])).Allowed);
    }

    [Fact]
    public void Limits_update_needs_manage_limits_and_sign_scope_does_not_substitute()
    {
        var (gate, sink) = Make();
        Assert.False(gate.CheckSigningScope("limits.update", Auth("openid", "sign")).Allowed);
        Assert.Equal("scope_missing", Assert.Single(sink.Events).Reason);
        Assert.True(gate.CheckSigningScope("limits.update", Auth("openid", "manage-limits")).Allowed);
    }

    [Fact]
    public void Scope_names_are_matched_exactly()
    {
        var (gate, _) = Make();
        foreach (var scope in new[] { "SIGN", "sign ", "signing", "openid sign", "sign\0", "" })
            Assert.False(gate.CheckSigningScope("wallet.transfer", Auth("openid", scope)).Allowed, scope);
    }

    [Fact]
    public void Non_signing_capability_cannot_pass_the_signing_check()
    {
        var (gate, _) = Make();
        Assert.Equal("not_signing_capability", gate.CheckSigningScope("wallet.balance", Auth("openid", "sign")).Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("wallet.sendEverything")]
    [InlineData("WALLET.TRANSFER")]
    [InlineData("wallet_transfer")]
    [InlineData(" wallet.transfer")]
    [InlineData("wallet.transfer ")]
    [InlineData("wallet.transfer\0")]
    [InlineData("wallet.transfer/../kb.search")]
    [InlineData("signTransaction")]
    [InlineData("mcp.signTransaction")]
    [InlineData("__proto__")]
    [InlineData("explain.product")]
    public void Unknown_or_non_callable_tool_is_rejected_for_every_tier(string tool)
    {
        foreach (var ctx in new[] { Anon(), Auth("openid", "sign", "manage-limits") })
        {
            var (gate, sink) = Make();
            var d = gate.Check(tool, new JsonObject(), ctx);
            Assert.False(d.Allowed, tool);
            Assert.Contains(d.Reason, new[] { "unknown_tool", "not_a_tool" });
            Assert.Equal("tool_call_rejected", Assert.Single(sink.Events).Kind);
        }
    }

    [Fact]
    public void Null_tool_name_and_huge_name_are_rejected_and_logged_sanitized()
    {
        var (gate, sink) = Make();
        Assert.False(gate.Check(null, null, Anon()).Allowed);
        Assert.False(gate.Check(new string('x', 100_000) + "\n<script>", null, Anon()).Allowed);
        Assert.Equal(2, sink.Events.Count);
        Assert.All(sink.Events, e => Assert.True(e.ToolName.Length <= 64 && !e.ToolName.Contains('<')));
    }

    [Fact]
    public void Anonymous_rejection_points_at_sign_in_and_not_at_wallet_details()
    {
        var (gate, _) = Make();
        var d = gate.Check("wallet.balance", new JsonObject(), Anon());
        Assert.False(d.Allowed);
        Assert.Contains("requestSignIn", d.ModelError);
    }

    private sealed class ThrowingSink : IPolicyEventSink
    {
        public void Emit(PolicyEvent policyEvent) => throw new InvalidOperationException("log store down");
    }

    [Fact]
    public void Failing_policy_sink_never_turns_a_rejection_into_an_allowance_or_an_exception()
    {
        var gate = new ToolGate(CapabilityRegistry.CreateDefault(), new ThrowingSink());
        Assert.False(gate.Check("wallet.transfer", new JsonObject(), Anon()).Allowed);
        Assert.False(gate.Check("nope", new JsonObject(), Anon()).Allowed);
        Assert.False(gate.CheckSigningScope("wallet.transfer", Auth("openid")).Allowed);
    }

    [Fact]
    public void Policy_events_never_contain_tool_arguments()
    {
        var (gate, sink) = Make();
        gate.Check("wallet.transfer", new JsonObject { ["amount"] = "SECRET-AMOUNT-123" }, Anon());
        Assert.DoesNotContain("SECRET-AMOUNT-123", System.Text.Json.JsonSerializer.Serialize(sink.Events));
    }
}
