using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Algorand;
using Algorand.Algod.Model.Transactions;
using Biatec.Chat.Tools.Builders;
using Biatec.Chat.Tools.Safe.Create;
using Biatec.Chat.Tools.Safe.Proposals;
using AlgoEncoder = Algorand.Utils.Encoder;

namespace Biatec.Chat.Tests.Safe.Proposals;

/// <summary>Real algo-safe propose / approve / execute output captured from the builder sidecar (builders/sidecar, fake algod).</summary>
internal static class ProposalFixture
{
    public static JsonElement Load([CallerFilePath] string here = "") =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", "sidecar-safe-proposals.json"))).RootElement.Clone();

    public static BuildResult Result(JsonElement step) => new(
        step.GetProperty("transactions").EnumerateArray().Select(t => t.GetString()!).ToList(), Preview: step.GetProperty("preview").Clone());

    public static Transaction Tx(JsonElement step) => Transaction.FromBase64String(step.GetProperty("transactions")[0].GetString()!);

    public static void SetArgs(Transaction tx, List<byte[]> args) => tx.GetType().GetProperty("ApplicationArgs")!.SetValue(tx, args);

    public static IReadOnlyList<byte[]> Args(Transaction tx) => SafeAbi.Args(tx);
}

/// <summary>SafeProposalBuilder and SafeProposalAbi against the sidecar's real output: what is accepted and what is refused.</summary>
public sealed class SafeProposalBuilderTests
{
    private static readonly JsonElement Fx = ProposalFixture.Load();
    private static string S(string name) => Fx.GetProperty(name).GetString()!;
    private static ulong U(string name) => ulong.Parse(S(name));
    private static string Alice => S("alice");
    private static ulong App => U("appId");
    private static string SafeAddress => SafeCreateService.SafeAddress(App);

    private sealed class OneShot(Func<string, JsonElement, BuildResult> build) : IBuildersClient
    {
        public readonly List<(string Name, JsonElement Params)> Calls = [];
        public Task<bool> IsHealthyAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<BuildResult> BuildAsync(string builder, string network, object? parameters, CancellationToken ct = default)
        {
            var p = JsonSerializer.SerializeToElement(parameters);
            Calls.Add((builder, p));
            return Task.FromResult(build(builder, p));
        }
    }

    private static SafeProposalBuilder Builder(string step, out OneShot client)
    {
        client = new OneShot((_, _) => ProposalFixture.Result(Fx.GetProperty(step)));
        return new SafeProposalBuilder(client);
    }

    /// <summary>The fixture step with its preview edited, or its transaction's arguments replaced.</summary>
    private static BuildResult Edited(string step, Action<JsonObject>? preview = null, Func<IReadOnlyList<byte[]>, List<byte[]>>? args = null)
    {
        var node = JsonNode.Parse(Fx.GetProperty(step).GetRawText())!;
        preview?.Invoke(node["preview"]!.AsObject());
        if (args is not null)
        {
            var tx = ProposalFixture.Tx(Fx.GetProperty(step));
            ProposalFixture.SetArgs(tx, args(ProposalFixture.Args(tx)));
            node["transactions"] = new JsonArray(Convert.ToBase64String(AlgoEncoder.EncodeToMsgPackOrdered(tx)));
        }
        return ProposalFixture.Result(JsonDocument.Parse(node.ToJsonString()).RootElement);
    }

    [Fact]
    public void SafeProposals_abi_decodes_the_real_calls_and_round_trips_their_bytes()
    {
        var pay = ProposalFixture.Tx(Fx.GetProperty("payment"));
        Assert.IsType<ApplicationNoopTransaction>(pay);
        var p = SafeProposalAbi.Propose(pay);
        Assert.Equal(1UL, p.GroupId);
        Assert.False(p.Execute);
        var e = Assert.Single(p.Entries!);
        Assert.True(e.SameAs(SafePayloadEntry.Pay(S("receiver"), 2_500_000)));
        Assert.Equal(SafeProposalAbi.EncodePayload(p.Entries!), ProposalFixture.Args(pay)[2]);
        // The sidecar's payload hash is over the same bytes.
        Assert.Equal(Fx.GetProperty("payment").GetProperty("preview").GetProperty("payloadHash").GetString(), p.PayloadHash);

        var liq = SafeProposalAbi.Propose(ProposalFixture.Tx(Fx.GetProperty("liquidity")));
        Assert.Equal([SafeTxType.AssetTransfer, SafeTxType.AppCall, SafeTxType.AssetTransfer, SafeTxType.AssetTransfer, SafeTxType.AppCall], liq.Entries!.Select(x => x.TxType));
        Assert.All(liq.Entries!, x => Assert.Null(x.Sender)); // the safe itself
        Assert.Equal(U("pool"), liq.Entries![4].AppId);

        var admin = SafeProposalAbi.Propose(ProposalFixture.Tx(Fx.GetProperty("admin")));
        Assert.Null(admin.Entries);
        Assert.Equal(new SafeAdminChange(SafeAdminChange.AddMember, 1, Member: S("carol")), admin.Change);

        Assert.Equal((1UL, 3UL), SafeProposalAbi.Approve(ProposalFixture.Tx(Fx.GetProperty("approve"))));
        Assert.Equal(2UL, SafeProposalAbi.Execute(ProposalFixture.Tx(Fx.GetProperty("execute"))).ProposalId);

        // A call of another kind is never mistaken for a proposal.
        Assert.Throws<SafeBuildException>(() => SafeProposalAbi.Propose(ProposalFixture.Tx(Fx.GetProperty("approve"))));
        Assert.Throws<SafeBuildException>(() => SafeProposalAbi.Approve(pay));
    }

    [Fact]
    public void SafeProposals_abi_refuses_bytes_that_do_not_round_trip()
    {
        var tx = ProposalFixture.Tx(Fx.GetProperty("payment"));
        var args = ProposalFixture.Args(tx).Select(a => a.ToArray()).ToList();
        // Trailing bytes after the payload, and a truncated payload.
        ProposalFixture.SetArgs(tx, [args[0], args[1], [.. args[2], 0], args[3], args[4], args[5]]);
        Assert.Throws<SafeBuildException>(() => SafeProposalAbi.Propose(tx));
        ProposalFixture.SetArgs(tx, [args[0], args[1], args[2][..^5], args[3], args[4], args[5]]);
        Assert.Throws<SafeBuildException>(() => SafeProposalAbi.Propose(tx));
        // execute = true is decoded as such (and refused by the builder).
        ProposalFixture.SetArgs(tx, [args[0], args[1], args[2], args[3], [0x80], args[5]]);
        Assert.True(SafeProposalAbi.Propose(tx).Execute);
    }

    [Fact]
    public async Task SafeProposals_builder_accepts_the_requested_payment_opt_in_and_admin_proposals()
    {
        var b = Builder("payment", out var client);
        var build = await b.BuildProposeAsync("testnet", Alice, App, 1, [SafePayloadEntry.Pay(S("receiver"), 2_500_000)]);
        Assert.Equal("safe.propose", build.Kind);
        var sent = Assert.Single(client.Calls);
        Assert.Equal("proposal.create", sent.Name);
        Assert.True(sent.Params.GetProperty("simulate").GetBoolean());
        Assert.Equal("payment", sent.Params.GetProperty("payload")[0].GetProperty("type").GetString());

        var optIn = await Builder("optIn", out _).BuildProposeAsync("testnet", Alice, App, 1,
            [SafePayloadEntry.Axfer(U("assetA"), SafeAddress, 0), SafePayloadEntry.Axfer(U("assetB"), SafeAddress, 0)]);
        Assert.Equal(2, optIn.Proposal!.Entries!.Count);

        var admin = await Builder("admin", out _).BuildProposeAdminAsync("testnet", Alice, App, 1,
            new SafeAdminChange(SafeAdminChange.AddMember, 1, Member: S("carol")), new { type = "addMember" });
        Assert.Equal(SafeAdminChange.AddMember, admin.Proposal!.Change!.ChangeType);
    }

    [Fact]
    public async Task SafeProposals_builder_refuses_a_proposal_that_differs_from_the_request()
    {
        var carol = S("carol");
        var b = Builder("payment", out _);
        await Assert.ThrowsAsync<SafeBuildException>(() => b.BuildProposeAsync("testnet", Alice, App, 1, [SafePayloadEntry.Pay(S("receiver"), 2_500_001)]));
        await Assert.ThrowsAsync<SafeBuildException>(() => b.BuildProposeAsync("testnet", Alice, App, 1, [SafePayloadEntry.Pay(carol, 2_500_000)]));
        await Assert.ThrowsAsync<SafeBuildException>(() => b.BuildProposeAsync("testnet", Alice, App, 2, [SafePayloadEntry.Pay(S("receiver"), 2_500_000)]));
        await Assert.ThrowsAsync<SafeBuildException>(() => b.BuildProposeAsync("testnet", carol, App, 1, [SafePayloadEntry.Pay(S("receiver"), 2_500_000)]));
        await Assert.ThrowsAsync<SafeBuildException>(() => b.BuildProposeAsync("testnet", Alice, App + 1, 1, [SafePayloadEntry.Pay(S("receiver"), 2_500_000)]));
        var admin = Builder("admin", out _);
        await Assert.ThrowsAsync<SafeBuildException>(() => admin.BuildProposeAdminAsync("testnet", Alice, App, 1, new SafeAdminChange(SafeAdminChange.AddMember, 1, Member: Alice), new { }));
        await Assert.ThrowsAsync<SafeBuildException>(() => admin.BuildProposeAdminAsync("testnet", Alice, App, 1, new SafeAdminChange(SafeAdminChange.RemoveMember, 1, Member: carol), new { }));
    }

    [Fact]
    public async Task SafeProposals_builder_trusts_the_bytes_not_the_sidecars_description()
    {
        // The description says the requested receiver; the bytes pay Carol. Refused.
        var carol = S("carol");
        var lying = Edited("payment", args: a =>
        {
            var payload = SafeProposalAbi.EncodePayload([SafePayloadEntry.Pay(carol, 2_500_000)]);
            return [a[0], a[1], payload, a[3], a[4], a[5]];
        });
        var b = new SafeProposalBuilder(new OneShot((_, _) => lying));
        await Assert.ThrowsAsync<SafeBuildException>(() => b.BuildProposeAsync("testnet", Alice, App, 1, [SafePayloadEntry.Pay(S("receiver"), 2_500_000)]));

        // A payload hash in the description that is not the hash of the bytes is refused too.
        var hash = Edited("payment", preview: p => p["payloadHash"] = new string('0', 64));
        await Assert.ThrowsAsync<SafeBuildException>(() => new SafeProposalBuilder(new OneShot((_, _) => hash))
            .BuildProposeAsync("testnet", Alice, App, 1, [SafePayloadEntry.Pay(S("receiver"), 2_500_000)]));

        // Execute-on-propose, a priced proposal and an unexpected fee are refused.
        var exec = Edited("payment", args: a => [a[0], a[1], a[2], a[3], [0x80], a[5]]);
        await Assert.ThrowsAsync<SafeBuildException>(() => new SafeProposalBuilder(new OneShot((_, _) => exec))
            .BuildProposeAsync("testnet", Alice, App, 1, [SafePayloadEntry.Pay(S("receiver"), 2_500_000)]));
        var priced = Edited("payment", preview: p => p["valueMicroAlgo"] = "1");
        await Assert.ThrowsAsync<SafeBuildException>(() => new SafeProposalBuilder(new OneShot((_, _) => priced))
            .BuildProposeAsync("testnet", Alice, App, 1, [SafePayloadEntry.Pay(S("receiver"), 2_500_000)]));
    }

    [Fact]
    public async Task SafeProposals_liquidity_payload_must_be_exactly_the_requested_deposit()
    {
        var e = new LiquidityExpectation(U("pool"), U("assetA"), U("assetB"), U("lp"), 2_000_000, 2_000_000);
        var b = Builder("liquidity", out var client);
        var build = await b.BuildProposeLiquidityAsync("testnet", Alice, App, 1, e);
        Assert.Equal(U("provider"), build.PoolProviderAppId);
        var sent = client.Calls.Single().Params.GetProperty("liquidity");
        Assert.Equal("2000000", sent.GetProperty("amountA").GetString());

        // Different amounts, pool or LP token: refused.
        await Assert.ThrowsAsync<SafeBuildException>(() => b.BuildProposeLiquidityAsync("testnet", Alice, App, 1, e with { AmountB = 1 }));
        await Assert.ThrowsAsync<SafeBuildException>(() => b.BuildProposeLiquidityAsync("testnet", Alice, App, 1, e with { PoolAppId = 601 }));

        var entries = SafeProposalAbi.Propose(ProposalFixture.Tx(Fx.GetProperty("liquidity"))).Entries!.ToList();
        var pool = SafeCreateService.SafeAddress(U("pool"));
        var apps = BiatecDexApps.For("testnet")!;
        Assert.Equal(apps.PoolProviderAppId, U("provider"));
        SafeProposalBuilder.CheckLiquidity(entries, App, e, apps);
        // A payment out of the safe to someone else, or a call to another app: refused.
        Assert.Throws<SafeBuildException>(() => SafeProposalBuilder.CheckLiquidity([.. entries, SafePayloadEntry.Pay(S("carol"), 1)], App, e, apps));
        Assert.Throws<SafeBuildException>(() => SafeProposalBuilder.CheckLiquidity([.. entries, new SafePayloadEntry(SafeTxType.AppCall, AppId: 999)], App, e, apps));
        // The provider is the backend's own table, never the builder's: on mainnet the testnet provider call is refused.
        Assert.Throws<SafeBuildException>(() => SafeProposalBuilder.CheckLiquidity(entries, App, e, BiatecDexApps.For("mainnet")!));
        // The pool call must be addLiquidity naming the config and identity apps and the pool's assets and LP token.
        var poolCall = entries.FindIndex(x => x.AppId == U("pool"));
        var args = entries[poolCall].AppArgs!.ToList();
        Assert.Equal(BiatecDexApps.AddLiquidity, args[0]);
        var swapped = entries.ToList();
        swapped[poolCall] = entries[poolCall] with { AppArgs = [[1, 2, 3, 4], .. args.Skip(1)] };
        Assert.Throws<SafeBuildException>(() => SafeProposalBuilder.CheckLiquidity(swapped, App, e, apps));
        swapped[poolCall] = entries[poolCall] with { AppArgs = [.. args.Take(3), args[4], args[3], args[5]] };
        Assert.Throws<SafeBuildException>(() => SafeProposalBuilder.CheckLiquidity(swapped, App, e, apps));
        // Two deposits calls into the pool: refused.
        Assert.Throws<SafeBuildException>(() => SafeProposalBuilder.CheckLiquidity([.. entries, entries[poolCall]], App, e, apps));
        // A deposit closing the safe's asset holding to the pool: refused.
        entries[2] = entries[2] with { CloseTo = pool };
        Assert.Throws<SafeBuildException>(() => SafeProposalBuilder.CheckLiquidity(entries, App, e, apps));
    }

    [Fact]
    public async Task SafeProposals_approval_is_bound_to_the_reviewed_payload_version_in_the_bytes()
    {
        var b = Builder("approve", out _);
        var build = await b.BuildApproveAsync("testnet", S("bob"), App, 1, 3);
        Assert.Equal(3UL, build.ExpectedPayloadVersion);
        Assert.Equal(3UL, build.View!.PayloadVersion);
        Assert.Equal(1UL, build.View.ProposalId);

        // The bytes carry version 3: an approval of version 2 (or of another proposal) is refused.
        await Assert.ThrowsAsync<SafeBuildException>(() => b.BuildApproveAsync("testnet", S("bob"), App, 1, 2));
        await Assert.ThrowsAsync<SafeBuildException>(() => b.BuildApproveAsync("testnet", S("bob"), App, 2, 3));
        // Bytes that approve version 4 while the description says 3: refused.
        var shifted = Edited("approve", args: a => [a[0], a[1], [0, 0, 0, 0, 0, 0, 0, 4], a[3]]);
        await Assert.ThrowsAsync<SafeBuildException>(() => new SafeProposalBuilder(new OneShot((_, _) => shifted)).BuildApproveAsync("testnet", S("bob"), App, 1, 3));

        var exec = await Builder("execute", out _).BuildExecuteAsync("testnet", S("carol"), App, 2, []);
        Assert.Equal("safe.execute", exec.Kind);
        Assert.Equal(10_000UL, exec.FeeMicroAlgo);
        await Assert.ThrowsAsync<SafeBuildException>(() => Builder("execute", out _).BuildExecuteAsync("testnet", S("carol"), App, 3, []));
        var t = ProposalFixture.Tx(Fx.GetProperty("execute"));
        t.Fee = SafeProposalBuilder.MaxExecuteFeeMicroAlgo + 1;
        var node = JsonNode.Parse(Fx.GetProperty("execute").GetRawText())!;
        node["transactions"] = new JsonArray(Convert.ToBase64String(AlgoEncoder.EncodeToMsgPackOrdered(t)));
        var expensive = ProposalFixture.Result(JsonDocument.Parse(node.ToJsonString()).RootElement);
        await Assert.ThrowsAsync<SafeBuildException>(() => new SafeProposalBuilder(new OneShot((_, _) => expensive)).BuildExecuteAsync("testnet", S("carol"), App, 2, []));
    }
}
