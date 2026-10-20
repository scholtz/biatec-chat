using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Biatec.Chat.Core.Canonical;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Dex.Liquidity;
using Biatec.Chat.Tools.Mcp;
using Biatec.Chat.Tools.Safe.Create;
using Biatec.Chat.Tools.Wallet.Transfer;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Tools.Safe.Proposals;

public enum SafeProposalState
{
    /// <summary>Nothing was built; <see cref="SafeProposalOutcome.Message"/> says why.</summary>
    Error,
    /// <summary>A detail is missing (a clarifying question, not an error).</summary>
    NeedsDetails,
    /// <summary>No pool was named: the pair's pools open to the safe are listed for the user to choose from.</summary>
    ChoosePool,
    /// <summary>The safe's account needs ALGO for the proposal's storage, sent with a separate wallet.transfer.</summary>
    NeedsFunding,
    /// <summary>The proposal's payload changed since the member reviewed it; nothing was built, the new payload is shown.</summary>
    PayloadChanged,
    /// <summary>The proposal cannot be approved or executed now (already approved, not ready, executed, expired).</summary>
    NotActionable,
    /// <summary>The call waits for the user's Confirm on the card.</summary>
    AwaitingConfirmation,
    /// <summary>The call is being signed or is waiting to land.</summary>
    Busy,
    /// <summary>The call has landed.</summary>
    Landed,
}

/// <summary><see cref="Data"/> carries the facts for the tool result (proposal id, pool listing, the guided sequence's next steps).</summary>
public sealed record SafeProposalOutcome(SafeProposalState State, string Message, PreviewSnapshot? Snapshot = null, JsonObject? Data = null)
{
    public static SafeProposalOutcome Error(string message) => new(SafeProposalState.Error, message);
}

public sealed class SafeProposalOptions
{
    public const string Section = "SafeProposals";

    /// <summary>ALGO (microAlgo) suggested for a top-up when the safe's account cannot pay a proposal's box storage.</summary>
    public ulong ProposalFundingMicroAlgo { get; set; } = 500_000;
}

/// <summary>Reads what an account holds of one asset. Implemented over algod in the API.</summary>
public interface ISafeHoldingsReader
{
    /// <summary>Base units of <paramref name="assetId"/> held (0 when not opted in), or null when it cannot be read.</summary>
    Task<ulong?> AssetAmountAsync(string network, string address, ulong assetId, CancellationToken ct = default);

    /// <summary>Decimals and unit name of an asset, or null when it cannot be read. The unit name is the asset creator's text.</summary>
    Task<SafeAssetInfo?> AssetInfoAsync(string network, ulong assetId, CancellationToken ct = default);
}

/// <summary>How an asset's base units are shown: its decimals and (untrusted, creator-chosen) unit name.</summary>
public sealed record SafeAssetInfo(int Decimals, string? UnitName);

/// <summary>
/// <c>safe.propose</c>, <c>safe.approve</c> and <c>safe.execute</c>. Each is one signing call through the shared confirm
/// path (sign at Biatec OIDC, broadcast, track; a step-up challenge becomes a phone approval there): this never signs.
/// Every call is built by <c>algo-safe</c> in the sidecar and checked byte by byte before it is previewed. Funding the
/// safe (minimum balance, proposal boxes, asset opt-ins it pays for) is always a separate <c>wallet.transfer</c>.
/// </summary>
public sealed partial class SafeProposalService(
    IBiatecMcpClient mcp,
    ISafeProposalBuilder builder,
    ISafeBuilder safes,
    ILiquidityBuilder pools,
    ISafeHoldingsReader holdings,
    ActionPreviewService previews,
    ChatDbContext db,
    TimeProvider clock,
    SafeProposalOptions? options = null)
{
    public const string Propose = "safe.propose";
    public const string Approve = "safe.approve";
    public const string Execute = "safe.execute";
    public const int MaxOptIns = 16;

    private readonly SafeProposalOptions _options = options ?? new SafeProposalOptions();

    // ---- safe.propose ----

    public async Task<SafeProposalOutcome> ProposeAsync(McpCallContext ctx, Guid conversationId, SafeProposeRequest r, CancellationToken ct)
    {
        if (await OwnAddressProblemAsync(ctx, r.From, ct) is { } problem) return SafeProposalOutcome.Error(problem);
        var safeAddress = SafeCreateService.SafeAddress(r.SafeAppId);
        var key = r.ToJson();

        try
        {
            // Only an open preview of the same request is reused; an identical later proposal is a new proposal.
            var existing = await FindAsync(conversationId, Propose, key, ct);
            if (existing is not null && await ExistingAsync(conversationId, existing, ct) is { } reuse) return reuse;
            var landedBefore = await LandedBeforeAsync(conversationId, Propose, key, ct);

            var listing = await safes.ListAsync(r.Network, r.From, [r.SafeAppId], ct);
            var safe = listing.Safes.FirstOrDefault(s => s.AppId == r.SafeAppId);
            if (safe is null)
                return SafeProposalOutcome.Error(listing.Unreadable.Contains(r.SafeAppId)
                    ? $"Safe {r.SafeAppId} could not be read. Tell the user to try again later."
                    : $"{r.From} is not a member of safe {r.SafeAppId}. Ask the user which of their addresses is a member, or check the app id with safe.list.");

            // The payload first, so the proposing group is chosen for every action the payload uses.
            List<SafePayloadEntry>? entries = null;
            LiquidityExpectation? expected = null;
            PoolInfo? pool = null;
            SafeAdminChange? adminChange = null;
            object? sidecarChange = null;
            List<string> sequence = [];
            var needs = new HashSet<string>(StringComparer.Ordinal);
            switch (r.Kind)
            {
                case SafeProposalKind.Payment or SafeProposalKind.AssetTransfer or SafeProposalKind.OptIn:
                    entries = Entries(r, safeAddress, out var invalidEntries);
                    if (invalidEntries is not null) return SafeProposalOutcome.Error(invalidEntries);
                    foreach (var e in entries) needs.Add(e.TxType == SafeTxType.Payment ? "payment" : "assetTransfer");
                    break;
                case SafeProposalKind.AddLiquidity:
                {
                    SafeProposalOutcome? refusal;
                    (expected, pool, refusal) = await LiquidityAsync(r, safeAddress, ct);
                    if (refusal is not null) return refusal;
                    // Calls to the pool, asset deposits and the LP-token opt-in; an ALGO side is a payment.
                    needs.UnionWith(["appCall", "assetTransfer"]);
                    if (pool!.AssetA == 0 || pool.AssetB == 0) needs.Add("payment");
                    break;
                }
                default:
                {
                    string? invalidAdmin;
                    (adminChange, sidecarChange, sequence, invalidAdmin) = Admin(r.Admin);
                    if (invalidAdmin is not null) return SafeProposalOutcome.Error(invalidAdmin);
                    break;
                }
            }

            // The proposing signer group: the one named, else the first of the user's groups allowed every action the payload uses.
            var isAdmin = r.Kind == SafeProposalKind.Admin;
            var group = safe.Groups.FirstOrDefault(g => (r.GroupId is null || g.Id == r.GroupId.Value.ToString(CultureInfo.InvariantCulture))
                && (isAdmin ? g.IsAdminGroup : needs.All(n => g.AllowedActions.Contains(n))));
            if (group is null || !ulong.TryParse(group.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var groupId))
                return SafeProposalOutcome.Error(isAdmin
                    ? $"None of {r.From}'s signer groups in safe {r.SafeAppId} may propose administrative changes."
                    : $"None of {r.From}'s signer groups in safe {r.SafeAppId} may propose this kind of transaction (it needs: {string.Join(", ", needs.Order())}).");

            SafeProposalBuild build;
            JsonObject extra = new();
            string summary;
            switch (r.Kind)
            {
                case SafeProposalKind.Payment or SafeProposalKind.AssetTransfer or SafeProposalKind.OptIn:
                    build = await builder.BuildProposeAsync(r.Network, r.From, r.SafeAppId, groupId, entries!, ct);
                    summary = r.Kind == SafeProposalKind.OptIn ? $"opt the safe in to asset(s) {string.Join(", ", r.OptInAssetIds!)}" : Summary(build.Proposal!.Entries!, safeAddress);
                    break;
                case SafeProposalKind.AddLiquidity:
                    build = await builder.BuildProposeLiquidityAsync(r.Network, r.From, r.SafeAppId, groupId, expected!, ct);
                    summary = $"add {expected!.AmountA} base units of asset {pool!.AssetA} and {expected.AmountB} base units of asset {pool.AssetB} as liquidity to Biatec DEX pool {pool.AppId} " +
                              $"(fee {pool.FeePercent} %, price range {pool.PriceRange})";
                    extra["pool"] = new JsonObject
                    {
                        ["poolAppId"] = Id(pool.AppId), ["assetA"] = Id(pool.AssetA), ["assetB"] = Id(pool.AssetB), ["lpTokenId"] = Id(pool.LpTokenId),
                        ["feePercent"] = pool.FeePercent, ["priceRangeOfAInB"] = pool.PriceRange, ["amountA"] = Id(expected.AmountA), ["amountB"] = Id(expected.AmountB),
                    };
                    break;
                default:
                    build = await builder.BuildProposeAdminAsync(r.Network, r.From, r.SafeAppId, groupId, adminChange!, sidecarChange!, ct);
                    summary = AdminSummary(adminChange!);
                    if (sequence.Count > 0) extra["nextInSequence"] = new JsonArray(sequence.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
                    break;
            }

            if (build.Simulation is { Ok: false } sim)
            {
                if (IsBalanceFailure(sim.FailureMessage))
                    return new SafeProposalOutcome(SafeProposalState.NeedsFunding,
                        $"The safe account ({safeAddress}) does not hold enough ALGO to store this proposal. Tell the user, then call wallet.transfer from {r.From} to {safeAddress} for " +
                        $"{Id(_options.ProposalFundingMicroAlgo)} base units of ALGO ({SafeCreateService.FormatAlgo(_options.ProposalFundingMicroAlgo)} ALGO, assetId 0). After it has landed, call safe.propose again with the same arguments.",
                        Data: new JsonObject { ["safeAddress"] = safeAddress, ["fundingMicroAlgo"] = Id(_options.ProposalFundingMicroAlgo) });
                return SafeProposalOutcome.Error($"The proposal would fail on chain, so nothing was previewed. Simulation reported: '{Sanitize(sim.FailureMessage)}'. Tell the user it cannot be prepared right now.");
            }

            var p = build.Proposal!;
            var payloadJson = p.Change is null ? DescribeEntries(p.Entries!, safeAddress) : new JsonArray(DescribeAdmin(p.Change));
            // A group with threshold 1: the proposer's own approval (recorded by the propose call) is the only one, and the
            // execution is valued at zero, so the propose call is where the spend is authorized. It must ask the phone.
            var soleApproval = group.Threshold <= 1 && MovesValue(payloadJson);
            var warnings = Warnings(payloadJson);
            if (soleApproval)
                warnings.Insert(0, "This signer group needs only 1 approval: proposing alone authorizes the safe to send this payload, so Biatec OIDC is asked for your phone approval now.");
            var canonical = new JsonObject
            {
                ["capability"] = Propose,
                ["intent"] = $"Propose on Biatec Safe {r.SafeAppId}: {summary}",
                ["network"] = r.StepNetwork,
                ["sender"] = r.From,
                ["request"] = key,
                ["safeAppId"] = Id(r.SafeAppId),
                ["safeAddress"] = safeAddress,
                ["groupId"] = Id(groupId),
                ["groupName"] = Sanitize(group.Name),
                ["threshold"] = group.Threshold,
                ["kind"] = p.Change is null ? "transactionGroup" : "admin",
                ["payload"] = payloadJson,
                ["payloadValue"] = PayloadValue(payloadJson),
                ["payloadHash"] = p.PayloadHash,
                // A new proposal starts at payload version 1; the contract assigns it when the call lands.
                ["payloadVersion"] = "1",
                ["expiryRound"] = Id(p.ExpiryRound),
                ["proposerApproves"] = true,
                ["valueMicroAlgo"] = "0",
            };
            if (warnings.Count > 0) canonical["warnings"] = new JsonArray(warnings.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray());
            if (soleApproval) canonical[RequireConsentField] = true;
            foreach (var (k, v) in extra) canonical[k] = v?.DeepClone();
            await AssetInfoAsync(r.Network, payloadJson, canonical, ct);
            var snap = await PersistAsync(conversationId, Propose, canonical, build, r.From, r.StepNetwork, existing, predictedNeedsConsent: soleApproval, ct);
            var data = new JsonObject
            {
                ["safeAddress"] = safeAddress, ["groupId"] = Id(groupId), ["threshold"] = group.Threshold, ["payloadVersion"] = "1",
            };
            if (warnings.Count > 0) data["warnings"] = canonical["warnings"]!.DeepClone();
            foreach (var (k, v) in extra) data[k] = v?.DeepClone();
            return new SafeProposalOutcome(SafeProposalState.AwaitingConfirmation,
                $"The proposal ({summary}) is ready for the user's confirmation. Proposing moves nothing; the contract records the proposer's own approval with it " +
                $"(1 of {group.Threshold}), so there is no separate approval by {r.From}. " +
                (group.Threshold > 1
                    ? "After it lands, the other signers approve it with safe.approve from their own sessions (safe.list shows its proposal id), then anyone calls safe.execute. "
                    : "This group needs only 1 approval, so proposing authorizes the spend and the user's phone is asked for it. After it lands it is ready: call safe.execute (safe.list shows its proposal id). ") +
                "Any funding the payload needs (ALGO for opt-ins, the assets it moves) is a separate wallet.transfer to the safe." +
                (landedBefore ? " Note: an identical proposal already landed in this conversation; this builds a second one, so make sure the user wants that." : ""),
                snap, data);
        }
        catch (PreviewException e) { return SafeProposalOutcome.Error(e.Message); }
    }

    private static List<SafePayloadEntry> Entries(SafeProposeRequest r, string safeAddress, out string? invalid)
    {
        invalid = null;
        switch (r.Kind)
        {
            case SafeProposalKind.Payment:
                if (r.Receiver is null || r.Amount == 0) { invalid = "A payment proposal needs a receiver and an amount above zero."; return []; }
                return [SafePayloadEntry.Pay(r.Receiver, r.Amount)];
            case SafeProposalKind.AssetTransfer:
                if (r.Receiver is null || r.Amount == 0 || r.AssetId == 0) { invalid = "An asset transfer proposal needs a receiver, an asset id and an amount above zero."; return []; }
                return [SafePayloadEntry.Axfer(r.AssetId, r.Receiver, r.Amount)];
            default:
                var ids = (r.OptInAssetIds ?? []).Distinct().ToList();
                if (ids.Count == 0 || ids.Count > MaxOptIns || ids.Contains(0UL)) { invalid = $"An opt-in proposal needs 1 to {MaxOptIns} asset ids (not ALGO)."; return []; }
                // Zero-amount transfers of the safe to itself, built by algo-safe.
                return ids.Select(a => SafePayloadEntry.Axfer(a, safeAddress, 0)).ToList();
        }
    }

    private async Task<(LiquidityExpectation?, PoolInfo?, SafeProposalOutcome?)> LiquidityAsync(SafeProposeRequest r, string safeAddress, CancellationToken ct)
    {
        // The account that interacts with the pool is the safe's: pools whose verification class it does not meet are not offered.
        if (r.PoolAppId == 0)
        {
            if (r.AssetAId is not { } a || r.AssetBId is not { } b)
                return (null, null, new SafeProposalOutcome(SafeProposalState.NeedsDetails,
                    "No pool was named. Ask the user which two assets the pool is for, then call safe.propose again with poolAppId 0, assetAId and assetBId. Do not guess a pool."));
            var listing = await pools.ListPoolsAsync(r.Network, a, b, null, safeAddress, ct);
            return (null, null, new SafeProposalOutcome(SafeProposalState.ChoosePool,
                listing.Pools.Count == 0
                    ? "No pool of this pair is open to the safe. Say so; pools under notAvailableToTheSafe need a verified account."
                    : "Nothing has been built. Ask the user which pool the safe should deposit into, listing each pool's id, fee, price range and liquidity; do not choose for them, even if only one is listed. " +
                      "Then call safe.propose again with that poolAppId (amountA/amountB in base units of that pool's assetA/assetB, or omit them to deposit everything the safe holds).",
                Data: PoolsJson(listing)));
        }
        var one = await pools.ListPoolsAsync(r.Network, null, null, r.PoolAppId, safeAddress, ct);
        if (one.Pools.Count != 1)
            return (null, null, SafeProposalOutcome.Error(one.Excluded.Count > 0
                ? $"Pool {r.PoolAppId} needs Biatec verification class {one.Excluded[0].RequiredVerificationClass}; the safe's account has class {one.AccountVerificationClass?.ToString(CultureInfo.InvariantCulture) ?? "none"}. Explain this and offer other pools."
                : $"{r.PoolAppId} is not a Biatec DEX pool on {r.Network}. Ask the user which pool they mean."));
        var pool = one.Pools[0];
        var amountA = r.AmountA ?? await AllAsync(r.Network, safeAddress, pool.AssetA, ct);
        var amountB = r.AmountB ?? await AllAsync(r.Network, safeAddress, pool.AssetB, ct);
        if (amountA is null || amountB is null)
            return (null, null, SafeProposalOutcome.Error("The safe's holdings could not be read (or one asset is ALGO, whose amount must be given). Ask the user for the amounts in base units."));
        if (amountA == 0 && amountB == 0)
            return (null, null, SafeProposalOutcome.Error($"The safe holds none of the pool's assets ({pool.AssetA}, {pool.AssetB}). Fund it first with wallet.transfer (after an opt-in proposal for the assets)."));
        return (new LiquidityExpectation(pool.AppId, pool.AssetA, pool.AssetB, pool.LpTokenId, amountA.Value, amountB.Value), pool, null);
    }

    private async Task<ulong?> AllAsync(string network, string address, ulong assetId, CancellationToken ct) =>
        assetId == 0 ? null : await holdings.AssetAmountAsync(network, address, assetId, ct);

    private static JsonObject PoolsJson(PoolListing listing)
    {
        var o = new JsonObject
        {
            ["pools"] = new JsonArray(listing.Pools.Select(p => (JsonNode?)new JsonObject
            {
                ["poolAppId"] = Id(p.AppId), ["assetA"] = Id(p.AssetA), ["assetB"] = Id(p.AssetB), ["feePercent"] = p.FeePercent,
                ["priceRangeOfAInB"] = p.PriceRange, ["currentPriceOfAInB"] = LiquidityFormat.Scaled(p.CurrentPrice), ["liquidity"] = p.Liquidity,
                ["minimumVerificationClass"] = p.VerificationClass,
            }).ToArray()),
        };
        if (listing.Excluded.Count > 0)
            o["notAvailableToTheSafe"] = new JsonArray(listing.Excluded.Select(x => (JsonNode?)new JsonObject
            {
                ["poolAppId"] = Id(x.AppId), ["requiresVerificationClass"] = x.RequiredVerificationClass,
            }).ToArray());
        return o;
    }

    /// <summary>
    /// The change the propose call must encode, the sidecar's request for it and, for a new signer group, the rest of
    /// the guided sequence (algo-safe creates a group with one member; the others and the threshold are further proposals).
    /// </summary>
    internal static (SafeAdminChange?, object?, List<string> Sequence, string? Invalid) Admin(SafeAdminRequest? a)
    {
        if (a is null || !SafeAdminRequest.Types.Contains(a.Type)) return (null, null, [], "An administrative proposal needs change.type: addMember, removeMember, changeThreshold or createGroup.");
        var g = a.TargetGroupId;
        switch (a.Type)
        {
            case "addMember" or "removeMember":
                if (g == 0 || a.Member is null) return (null, null, [], $"{a.Type} needs the signer group id and the member's address.");
                return (new SafeAdminChange(a.Type == "addMember" ? SafeAdminChange.AddMember : SafeAdminChange.RemoveMember, g, Member: a.Member),
                    new { type = a.Type, groupId = Id(g), member = a.Member }, [], null);
            case "changeThreshold":
                if (g == 0 || a.Threshold == 0) return (null, null, [], "changeThreshold needs the signer group id and the new threshold.");
                return (new SafeAdminChange(SafeAdminChange.ChangeThreshold, g, Threshold: a.Threshold),
                    new { type = "changeThreshold", groupId = Id(g), threshold = Id(a.Threshold) }, [], null);
            default:
            {
                var members = (a.Members ?? []).Distinct(StringComparer.Ordinal).ToList();
                if (string.IsNullOrWhiteSpace(a.Name) || members.Count == 0) return (null, null, [], "createGroup needs a group name and at least one member address.");
                var threshold = a.Threshold == 0 ? 1 : a.Threshold;
                if (threshold > (ulong)members.Count) return (null, null, [], "The new group's threshold cannot exceed its number of members.");
                var sequence = members.Skip(1).Select(m => $"After this proposal executes, propose addMember {m} to the new group (its id is shown by safe.list).").ToList();
                if (threshold > 1) sequence.Add($"Then propose changeThreshold to {threshold} for the new group.");
                return (new SafeAdminChange(SafeAdminChange.CreateGroup, GroupName: a.Name!, Member: members[0], Threshold: 1, AllowedActions: SafeBuilder.InitialAllowedActions),
                    new { type = "createGroup", name = a.Name, threshold = 1, allowedActions = SafeBuilder.InitialAllowedActions, members = new[] { new { address = members[0] } } },
                    sequence, null);
            }
        }
    }

    // ---- safe.approve ----

    public async Task<SafeProposalOutcome> ApproveAsync(McpCallContext ctx, Guid conversationId, SafeProposalActionRequest r, CancellationToken ct)
    {
        if (await OwnAddressProblemAsync(ctx, r.From, ct) is { } problem) return SafeProposalOutcome.Error(problem);
        try
        {
            var live = (await builder.ProposalsAsync(r.Network, r.SafeAppId, r.From, ct)).FirstOrDefault(p => p.ProposalId == r.ProposalId);
            if (live is null && r.ExpectedPayloadVersion is null)
                return SafeProposalOutcome.Error($"Proposal {r.ProposalId} is not among the safe's recent proposals. Check it with safe.list and pass its payloadVersion as expectedPayloadVersion.");
            if (live is not null)
            {
                if (r.ExpectedPayloadVersion is { } reviewed && reviewed != live.PayloadVersion)
                    return new SafeProposalOutcome(SafeProposalState.PayloadChanged,
                        $"The payload of proposal {r.ProposalId} changed since it was reviewed (reviewed version {reviewed}, now version {live.PayloadVersion}). Nothing was built: an approval of the old " +
                        "version would be refused. Show the user the current payload below and ask whether they approve it; if so, call safe.approve again with the new expectedPayloadVersion.",
                        Data: ViewJson(live));
                if (live.Status != "active")
                    return new SafeProposalOutcome(SafeProposalState.NotActionable, live.Status == "ready"
                        ? $"Proposal {r.ProposalId} already has its {live.Threshold} approvals. It can be executed with safe.execute."
                        : $"Proposal {r.ProposalId} is {Sanitize(live.Status)} and can no longer be approved.", Data: ViewJson(live));
                if (live.ApprovedByMember == true)
                    return new SafeProposalOutcome(SafeProposalState.NotActionable,
                        $"{r.From} has already approved proposal {r.ProposalId} (a proposer's approval is recorded when proposing). It has {live.Approvals} of {live.Threshold} approvals.", Data: ViewJson(live));
                if (RekeyRefusal(SanitizedPayload(live.Payload), "approved") is { } rekey) return rekey with { Data = ViewJson(live) };
            }
            var version = r.ExpectedPayloadVersion ?? live!.PayloadVersion;
            var key = new JsonObject { ["network"] = r.Network, ["from"] = r.From, ["safeAppId"] = Id(r.SafeAppId), ["proposalId"] = Id(r.ProposalId), ["expectedPayloadVersion"] = Id(version) };
            var existing = await FindAsync(conversationId, Approve, key, ct);
            if (existing is not null && await ExistingAsync(conversationId, existing, ct) is { } reuse) return reuse;

            var build = await builder.BuildApproveAsync(r.Network, r.From, r.SafeAppId, r.ProposalId, version, ct);
            if (build.Simulation is { Ok: false } sim)
                return SafeProposalOutcome.Error($"The approval would fail on chain, so nothing was previewed. Simulation reported: '{Sanitize(sim.FailureMessage)}'.");
            var view = build.View!;
            var payload = SanitizedPayload(view.Payload);
            if (RekeyRefusal(payload, "approved") is { } rekeyed) return rekeyed with { Data = ViewJson(view) };
            var summary = PayloadSummary(payload);
            var canonical = ActionCanonical(Approve, r, build, view, payload,
                $"Approve proposal {r.ProposalId} on Biatec Safe {r.SafeAppId} (payload version {version}): {summary}", key);
            canonical["expectedPayloadVersion"] = Id(version);
            canonical["approvalsAfter"] = view.Approvals + 1;
            AddWarnings(canonical, payload);
            await AssetInfoAsync(r.Network, payload, canonical, ct);
            // The approval is the consent point: Biatec OIDC values it at the payload it authorizes. Only a payload that
            // provably moves nothing (an opt-in, an admin change) is predicted below threshold; anything else asks.
            var snap = await PersistAsync(conversationId, Approve, canonical, build, r.From, r.StepNetwork, existing, predictedNeedsConsent: MovesValue(payload), ct);
            var after = view.Approvals + 1;
            return new SafeProposalOutcome(SafeProposalState.AwaitingConfirmation,
                $"The approval of proposal {r.ProposalId} (payload version {version}) is ready for the user's confirmation. It is bound to that version: if the payload changes before it lands, " +
                $"the contract refuses it. After it lands the proposal has {after} of {view.Threshold} approvals" + (after >= view.Threshold ? "; anyone may then call safe.execute." : "."),
                snap, ViewJson(view));
        }
        catch (PreviewException e) { return SafeProposalOutcome.Error(e.Message); }
    }

    // ---- safe.execute ----

    public async Task<SafeProposalOutcome> ExecuteAsync(McpCallContext ctx, Guid conversationId, SafeProposalActionRequest r, CancellationToken ct)
    {
        if (await OwnAddressProblemAsync(ctx, r.From, ct) is { } problem) return SafeProposalOutcome.Error(problem);
        try
        {
            // The chain first: an executed (or not yet ready) proposal is reported as such, whatever this conversation previewed.
            var live = (await builder.ProposalsAsync(r.Network, r.SafeAppId, r.From, ct)).FirstOrDefault(p => p.ProposalId == r.ProposalId);
            if (live is not null && live.Status != "ready")
                return new SafeProposalOutcome(SafeProposalState.NotActionable, live.Status switch
                {
                    "active" => $"Proposal {r.ProposalId} has {live.Approvals} of {live.Threshold} approvals; it can be executed once the threshold is met.",
                    "executed" => $"Proposal {r.ProposalId} has already been executed; there is nothing left to execute.",
                    _ => $"Proposal {r.ProposalId} is {Sanitize(live.Status)}; there is nothing to execute.",
                }, Data: ViewJson(live));
            if (live is not null && RekeyRefusal(SanitizedPayload(live.Payload), "executed") is { } rekey) return rekey with { Data = ViewJson(live) };

            var key = new JsonObject { ["network"] = r.Network, ["from"] = r.From, ["safeAppId"] = Id(r.SafeAppId), ["proposalId"] = Id(r.ProposalId) };
            var existing = await FindAsync(conversationId, Execute, key, ct);
            if (existing is not null && await ExistingAsync(conversationId, existing, ct) is { } reuse) return reuse;

            // Calls to registered Biatec DEX pools send inner transactions of their own; only those get the extra fee.
            var poolApps = live is null ? [] : await PoolCallsAsync(r.Network, SanitizedPayload(live.Payload), ct);
            var build = await builder.BuildExecuteAsync(r.Network, r.From, r.SafeAppId, r.ProposalId, poolApps, ct);
            if (build.Simulation is { Ok: false } sim)
                return SafeProposalOutcome.Error($"The execution would fail on chain, so nothing was previewed. Simulation reported: '{Sanitize(sim.FailureMessage)}'. " +
                    "The safe may lack the assets or ALGO the payload needs; that funding is a separate wallet.transfer to the safe.");
            var view = build.View!;
            var payload = SanitizedPayload(view.Payload);
            if (RekeyRefusal(payload, "executed") is { } rekeyed) return rekeyed with { Data = ViewJson(view) };
            var canonical = ActionCanonical(Execute, r, build, view, payload,
                $"Execute proposal {r.ProposalId} on Biatec Safe {r.SafeAppId} (payload version {view.PayloadVersion}): {PayloadSummary(payload)}", key);
            canonical["feeMicroAlgo"] = Id(build.FeeMicroAlgo);
            AddWarnings(canonical, payload);
            await AssetInfoAsync(r.Network, payload, canonical, ct);
            // Valued at zero: every approver consented to this exact payload. Non-value triggers can still ask the phone;
            // the confirm path handles that step-up like any other.
            var snap = await PersistAsync(conversationId, Execute, canonical, build, r.From, r.StepNetwork, existing, predictedNeedsConsent: false, ct);
            return new SafeProposalOutcome(SafeProposalState.AwaitingConfirmation,
                $"The execution of proposal {r.ProposalId} is ready for the user's confirmation. The safe sends the payload as one atomic group; the executing address pays the fee " +
                $"({SafeCreateService.FormatAlgo(build.FeeMicroAlgo)} ALGO). It is valued at zero for the user's limits, but Biatec OIDC may still ask for phone approval.",
                snap, ViewJson(view));
        }
        catch (PreviewException e) { return SafeProposalOutcome.Error(e.Message); }
    }

    // ---- previews ----

    private async Task<ActionPreview?> FindAsync(Guid conversationId, string capability, JsonObject key, CancellationToken ct)
    {
        // Open only: awaiting confirmation, being signed or submitted, or expired (rebuilt). A landed, failed or
        // cancelled preview is never reused, so an identical later request is a new action.
        var candidates = await db.ActionPreviews.AsNoTracking()
            .Where(p => p.ConversationId == conversationId && p.Capability == capability && p.RebuiltAsPreviewId == null
                        && (p.Status == ActionPreviewStatus.Proposed || p.Status == ActionPreviewStatus.Confirmed
                            || p.Status == ActionPreviewStatus.InProgress || p.Status == ActionPreviewStatus.Expired))
            .ToListAsync(ct);
        return candidates.Where(p => KeyOf(p.CanonicalJson) is { } k && JsonNode.DeepEquals(k, key)).OrderByDescending(p => p.ExpiresAt).FirstOrDefault();
    }

    private async Task<bool> LandedBeforeAsync(Guid conversationId, string capability, JsonObject key, CancellationToken ct)
    {
        var landed = await db.ActionPreviews.AsNoTracking()
            .Where(p => p.ConversationId == conversationId && p.Capability == capability && p.Status == ActionPreviewStatus.Completed)
            .Select(p => p.CanonicalJson).ToListAsync(ct);
        return landed.Any(c => KeyOf(c) is { } k && JsonNode.DeepEquals(k, key));
    }

    /// <summary>What an earlier open preview of the same request means now: in flight, still confirmable, or null (expired: build again).</summary>
    private async Task<SafeProposalOutcome?> ExistingAsync(Guid conversationId, ActionPreview preview, CancellationToken ct)
    {
        var snap = await previews.GetAsync(conversationId, preview.Id, null, ct);
        if (snap.Preview.Status is ActionPreviewStatus.Confirmed or ActionPreviewStatus.InProgress
            || snap.Steps.Any(s => s.Status is StepStatus.Submitted or StepStatus.AwaitingConsent or StepStatus.Signed))
            return new SafeProposalOutcome(SafeProposalState.Busy, "This is being signed or is waiting to land. Wait for it before asking again.", snap);
        if (snap.Preview.Status == ActionPreviewStatus.Proposed && snap.Countdown.CanConfirm)
            return new SafeProposalOutcome(SafeProposalState.AwaitingConfirmation, "The preview is waiting for the user's confirmation.", snap);
        return null;
    }

    private async Task<PreviewSnapshot> PersistAsync(Guid conversationId, string capability, JsonObject canonical, SafeProposalBuild build, string sender, string network,
        ActionPreview? expired, bool predictedNeedsConsent, CancellationToken ct)
    {
        canonical["groupHash"] = build.Group.GroupHash;
        canonical["txIds"] = new JsonArray(build.Group.TxIds.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        canonical["firstValid"] = Id(build.FirstValid);
        canonical["lastValid"] = Id(build.LastValid);
        canonical["simulation"] = build.Simulation is { Ok: true } ? "Simulation passed" : build.Simulation is null ? null : "Simulation failed";
        var json = JsonCanonicalizer.Canonicalize(canonical);
        var spec = new NewStepSpec(ActionStepKind.Sign, build.Group.GroupHash, PreviewPolicy.ExpiryFromLastValid(build.LastValid, build.FirstValid, clock.GetUtcNow()),
            UnsignedGroup: JsonSerializer.Serialize(build.Group.ToBase64()), SenderAddress: sender, Network: network,
            FirstValid: (long)build.FirstValid, LastValid: (long)build.LastValid);
        return expired is not null && expired.Status is ActionPreviewStatus.Proposed or ActionPreviewStatus.Expired
            ? await previews.RebuildAsync(conversationId, expired.Id, json, [spec], predictedValueEur: capability == Approve ? null : 0m, predictedNeedsConsent, ct)
            : await previews.CreateAsync(new NewPreviewSpec(conversationId, capability, json, [spec], PredictedValueEur: capability == Approve ? null : 0m, predictedNeedsConsent), ct);
    }

    private static JsonObject ActionCanonical(string capability, SafeProposalActionRequest r, SafeProposalBuild build, SafeProposalView view, JsonArray payload, string intent, JsonObject key) => new()
    {
        ["capability"] = capability,
        ["intent"] = intent,
        ["network"] = r.StepNetwork,
        ["sender"] = r.From,
        ["request"] = key,
        ["safeAppId"] = Id(r.SafeAppId),
        ["safeAddress"] = build.SafeAddress,
        ["proposalId"] = Id(view.ProposalId),
        ["proposalKind"] = Sanitize(view.Kind),
        ["groupId"] = Sanitize(view.GroupId),
        ["proposer"] = Sanitize(view.Proposer),
        ["approvals"] = view.Approvals,
        ["threshold"] = view.Threshold,
        ["payloadVersion"] = Id(view.PayloadVersion),
        ["payloadHash"] = view.PayloadHash is null ? null : Sanitize(view.PayloadHash),
        ["expiryRound"] = Id(view.ExpiryRound),
        ["payload"] = payload,
        ["valueMicroAlgo"] = capability == Execute ? "0" : null,
    };

    private static JsonObject? KeyOf(string canonicalJson)
    {
        try { return JsonNode.Parse(canonicalJson)?["request"] as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? Field(string canonicalJson, string name)
    {
        try { return JsonNode.Parse(canonicalJson)?[name]?.GetValue<string>(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    // ---- describing payloads ----

    /// <summary>The payload decoded from the propose call's own bytes, for the card and the evidence record.</summary>
    internal static JsonArray DescribeEntries(IReadOnlyList<SafePayloadEntry> entries, string safeAddress) =>
        new(entries.Select(e => (JsonNode?)(e.TxType switch
        {
            SafeTxType.Payment => new JsonObject { ["type"] = "payment", ["from"] = "safe", ["receiver"] = e.Receiver, ["assetId"] = "0", ["amount"] = Id(e.Amount), ["closesAccountTo"] = e.CloseTo, ["note"] = Sanitize(e.Note, 64) },
            SafeTxType.AssetTransfer => new JsonObject
            {
                ["type"] = e.Amount == 0 && e.Receiver == safeAddress ? "optIn" : "assetTransfer", ["from"] = "safe", ["receiver"] = e.Receiver,
                ["assetId"] = Id(e.AssetId), ["amount"] = Id(e.Amount), ["closesAssetTo"] = e.CloseTo, ["note"] = Sanitize(e.Note, 64),
            },
            _ => new JsonObject { ["type"] = "appCall", ["appId"] = Id(e.AppId), ["onCompletion"] = Id(e.OnCompletion), ["appArgs"] = (e.AppArgs ?? []).Count },
        })).ToArray());

    public static JsonObject DescribeAdmin(SafeAdminChange c) => new()
    {
        ["type"] = c.TypeName, ["targetGroupId"] = Id(c.TargetGroupId), ["groupName"] = Sanitize(c.GroupName, 32), ["member"] = c.Member,
        ["threshold"] = Id(c.Threshold),
    };

    private static string AdminSummary(SafeAdminChange c) => c.ChangeType switch
    {
        SafeAdminChange.AddMember => $"add {c.Member} to signer group {c.TargetGroupId}",
        SafeAdminChange.RemoveMember => $"remove {c.Member} from signer group {c.TargetGroupId}",
        SafeAdminChange.ChangeThreshold => $"change the threshold of signer group {c.TargetGroupId} to {c.Threshold}",
        _ => $"create signer group \"{Sanitize(c.GroupName, 32)}\" with {c.Member}",
    };

    private static string Summary(IReadOnlyList<SafePayloadEntry> entries, string safeAddress) =>
        PayloadSummary(DescribeEntries(entries, safeAddress));

    /// <summary>
    /// The proposal payload as the sidecar read it from the chain, cut down to known fields with every string cleaned:
    /// receivers, notes and labels were written by whoever proposed and are data, never instructions.
    /// </summary>
    internal static JsonArray SanitizedPayload(JsonElement payload)
    {
        var o = new JsonArray();
        if (payload.ValueKind != JsonValueKind.Array) return o;
        // Every field that decides where value or control goes is kept: close-to, rekey target, asset-config addresses.
        string[] fields =
        [
            "type", "txType", "from", "receiver", "assetId", "amount", "closesAccountTo", "closesAssetTo", "appId", "onCompletion", "targetGroupId", "groupName",
            "member", "threshold", "rekeyTo", "configAsset", "total", "decimals", "unitName", "assetName", "manager", "reserve", "freeze", "clawback",
            "online", "voteFirst", "voteLast", "dailyLimit", "monthlyLimit", "adminPrivileges", "active",
        ];
        foreach (var e in payload.EnumerateArray().Take(SafeProposalAbi.MaxEntries))
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var x = new JsonObject();
            foreach (var f in fields)
                if (e.TryGetProperty(f, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    x[f] = Sanitize(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText(), 64);
            // A type the card does not know is still shown, as unknown.
            if (x["type"] is null) x["type"] = "unknown";
            if (e.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } note) x["note"] = Sanitize(note, 64);
            if (e.TryGetProperty("appArgs", out var args) && args.ValueKind == JsonValueKind.Array) x["appArgs"] = args.GetArrayLength();
            o.Add(x);
        }
        return o;
    }

    /// <summary>One line per payload entry, from the sanitized fields only.</summary>
    public static string PayloadSummary(JsonArray payload)
    {
        var parts = payload.OfType<JsonObject>().Select(e => (string?)e["type"] switch
        {
            "payment" => $"pay {Algo(e["amount"])} ALGO to {e["receiver"]}" + (e["closesAccountTo"] is { } c ? $" and CLOSE the safe's account to {c}" : ""),
            "optIn" => $"opt the safe in to asset {e["assetId"]}",
            "assetTransfer" when (string?)e["amount"] == "0" && e["closesAssetTo"] is null => $"opt-in transfer of asset {e["assetId"]} to {e["receiver"]}",
            "assetTransfer" => $"send {e["amount"]} base units of asset {e["assetId"]} to {e["receiver"]}" + (e["closesAssetTo"] is { } c ? $" and CLOSE the safe's holding to {c}" : ""),
            "rekey" => $"REKEY {e["from"]} to {e["rekeyTo"]}",
            "assetConfig" => $"configure asset {e["configAsset"]} (manager {e["manager"]}, reserve {e["reserve"]}, freeze {e["freeze"]}, clawback {e["clawback"]})",
            "keyReg" => $"register participation keys (online {e["online"]})",
            "appCall" => $"call application {e["appId"]}",
            "addMember" => $"add {e["member"]} to signer group {e["targetGroupId"]}",
            "removeMember" => $"remove {e["member"]} from signer group {e["targetGroupId"]}",
            "changeThreshold" => $"set the threshold of signer group {e["targetGroupId"]} to {e["threshold"]}",
            "createGroup" => $"create signer group \"{e["groupName"]}\" with {e["member"]}",
            var t => $"{t ?? "unknown"} transaction",
        }).ToList();
        if (parts.Count == 0) return "an empty payload";
        if (parts.Count > 6) parts = [.. parts.Take(6), $"and {parts.Count - 6} more"];
        return string.Join("; ", parts);
    }

    /// <summary>The admin changes the spec values at zero (they move nothing; their effect is governed by the same approvals).</summary>
    private static readonly HashSet<string> ZeroValuedAdmin = ["addMember", "removeMember", "changeThreshold", "createGroup", "setPolicy", "setPrivileges", "setActive", "setPaused"];

    /// <summary>
    /// Whether the payload can move value. Fail closed: only zero-amount transfers that close nothing and the
    /// zero-valued admin changes do not; app calls, rekeys, asset configuration, key registration and anything unknown do.
    /// </summary>
    public static bool MovesValue(JsonArray payload) =>
        payload.OfType<JsonObject>().Any(e => (string?)e["type"] switch
        {
            "payment" or "assetTransfer" or "optIn" => (string?)e["amount"] != "0" || e["closesAccountTo"] is not null || e["closesAssetTo"] is not null,
            { } t when ZeroValuedAdmin.Contains(t) => false,
            _ => true,
        });

    /// <summary>Plain-language warnings for what can hand over value or control beyond the listed amounts.</summary>
    public static List<string> Warnings(JsonArray payload)
    {
        var w = new List<string>();
        foreach (var e in payload.OfType<JsonObject>())
        {
            var type = (string?)e["type"];
            if (e["closesAccountTo"] is { } ca) w.Add($"Closes the safe's ALGO account to {ca}: everything left in it goes to that address.");
            if (e["closesAssetTo"] is { } cs) w.Add($"Closes the safe's holding of asset {e["assetId"]} to {cs}: all of it goes to that address.");
            switch (type)
            {
                case "rekey": w.Add($"Rekeys {e["from"]} to {e["rekeyTo"]}: control of that account passes to another key."); break;
                case "assetConfig": w.Add($"Reconfigures asset {e["configAsset"]}: manager {e["manager"]}, reserve {e["reserve"]}, freeze {e["freeze"]}, clawback {e["clawback"]}."); break;
                case "keyReg": w.Add("Registers participation keys for the safe's account."); break;
                case "payment" or "assetTransfer" or "optIn" or "appCall": break;
                case { } t when ZeroValuedAdmin.Contains(t): break;
                default: w.Add($"Contains a transaction chat cannot describe ({type ?? "unknown"}); treat it as able to move funds."); break;
            }
        }
        return w;
    }

    private static void AddWarnings(JsonObject canonical, JsonArray payload)
    {
        var w = Warnings(payload);
        if (w.Count > 0) canonical["warnings"] = new JsonArray(w.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
    }

    /// <summary>
    /// Rekeys hand control of an account to another key, which chat does not do (the rekey scope is excluded in version 1):
    /// a rekey proposal is neither approved nor executed here.
    /// </summary>
    private static SafeProposalOutcome? RekeyRefusal(JsonArray payload, string verb) =>
        payload.OfType<JsonObject>().Any(e => (string?)e["type"] == "rekey")
            ? new SafeProposalOutcome(SafeProposalState.NotActionable,
                $"This proposal rekeys an account (it hands control of it to another key). Rekeying is not available in chat, so it cannot be {verb} here. " +
                "Tell the user to review it in the safe's own interface; do not retry.")
            : null;

    /// <summary>What the payload moves, summed per asset (base units; asset 0 is ALGO), for the preview and the consent request.</summary>
    private static JsonArray PayloadValue(JsonArray payload)
    {
        var sums = new SortedDictionary<ulong, System.Numerics.BigInteger>();
        foreach (var e in payload.OfType<JsonObject>())
        {
            if ((string?)e["type"] is not ("payment" or "assetTransfer")) continue;
            if (!ulong.TryParse((string?)e["assetId"] ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out var asset)) continue;
            if (!ulong.TryParse((string?)e["amount"], NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount == 0) continue;
            sums[asset] = sums.GetValueOrDefault(asset) + amount;
        }
        return new JsonArray(sums.Select(kv => (JsonNode?)new JsonObject { ["assetId"] = Id(kv.Key), ["amount"] = kv.Value.ToString(CultureInfo.InvariantCulture) }).ToArray());
    }

    /// <summary>Decimals and unit name of every asset the payload moves, for the card (read from algod; unit names are cleaned).</summary>
    private async Task AssetInfoAsync(string network, JsonArray payload, JsonObject canonical, CancellationToken ct)
    {
        var info = new JsonObject();
        var ids = payload.OfType<JsonObject>().Where(e => (string?)e["type"] == "assetTransfer")
            .Select(e => ulong.TryParse((string?)e["assetId"], NumberStyles.None, CultureInfo.InvariantCulture, out var a) ? a : 0).Where(a => a > 0).Distinct().Take(SafeProposalAbi.MaxEntries);
        foreach (var id in ids)
            if (await holdings.AssetInfoAsync(network, id, ct) is { } a && a.Decimals is >= 0 and <= 19)
                info[Id(id)] = new JsonObject { ["decimals"] = a.Decimals, ["unitName"] = string.IsNullOrWhiteSpace(a.UnitName) ? null : Sanitize(a.UnitName, 8) };
        if (info.Count > 0) canonical["assetInfo"] = info;
    }

    /// <summary>The payload's app calls that go to a registered Biatec DEX pool (checked against the pool registry, not the payload).</summary>
    private async Task<IReadOnlyList<ulong>> PoolCallsAsync(string network, JsonArray payload, CancellationToken ct)
    {
        var found = new List<ulong>();
        var apps = payload.OfType<JsonObject>().Where(e => (string?)e["type"] == "appCall")
            .Select(e => ulong.TryParse((string?)e["appId"], NumberStyles.None, CultureInfo.InvariantCulture, out var a) ? a : 0).Where(a => a > 0).Distinct().Take(4);
        foreach (var app in apps)
        {
            try
            {
                var listing = await pools.ListPoolsAsync(network, null, null, app, null, ct);
                if (listing.Pools.Any(x => x.AppId == app)) found.Add(app);
            }
            catch (Exception e) when (e is Builders.BuilderException or HttpRequestException) { }
        }
        return found;
    }

    private static string Algo(JsonNode? micro) =>
        ulong.TryParse((string?)micro, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? SafeCreateService.FormatAlgo(v) : "?";

    internal static JsonObject ViewJson(SafeProposalView v) => new()
    {
        ["proposalId"] = Id(v.ProposalId),
        ["kind"] = Sanitize(v.Kind),
        ["proposalStatus"] = v.Status == "ready" ? "ready_to_execute" : v.Status == "active" ? "awaiting_approvals" : Sanitize(v.Status),
        ["progress"] = $"{v.Approvals} of {v.Threshold} approvals",
        ["approvedByYou"] = v.ApprovedByMember,
        ["proposer"] = Sanitize(v.Proposer),
        ["payloadVersion"] = Id(v.PayloadVersion),
        ["expiryRound"] = Id(v.ExpiryRound),
        ["payload"] = SanitizedPayload(v.Payload),
    };

    // ---- helpers ----

    private async Task<string?> OwnAddressProblemAsync(McpCallContext ctx, string address, CancellationToken ct)
    {
        var own = await mcp.ListAlgorandAddressesAsync(ctx, ct);
        if (own.IsError) return "The addresses could not be read. Tell the user to try again.";
        return McpWalletJson.Addresses(own.Json).Contains(address, StringComparer.Ordinal)
            ? null
            : "That address does not belong to the signed-in user. Use wallet.listAddresses to see the user's addresses.";
    }

    private static bool IsBalanceFailure(string? message) => message is not null && BalanceFailure().IsMatch(message);

    [GeneratedRegex("balance|overspend|insufficient", RegexOptions.IgnoreCase)]
    private static partial Regex BalanceFailure();

    /// <summary>Sidecar, algod and chain text is untrusted: cleaned by the shared <see cref="UntrustedText.Clean"/>, length bounded.</summary>
    internal static string Sanitize(string? text, int max = 200) => UntrustedText.Clean(text, max);

    private static string Id(ulong v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Canonical-JSON flag the confirm path reads to send <c>requireConsent</c> on the signing request.</summary>
    public const string RequireConsentField = "requireConsent";
}
