using System.Globalization;
using System.Text.Json.Nodes;

namespace Biatec.Chat.Tools.Safe.Proposals;

public enum SafeProposalKind
{
    /// <summary>ALGO from the safe to a receiver.</summary>
    Payment,
    /// <summary>An asset from the safe to a receiver.</summary>
    AssetTransfer,
    /// <summary>Zero-amount asset transfers of the safe to itself, one per asset, so it can hold them.</summary>
    OptIn,
    /// <summary>A Biatec DEX add-liquidity group sent by the safe.</summary>
    AddLiquidity,
    /// <summary>A governed change of the signer groups (add or remove a member, change a threshold, create a group).</summary>
    Admin,
}

/// <summary>An administrative change as the user asked for it. <see cref="Members"/> and <see cref="Name"/> are for createGroup.</summary>
public sealed record SafeAdminRequest(string Type, ulong TargetGroupId = 0, string? Member = null, ulong Threshold = 0, string? Name = null, IReadOnlyList<string>? Members = null)
{
    public static readonly string[] Types = ["addMember", "removeMember", "changeThreshold", "createGroup"];

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["type"] = Type };
        if (TargetGroupId != 0) o["groupId"] = TargetGroupId.ToString(CultureInfo.InvariantCulture);
        if (Member is not null) o["member"] = Member;
        if (Threshold != 0) o["threshold"] = Threshold.ToString(CultureInfo.InvariantCulture);
        if (Name is not null) o["name"] = Name;
        if (Members is not null) o["members"] = new JsonArray(Members.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray());
        return o;
    }
}

/// <summary>
/// What the user asked the safe to propose. <see cref="From"/> is one of the user's own addresses and a member of the
/// proposing signer group. Amounts are base units; a null <see cref="AmountA"/> or <see cref="AmountB"/> means
/// everything the safe holds of that asset.
/// </summary>
public sealed record SafeProposeRequest(
    string Network,
    string From,
    ulong SafeAppId,
    SafeProposalKind Kind,
    ulong? GroupId = null,
    string? Receiver = null,
    ulong AssetId = 0,
    ulong Amount = 0,
    IReadOnlyList<ulong>? OptInAssetIds = null,
    ulong PoolAppId = 0,
    ulong? AssetAId = null,
    ulong? AssetBId = null,
    ulong? AmountA = null,
    ulong? AmountB = null,
    SafeAdminRequest? Admin = null)
{
    public string StepNetwork => "algorand-" + Network;

    public string KindName => Kind switch
    {
        SafeProposalKind.Payment => "payment",
        SafeProposalKind.AssetTransfer => "assetTransfer",
        SafeProposalKind.OptIn => "optIn",
        SafeProposalKind.AddLiquidity => "addLiquidity",
        _ => "admin",
    };

    /// <summary>The request as stored in the preview's canonical JSON; two calls with equal JSON are the same request.</summary>
    public JsonObject ToJson()
    {
        static string S(ulong v) => v.ToString(CultureInfo.InvariantCulture);
        var o = new JsonObject { ["network"] = Network, ["from"] = From, ["safeAppId"] = S(SafeAppId), ["kind"] = KindName };
        if (GroupId is { } g) o["groupId"] = S(g);
        switch (Kind)
        {
            case SafeProposalKind.Payment:
                o["receiver"] = Receiver; o["amount"] = S(Amount);
                break;
            case SafeProposalKind.AssetTransfer:
                o["receiver"] = Receiver; o["assetId"] = S(AssetId); o["amount"] = S(Amount);
                break;
            case SafeProposalKind.OptIn:
                o["assetIds"] = new JsonArray((OptInAssetIds ?? []).Select(a => (JsonNode?)JsonValue.Create(S(a))).ToArray());
                break;
            case SafeProposalKind.AddLiquidity:
                o["poolAppId"] = S(PoolAppId);
                o["amountA"] = AmountA is { } a ? S(a) : "all";
                o["amountB"] = AmountB is { } b ? S(b) : "all";
                break;
            case SafeProposalKind.Admin:
                o["change"] = Admin?.ToJson();
                break;
        }
        return o;
    }
}

/// <summary>An approval or execution of proposal <see cref="ProposalId"/>, signed by <see cref="From"/> (one of the user's own addresses).</summary>
public sealed record SafeProposalActionRequest(string Network, string From, ulong SafeAppId, ulong ProposalId, ulong? ExpectedPayloadVersion = null)
{
    public string StepNetwork => "algorand-" + Network;
}
