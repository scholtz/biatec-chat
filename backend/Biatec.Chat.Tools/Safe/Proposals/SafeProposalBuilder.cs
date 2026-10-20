using System.Globalization;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using Biatec.Chat.Tools.Algod;
using Biatec.Chat.Tools.Builders;
using Biatec.Chat.Tools.Safe.Create;

namespace Biatec.Chat.Tools.Safe.Proposals;

/// <summary>
/// A proposal as algo-safe reads it from the safe's boxes, through the sidecar. <see cref="Payload"/> is decoded chain
/// content (receivers, notes, app arguments written by whoever proposed): untrusted data, never instructions.
/// <see cref="PayloadHash"/> is the SHA-256 of the stored payload encoding, comparable with <see cref="EncodedProposal.PayloadHash"/>.
/// </summary>
public sealed record SafeProposalView(
    ulong ProposalId,
    string GroupId,
    string Kind,
    string Status,
    int Approvals,
    int Threshold,
    string Proposer,
    ulong ExpiryRound,
    ulong PayloadVersion,
    string? PayloadHash,
    bool? ApprovedByMember,
    JsonElement Payload);

/// <summary>
/// The Biatec DEX applications per network, held by the backend itself (the values <c>biatec-concentrated-liquidity-amm</c>
/// 0.9.44 resolves with <c>getConfig</c> from the genesis id), so a liquidity payload is checked against them and never
/// against app ids the builder reports.
/// </summary>
public sealed record BiatecDexApps(ulong ConfigAppId, ulong IdentityAppId, ulong PoolProviderAppId)
{
    /// <summary>addLiquidity(uint64,uint64,txn,txn,uint64,uint64,uint64)uint64 of the Biatec CLAMM pool.</summary>
    public static readonly byte[] AddLiquidity = Convert.FromHexString("0440fa8f");
    /// <summary>noop(uint64)void of the Biatec pool provider (the call clammAddLiquidityTxs puts before the deposit).</summary>
    public static readonly byte[] ProviderNoop = Convert.FromHexString("1b8ca919");

    public static BiatecDexApps? For(string network) => network switch
    {
        "mainnet" => new BiatecDexApps(3074197827, 3074197744, 3074197785),
        "testnet" => new BiatecDexApps(741107917, 741107914, 741107916),
        _ => null,
    };
}

/// <summary>The Biatec DEX add-liquidity payload a safe proposal must hold, from the pool's own registration.</summary>
public sealed record LiquidityExpectation(ulong PoolAppId, ulong AssetA, ulong AssetB, ulong LpTokenId, ulong AmountA, ulong AmountB);

/// <summary>
/// An unsigned, single-call proposal step (propose, approve or execute) as the sidecar built it and this side checked it.
/// <see cref="Proposal"/> is set for a propose call (decoded from its bytes); <see cref="View"/> for approve and execute
/// (the proposal as read from the chain).
/// </summary>
public sealed record SafeProposalBuild(
    string Kind,
    string Sender,
    ulong AppId,
    GroupedTransactions Group,
    ulong FirstValid,
    ulong LastValid,
    ulong FeeMicroAlgo,
    SafeSimulation? Simulation,
    EncodedProposal? Proposal = null,
    SafeProposalView? View = null,
    ulong? ExpectedPayloadVersion = null,
    ulong? PoolProviderAppId = null)
{
    public string SafeAddress => Algorand.Address.ForApplication(AppId).EncodeAsString();
}

/// <summary>Proposal, approval and execution calls, all built by <c>algo-safe</c> in the builder sidecar and checked here byte by byte.</summary>
public interface ISafeProposalBuilder
{
    /// <summary>A transaction-group proposal whose payload must be exactly <paramref name="payload"/>.</summary>
    Task<SafeProposalBuild> BuildProposeAsync(string network, string sender, ulong appId, ulong groupId, IReadOnlyList<SafePayloadEntry> payload, CancellationToken ct = default);

    /// <summary>A Biatec DEX add-liquidity proposal for the safe, built by <c>biatec-concentrated-liquidity-amm</c>.</summary>
    Task<SafeProposalBuild> BuildProposeLiquidityAsync(string network, string sender, ulong appId, ulong groupId, LiquidityExpectation expected, CancellationToken ct = default);

    /// <summary>An administrative proposal that must encode exactly <paramref name="expected"/>; <paramref name="change"/> is the sidecar request.</summary>
    Task<SafeProposalBuild> BuildProposeAdminAsync(string network, string sender, ulong appId, ulong groupId, SafeAdminChange expected, object change, CancellationToken ct = default);

    /// <summary>The approveProposal call, bound to <paramref name="expectedPayloadVersion"/> (the version the member reviewed).</summary>
    Task<SafeProposalBuild> BuildApproveAsync(string network, string sender, ulong appId, ulong proposalId, ulong expectedPayloadVersion, CancellationToken ct = default);

    /// <summary>
    /// The executeProposal call of a proposal that reached its threshold. <paramref name="poolAppIds"/> are the payload's
    /// calls to registered Biatec DEX pools, the only app calls given an extra fee for their own inner transactions.
    /// </summary>
    Task<SafeProposalBuild> BuildExecuteAsync(string network, string sender, ulong appId, ulong proposalId, IReadOnlyList<ulong> poolAppIds, CancellationToken ct = default);

    /// <summary>The newest proposals of a safe (with whether <paramref name="member"/> approved each), payload hash included.</summary>
    Task<IReadOnlyList<SafeProposalView>> ProposalsAsync(string network, ulong appId, string? member, CancellationToken ct = default);
}

public sealed class SafeProposalBuilder(IBuildersClient builders) : ISafeProposalBuilder
{
    public const string ProposeBuilder = "proposal.create";
    public const string ApproveBuilder = "proposal.approve";
    public const string ExecuteBuilder = "proposal.execute";
    public const string ProposalsBuilder = "safe.proposals";

    /// <summary>Fee ceiling for a propose or approve call (a single app call with no inner transactions).</summary>
    public const ulong MaxCallFeeMicroAlgo = 10_000;
    /// <summary>Fee ceiling for an execution (it pays the inner transactions of up to 16 payload entries and their opcode budget).</summary>
    public const ulong MaxExecuteFeeMicroAlgo = 250_000;

    public async Task<SafeProposalBuild> BuildProposeAsync(string network, string sender, ulong appId, ulong groupId, IReadOnlyList<SafePayloadEntry> payload, CancellationToken ct = default)
    {
        var request = new
        {
            appId = Id(appId), sender, groupId = Id(groupId), simulate = true,
            payload = payload.Select(SidecarEntry).ToArray(),
        };
        var (result, tx, preview) = await CallAsync(ProposeBuilder, network, sender, appId, "safe.propose", request, MaxCallFeeMicroAlgo, ct);
        var p = SafeProposalAbi.Propose(tx);
        CheckPropose(p, groupId, preview);
        if (p.Entries is null || p.Entries.Count != payload.Count || !p.Entries.Zip(payload).All(x => x.First.SameAs(x.Second)))
            throw new SafeBuildException("The proposal encodes a different payload than was requested.");
        return Finish(result, tx, sender, appId, "safe.propose", proposal: p);
    }

    public async Task<SafeProposalBuild> BuildProposeLiquidityAsync(string network, string sender, ulong appId, ulong groupId, LiquidityExpectation e, CancellationToken ct = default)
    {
        var request = new
        {
            appId = Id(appId), sender, groupId = Id(groupId), simulate = true,
            liquidity = new { poolAppId = Id(e.PoolAppId), amountA = Id(e.AmountA), amountB = Id(e.AmountB) },
        };
        var (result, tx, preview) = await CallAsync(ProposeBuilder, network, sender, appId, "safe.propose", request, MaxCallFeeMicroAlgo, ct);
        var p = SafeProposalAbi.Propose(tx);
        CheckPropose(p, groupId, preview);
        // The Biatec app ids come from the backend's own table, never from the builder's facts.
        var apps = BiatecDexApps.For(network) ?? throw new SafeBuildException("No Biatec DEX deployment is known for this network.");
        CheckLiquidity(p.Entries ?? throw new SafeBuildException("The liquidity proposal holds no transaction group."), appId, e, apps);
        return Finish(result, tx, sender, appId, "safe.propose", proposal: p, provider: apps.PoolProviderAppId);
    }

    public async Task<SafeProposalBuild> BuildProposeAdminAsync(string network, string sender, ulong appId, ulong groupId, SafeAdminChange expected, object change, CancellationToken ct = default)
    {
        var request = new { appId = Id(appId), sender, groupId = Id(groupId), simulate = true, change };
        var (result, tx, preview) = await CallAsync(ProposeBuilder, network, sender, appId, "safe.propose", request, MaxCallFeeMicroAlgo, ct);
        var p = SafeProposalAbi.Propose(tx);
        CheckPropose(p, groupId, preview);
        if (p.Change != expected) throw new SafeBuildException("The proposal encodes a different administrative change than was requested.");
        return Finish(result, tx, sender, appId, "safe.propose", proposal: p);
    }

    public async Task<SafeProposalBuild> BuildApproveAsync(string network, string sender, ulong appId, ulong proposalId, ulong expectedPayloadVersion, CancellationToken ct = default)
    {
        var request = new { appId = Id(appId), sender, proposalId = Id(proposalId), expectedPayloadVersion = Id(expectedPayloadVersion), simulate = true };
        var (result, tx, preview) = await CallAsync(ApproveBuilder, network, sender, appId, "safe.approve", request, MaxCallFeeMicroAlgo, ct);
        // The bytes decide: the call approves this proposal at the version the member reviewed.
        var (id, version) = SafeProposalAbi.Approve(tx);
        if (id != proposalId) throw new SafeBuildException("The approval names a different proposal.");
        if (version != expectedPayloadVersion) throw new SafeBuildException("The approval is bound to a different payload version.");
        var view = View(preview, proposalId);
        if (view.PayloadVersion != expectedPayloadVersion) throw new SafeBuildException("The proposal read back has a different payload version.");
        return Finish(result, tx, sender, appId, "safe.approve", view: view, version: version);
    }

    public async Task<SafeProposalBuild> BuildExecuteAsync(string network, string sender, ulong appId, ulong proposalId, IReadOnlyList<ulong> poolAppIds, CancellationToken ct = default)
    {
        var request = new { appId = Id(appId), sender, proposalId = Id(proposalId), simulate = true, poolAppIds = poolAppIds.Select(Id).ToArray() };
        var (result, tx, preview) = await CallAsync(ExecuteBuilder, network, sender, appId, "safe.execute", request, MaxExecuteFeeMicroAlgo, ct);
        var (id, _) = SafeProposalAbi.Execute(tx);
        if (id != proposalId) throw new SafeBuildException("The execution names a different proposal.");
        return Finish(result, tx, sender, appId, "safe.execute", view: View(preview, proposalId));
    }

    public async Task<IReadOnlyList<SafeProposalView>> ProposalsAsync(string network, ulong appId, string? member, CancellationToken ct = default)
    {
        var p = new Dictionary<string, object?> { ["appId"] = Id(appId), ["limit"] = 25 };
        if (member is not null) p["member"] = member;
        var data = await builders.ReadAsync(ProposalsBuilder, network, p, ct);
        try
        {
            if (U64(data, "appId") != appId) throw new SafeBuildException("The proposal listing is for a different safe.");
            return data.TryGetProperty("proposals", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(ParseView).ToList()
                : [];
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new SafeBuildException("The proposal listing has an unexpected shape.");
        }
    }

    // ---- checks ----

    private async Task<(BuildResult Result, Transaction Tx, JsonElement Preview)> CallAsync(string builder, string network, string sender, ulong appId, string kind,
        object request, ulong maxFee, CancellationToken ct)
    {
        var result = await builders.BuildAsync(builder, network, request, ct);
        if (result.Preview is not { ValueKind: JsonValueKind.Object } preview)
            throw new SafeBuildException("The builder returned no preview facts.");
        if (SafeBuilder.Str(preview, "kind") != kind)
            throw new SafeBuildException("The builder returned a different kind of call.");
        if (SafeBuilder.Str(preview, "appId") != Id(appId))
            throw new SafeBuildException("The builder facts name a different safe.");

        GroupedTransactions group;
        try { group = AlgorandGroup.Ensure(AlgorandGroup.Decode(result.Transactions)); }
        catch (Exception e) when (e is GroupException or ArgumentException or InvalidOperationException or FormatException)
        {
            throw new SafeBuildException("The builder returned a group that could not be read.");
        }
        // One app call from the member to the safe; any funding the safe needs is a separate wallet transfer.
        if (group.Transactions.Count != 1) throw new SafeBuildException("The builder returned more than the single safe call.");
        var tx = group.Transactions[0];
        if (tx is not ApplicationNoopTransaction || AppId(tx) != appId)
            throw new SafeBuildException("The built call is not a plain call to the safe.");
        if (!string.Equals(tx.Sender.EncodeAsString(), sender, StringComparison.Ordinal))
            throw new SafeBuildException("The built call is from a different sender.");
        if (tx.RekeyTo is { } r && r.Bytes.Any(b => b != 0))
            throw new SafeBuildException("The built call rekeys the sender.");
        if (tx.Fee > maxFee) throw new SafeBuildException("The built call pays an unexpectedly high fee.");
        return (result, tx, preview);
    }

    private static void CheckPropose(EncodedProposal p, ulong groupId, JsonElement preview)
    {
        if (p.GroupId != groupId) throw new SafeBuildException("The proposal is for a different signer group.");
        // Execute-on-propose would skip the other members' approvals for a 1-of-N group; chat always proposes only.
        if (p.Execute) throw new SafeBuildException("The proposal would execute immediately.");
        if (SafeBuilder.Str(preview, "valueMicroAlgo") != "0") throw new SafeBuildException("The builder values the proposal above zero.");
        if (SafeBuilder.Str(preview, "payloadHash") is { } h && !string.Equals(h, p.PayloadHash, StringComparison.Ordinal))
            throw new SafeBuildException("The builder describes a different payload than the call carries.");
    }

    /// <summary>
    /// The payload must be exactly a Biatec DEX deposit by the safe into the requested pool: amountA of asset A and
    /// amountB of asset B paid to the pool's account, an optional zero-amount LP-token opt-in of the safe to itself, at
    /// most one pool-provider <c>noop</c>, and exactly one <c>addLiquidity</c> call to the pool whose arguments name the
    /// network's Biatec config and identity apps and the pool's assets and LP token. Nothing else, no closes, no other sender.
    /// </summary>
    public static void CheckLiquidity(IReadOnlyList<SafePayloadEntry> entries, ulong safeAppId, LiquidityExpectation e, BiatecDexApps apps)
    {
        var safe = Algorand.Address.ForApplication(safeAppId).EncodeAsString();
        var pool = Algorand.Address.ForApplication(e.PoolAppId).EncodeAsString();
        bool paidA = false, paidB = false, optIn = false;
        int poolCalls = 0, providerCalls = 0;
        foreach (var x in entries)
        {
            if (x.Sender is not null || x.CloseTo is not null) throw new SafeBuildException("The liquidity payload spends from another account or closes one.");
            switch (x.TxType)
            {
                case SafeTxType.Payment or SafeTxType.AssetTransfer:
                {
                    var asset = x.TxType == SafeTxType.Payment ? 0UL : x.AssetId;
                    if (x.TxType == SafeTxType.AssetTransfer && asset == e.LpTokenId && x.Amount == 0 && x.Receiver == safe && !optIn) { optIn = true; continue; }
                    if (x.Receiver != pool) throw new SafeBuildException("The liquidity payload pays someone other than the pool.");
                    if (!paidA && asset == e.AssetA && x.Amount == e.AmountA) paidA = true;
                    else if (!paidB && asset == e.AssetB && x.Amount == e.AmountB) paidB = true;
                    else throw new SafeBuildException("The liquidity payload deposits different amounts than were requested.");
                    break;
                }
                case SafeTxType.AppCall:
                {
                    if (x.OnCompletion != 0) throw new SafeBuildException("The liquidity payload makes a call that is not a plain call.");
                    var args = x.AppArgs ?? [];
                    if (x.AppId == e.PoolAppId)
                    {
                        // addLiquidity(config, identity, txn, txn, assetA, assetB, assetLp): transaction arguments are not app args.
                        if (args.Count != 6 || !args[0].AsSpan().SequenceEqual(BiatecDexApps.AddLiquidity)
                            || U64Arg(args[1]) != apps.ConfigAppId || U64Arg(args[2]) != apps.IdentityAppId
                            || U64Arg(args[3]) != e.AssetA || U64Arg(args[4]) != e.AssetB || U64Arg(args[5]) != e.LpTokenId)
                            throw new SafeBuildException("The pool call is not an addLiquidity call for the requested pool and assets.");
                        poolCalls++;
                    }
                    else if (x.AppId == apps.PoolProviderAppId)
                    {
                        if (args.Count != 2 || !args[0].AsSpan().SequenceEqual(BiatecDexApps.ProviderNoop) || args[1].Length != 8)
                            throw new SafeBuildException("The pool-provider call is not the provider's noop.");
                        providerCalls++;
                    }
                    else throw new SafeBuildException("The liquidity payload calls an application other than the pool.");
                    break;
                }
                default:
                    throw new SafeBuildException("The liquidity payload holds an unexpected transaction.");
            }
        }
        if (!paidA || !paidB || poolCalls != 1 || providerCalls > 1) throw new SafeBuildException("The liquidity payload is not exactly one deposit into the pool.");
        // The LP-token opt-in, when present, must come first so the safe can receive the LP tokens.
        if (optIn && !(entries[0].TxType == SafeTxType.AssetTransfer && entries[0].AssetId == e.LpTokenId))
            throw new SafeBuildException("The LP-token opt-in is not the first transaction of the payload.");
    }

    private static ulong U64Arg(byte[] b) =>
        b.Length == 8 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b) : throw new SafeBuildException("An app call argument is not a uint64.");

    private static SafeProposalView View(JsonElement preview, ulong proposalId)
    {
        try
        {
            if (!preview.TryGetProperty("proposal", out var v) || v.ValueKind != JsonValueKind.Object) throw new KeyNotFoundException("proposal");
            var view = ParseView(v);
            if (view.ProposalId != proposalId) throw new SafeBuildException("The builder read back a different proposal.");
            return view;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new SafeBuildException("The builder's proposal facts are malformed.");
        }
    }

    private static SafeProposalView ParseView(JsonElement x) => new(
        U64(x, "proposalId"), SafeBuilder.Str(x, "groupId") ?? "", SafeBuilder.Str(x, "kind") ?? "", SafeBuilder.Str(x, "status") ?? "",
        checked((int)U64(x, "approvals")), checked((int)U64(x, "threshold")), SafeBuilder.Str(x, "proposer") ?? "",
        U64(x, "expiryRound"), U64(x, "payloadVersion"), SafeBuilder.Str(x, "payloadHash"),
        x.TryGetProperty("approvedByMember", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False ? a.GetBoolean() : null,
        x.TryGetProperty("payload", out var pl) ? pl.Clone() : default);

    private static SafeProposalBuild Finish(BuildResult result, Transaction tx, string sender, ulong appId, string kind,
        EncodedProposal? proposal = null, SafeProposalView? view = null, ulong? version = null, ulong? provider = null)
    {
        var group = AlgorandGroup.Ensure([tx]);
        var (first, last) = group.ValidityWindow();
        SafeSimulation? sim = null;
        if (result.Simulation is { ValueKind: JsonValueKind.Object } s)
            sim = new SafeSimulation(s.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True, SafeBuilder.Str(s, "failureMessage"));
        return new SafeProposalBuild(kind, sender, appId, group, first, last, tx.Fee, sim, proposal, view, version, provider);
    }

    /// <summary>A payload entry as the sidecar's <c>proposal.create</c> takes it.</summary>
    private static object SidecarEntry(SafePayloadEntry e) => e.TxType switch
    {
        SafeTxType.Payment => new { type = "payment", receiver = e.Receiver, amount = Id(e.Amount) },
        SafeTxType.AssetTransfer => new { type = "assetTransfer", assetId = Id(e.AssetId), receiver = e.Receiver, amount = Id(e.Amount) },
        _ => throw new ArgumentException("Chat proposes payments and asset transfers directly; app calls only through a builder."),
    };

    private static string Id(ulong v) => v.ToString(CultureInfo.InvariantCulture);

    // The SDK declares ApplicationId on the concrete call types, not on the abstract base.
    private static ulong? AppId(Transaction tx) => tx.GetType().GetProperty("ApplicationId")?.GetValue(tx) as ulong?;

    private static ulong U64(JsonElement e, string name) =>
        ulong.Parse(SafeBuilder.Str(e, name) ?? throw new KeyNotFoundException(name), NumberStyles.None, CultureInfo.InvariantCulture);
}
