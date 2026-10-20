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
}

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
            var existing = await FindAsync(conversationId, Propose, key, ct);
            if (existing is not null && await ExistingAsync(conversationId, existing, ct) is { } reuse)
                return reuse.State == SafeProposalState.Landed ? await ProposedAsync(r, existing, reuse, ct) : reuse;

            // The proposing signer group: the one named, else the first of the user's groups allowed to propose this.
            var listing = await safes.ListAsync(r.Network, r.From, [r.SafeAppId], ct);
            var safe = listing.Safes.FirstOrDefault(s => s.AppId == r.SafeAppId);
            if (safe is null)
                return SafeProposalOutcome.Error(listing.Unreadable.Contains(r.SafeAppId)
                    ? $"Safe {r.SafeAppId} could not be read. Tell the user to try again later."
                    : $"{r.From} is not a member of safe {r.SafeAppId}. Ask the user which of their addresses is a member, or check the app id with safe.list.");
            var need = r.Kind switch
            {
                SafeProposalKind.Payment => "payment",
                SafeProposalKind.AddLiquidity => "appCall",
                SafeProposalKind.Admin => null,
                _ => "assetTransfer",
            };
            var group = safe.Groups.FirstOrDefault(g => (r.GroupId is null || g.Id == r.GroupId.Value.ToString(CultureInfo.InvariantCulture))
                && (need is null ? g.IsAdminGroup : g.AllowedActions.Contains(need) && (r.Kind != SafeProposalKind.AddLiquidity || g.AllowedActions.Contains("assetTransfer"))));
            if (group is null || !ulong.TryParse(group.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var groupId))
                return SafeProposalOutcome.Error(need is null
                    ? $"None of {r.From}'s signer groups in safe {r.SafeAppId} may propose administrative changes."
                    : $"None of {r.From}'s signer groups in safe {r.SafeAppId} may propose this kind of transaction.");

            SafeProposalBuild build;
            JsonObject extra = new();
            string summary;
            switch (r.Kind)
            {
                case SafeProposalKind.Payment or SafeProposalKind.AssetTransfer or SafeProposalKind.OptIn:
                {
                    var entries = Entries(r, safeAddress, out var invalid);
                    if (invalid is not null) return SafeProposalOutcome.Error(invalid);
                    build = await builder.BuildProposeAsync(r.Network, r.From, r.SafeAppId, groupId, entries, ct);
                    summary = r.Kind == SafeProposalKind.OptIn ? $"opt the safe in to asset(s) {string.Join(", ", r.OptInAssetIds!)}" : Summary(build.Proposal!.Entries!, safeAddress);
                    break;
                }
                case SafeProposalKind.AddLiquidity:
                {
                    var (expected, pool, refusal) = await LiquidityAsync(r, safeAddress, ct);
                    if (refusal is not null) return refusal;
                    build = await builder.BuildProposeLiquidityAsync(r.Network, r.From, r.SafeAppId, groupId, expected!, ct);
                    summary = $"add {expected!.AmountA} base units of asset {pool!.AssetA} and {expected.AmountB} base units of asset {pool.AssetB} as liquidity to Biatec DEX pool {pool.AppId} " +
                              $"(fee {pool.FeePercent} %, price range {pool.PriceRange})";
                    extra["pool"] = new JsonObject
                    {
                        ["poolAppId"] = Id(pool.AppId), ["assetA"] = Id(pool.AssetA), ["assetB"] = Id(pool.AssetB), ["lpTokenId"] = Id(pool.LpTokenId),
                        ["feePercent"] = pool.FeePercent, ["priceRangeOfAInB"] = pool.PriceRange, ["amountA"] = Id(expected.AmountA), ["amountB"] = Id(expected.AmountB),
                    };
                    break;
                }
                default:
                {
                    var (expected, change, sequence, invalid) = Admin(r.Admin);
                    if (invalid is not null) return SafeProposalOutcome.Error(invalid);
                    build = await builder.BuildProposeAdminAsync(r.Network, r.From, r.SafeAppId, groupId, expected!, change!, ct);
                    summary = AdminSummary(expected!);
                    if (sequence.Count > 0) extra["nextInSequence"] = new JsonArray(sequence.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
                    break;
                }
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
                ["payload"] = p.Change is null ? DescribeEntries(p.Entries!, safeAddress) : new JsonArray(DescribeAdmin(p.Change)),
                ["payloadHash"] = p.PayloadHash,
                // A new proposal starts at payload version 1; the contract assigns it when the call lands.
                ["payloadVersion"] = "1",
                ["expiryRound"] = Id(p.ExpiryRound),
                ["proposerApproves"] = true,
                ["valueMicroAlgo"] = "0",
            };
            foreach (var (k, v) in extra) canonical[k] = v?.DeepClone();
            var snap = await PersistAsync(conversationId, Propose, canonical, build, r.From, r.StepNetwork, existing, predictedNeedsConsent: false, ct);
            var data = new JsonObject
            {
                ["safeAddress"] = safeAddress, ["groupId"] = Id(groupId), ["threshold"] = group.Threshold, ["payloadVersion"] = "1",
            };
            foreach (var (k, v) in extra) data[k] = v?.DeepClone();
            return new SafeProposalOutcome(SafeProposalState.AwaitingConfirmation,
                $"The proposal ({summary}) is ready for the user's confirmation. Proposing moves nothing; the contract records the proposer's own approval with it " +
                $"(1 of {group.Threshold}), so there is no separate approval by {r.From}. " +
                (group.Threshold > 1 ? "After it lands, the other signers approve it with safe.approve from their own sessions, then anyone calls safe.execute. " : "After it lands it is ready: call safe.execute. ") +
                "Any funding the payload needs (ALGO for opt-ins, the assets it moves) is a separate wallet.transfer to the safe.",
                snap, data);
        }
        catch (PreviewException e) { return SafeProposalOutcome.Error(e.Message); }
    }

    /// <summary>A landed proposal: its id, found on chain by its proposer and the hash of the payload it carries.</summary>
    private async Task<SafeProposalOutcome> ProposedAsync(SafeProposeRequest r, ActionPreview preview, SafeProposalOutcome landed, CancellationToken ct)
    {
        var hash = Field(preview.CanonicalJson, "payloadHash");
        var found = (await builder.ProposalsAsync(r.Network, r.SafeAppId, r.From, ct))
            .Where(p => p.PayloadHash == hash && p.Proposer == r.From).OrderByDescending(p => p.ProposalId).FirstOrDefault();
        var data = landed.Data ?? new JsonObject();
        if (found is null)
            return landed with { Message = "The proposal has landed, but it could not be found on the safe yet. Tell the user to check the pending proposals with safe.list in a moment.", Data = data };
        data["proposalId"] = Id(found.ProposalId);
        data["proposalStatus"] = found.Status == "ready" ? "ready_to_execute" : found.Status == "active" ? "awaiting_approvals" : Sanitize(found.Status);
        data["progress"] = $"{found.Approvals} of {found.Threshold} approvals";
        data["payloadVersion"] = Id(found.PayloadVersion);
        data["expiryRound"] = Id(found.ExpiryRound);
        return landed with
        {
            Message = $"The proposal is on the safe as proposal {found.ProposalId} (payload version {found.PayloadVersion}, {found.Approvals} of {found.Threshold} approvals). " +
                      (found.Status == "ready" ? "The threshold is met: anyone may now call safe.execute." : "The other signers approve it with safe.approve from their own sessions."),
            Data = data,
        };
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
            var summary = PayloadSummary(payload);
            var canonical = ActionCanonical(Approve, r, build, view, payload,
                $"Approve proposal {r.ProposalId} on Biatec Safe {r.SafeAppId} (payload version {version}): {summary}", key);
            canonical["expectedPayloadVersion"] = Id(version);
            canonical["approvalsAfter"] = view.Approvals + 1;
            // The approval is the consent point: Biatec OIDC values it at the payload it authorizes. A payload that moves
            // nothing (an opt-in) is valued at zero.
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
            var key = new JsonObject { ["network"] = r.Network, ["from"] = r.From, ["safeAppId"] = Id(r.SafeAppId), ["proposalId"] = Id(r.ProposalId) };
            var existing = await FindAsync(conversationId, Execute, key, ct);
            if (existing is not null && await ExistingAsync(conversationId, existing, ct) is { } reuse) return reuse;

            var live = (await builder.ProposalsAsync(r.Network, r.SafeAppId, r.From, ct)).FirstOrDefault(p => p.ProposalId == r.ProposalId);
            if (live is not null && live.Status != "ready")
                return new SafeProposalOutcome(SafeProposalState.NotActionable, live.Status == "active"
                    ? $"Proposal {r.ProposalId} has {live.Approvals} of {live.Threshold} approvals; it can be executed once the threshold is met."
                    : $"Proposal {r.ProposalId} is {Sanitize(live.Status)}; there is nothing to execute.", Data: ViewJson(live));

            var build = await builder.BuildExecuteAsync(r.Network, r.From, r.SafeAppId, r.ProposalId, ct);
            if (build.Simulation is { Ok: false } sim)
                return SafeProposalOutcome.Error($"The execution would fail on chain, so nothing was previewed. Simulation reported: '{Sanitize(sim.FailureMessage)}'. " +
                    "The safe may lack the assets or ALGO the payload needs; that funding is a separate wallet.transfer to the safe.");
            var view = build.View!;
            var payload = SanitizedPayload(view.Payload);
            var canonical = ActionCanonical(Execute, r, build, view, payload,
                $"Execute proposal {r.ProposalId} on Biatec Safe {r.SafeAppId} (payload version {view.PayloadVersion}): {PayloadSummary(payload)}", key);
            canonical["feeMicroAlgo"] = Id(build.FeeMicroAlgo);
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
        var candidates = await db.ActionPreviews.AsNoTracking()
            .Where(p => p.ConversationId == conversationId && p.Capability == capability && p.RebuiltAsPreviewId == null
                        && p.Status != ActionPreviewStatus.Failed && p.Status != ActionPreviewStatus.Cancelled)
            .ToListAsync(ct);
        return candidates.Where(p => KeyOf(p.CanonicalJson) is { } k && JsonNode.DeepEquals(k, key)).OrderByDescending(p => p.ExpiresAt).FirstOrDefault();
    }

    /// <summary>What an earlier preview of the same request means now: landed, in flight, still confirmable, or null (expired: build again).</summary>
    private async Task<SafeProposalOutcome?> ExistingAsync(Guid conversationId, ActionPreview preview, CancellationToken ct)
    {
        var snap = await previews.GetAsync(conversationId, preview.Id, null, ct);
        if (snap.Preview.Status == ActionPreviewStatus.Completed)
            return new SafeProposalOutcome(SafeProposalState.Landed, "This has already landed.", snap,
                new JsonObject { ["txIds"] = new JsonArray(snap.Steps.SelectMany(s => s.TxIds).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()) });
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
            SafeTxType.Payment => new JsonObject { ["type"] = "payment", ["from"] = "safe", ["receiver"] = e.Receiver, ["assetId"] = "0", ["amount"] = Id(e.Amount), ["note"] = Sanitize(e.Note, 64) },
            SafeTxType.AssetTransfer => new JsonObject
            {
                ["type"] = e.Amount == 0 && e.Receiver == safeAddress ? "optIn" : "assetTransfer", ["from"] = "safe", ["receiver"] = e.Receiver,
                ["assetId"] = Id(e.AssetId), ["amount"] = Id(e.Amount), ["note"] = Sanitize(e.Note, 64),
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
        string[] fields = ["type", "from", "receiver", "assetId", "amount", "closesAccountTo", "closesAssetTo", "appId", "onCompletion", "targetGroupId", "groupName", "member", "threshold"];
        foreach (var e in payload.EnumerateArray().Take(SafeProposalAbi.MaxEntries))
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var x = new JsonObject();
            foreach (var f in fields)
                if (e.TryGetProperty(f, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    x[f] = Sanitize(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText(), 64);
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
            "payment" => $"pay {Algo(e["amount"])} ALGO to {e["receiver"]}",
            "optIn" => $"opt the safe in to asset {e["assetId"]}",
            "assetTransfer" when (string?)e["amount"] == "0" => $"opt-in transfer of asset {e["assetId"]} to {e["receiver"]}",
            "assetTransfer" => $"send {e["amount"]} base units of asset {e["assetId"]} to {e["receiver"]}",
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

    /// <summary>Whether the payload can move value (anything but zero-amount opt-ins and admin changes).</summary>
    private static bool MovesValue(JsonArray payload) =>
        payload.OfType<JsonObject>().Any(e => (string?)e["type"] switch
        {
            "payment" or "assetTransfer" => (string?)e["amount"] != "0" || e["closesAccountTo"] is not null || e["closesAssetTo"] is not null,
            "appCall" => true,
            _ => false,
        });

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

    /// <summary>Sidecar, algod and chain text is untrusted: control characters and quotes removed, length bounded.</summary>
    internal static string Sanitize(string? text, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var clean = new string(text.Select(c => char.IsControl(c) || c is '\'' or '"' or '`' ? ' ' : c).ToArray()).Trim();
        return clean.Length <= max ? clean : clean[..max] + "...";
    }

    private static string Id(ulong v) => v.ToString(CultureInfo.InvariantCulture);
}
