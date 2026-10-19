using System.Globalization;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using Biatec.Chat.Tools.Algod;
using Biatec.Chat.Tools.Builders;

namespace Biatec.Chat.Tools.Safe.Create;

/// <summary>Outcome of the sidecar's algod simulate, when the build asked for one.</summary>
public sealed record SafeSimulation(bool Ok, string? FailureMessage);

/// <summary>An unsigned, grouped safe-creation step (deploy or bootstrap) as the sidecar built it and this side checked it.</summary>
public sealed record SafeBuild(
    string Step,
    string Sender,
    GroupedTransactions Group,
    ulong FirstValid,
    ulong LastValid,
    SafeSimulation? Simulation,
    ulong? ValidatorAppId);

/// <summary>A signer group of a safe the member belongs to.</summary>
public sealed record SafeGroupSummary(string Id, string Name, int Threshold, int MemberCount, IReadOnlyList<string> AllowedActions, bool IsAdminGroup, long? MembershipEpoch);

/// <summary>A safe the member belongs to (at least one active signer group).</summary>
public sealed record SafeSummary(ulong AppId, string Address, string Name, bool Paused, IReadOnlyList<SafeGroupSummary> Groups);

/// <summary>One proposal of a safe with its approval progress. <see cref="Payload"/> is the sidecar's decoded payload (untrusted data).</summary>
public sealed record SafeProposalSummary(
    ulong ProposalId,
    string GroupId,
    string Kind,
    string Status,
    int Approvals,
    int Threshold,
    string Proposer,
    ulong ExpiryRound,
    ulong PayloadVersion,
    bool? ApprovedByMember,
    JsonElement Payload);

public sealed record SafeProposalPage(ulong AppId, string SafeAddress, IReadOnlyList<SafeProposalSummary> Proposals, bool Truncated);

/// <summary>The group or data the sidecar returned is not what was asked for. Nothing is previewed.</summary>
public sealed class SafeBuildException(string message) : Exception(message);

/// <summary>
/// Safe creation and safe reads, all through the <c>algo-safe</c> library in the builder sidecar. The backend never
/// assembles the groups: it only decodes and checks what the sidecar returned.
/// </summary>
public interface ISafeBuilder
{
    /// <summary>The single application-create call that deploys the safe. Valued at zero; it moves no funds.</summary>
    Task<SafeBuild> BuildDeployAsync(string network, string sender, string name, CancellationToken ct = default);

    /// <summary>
    /// Bootstrap and finalize of a deployed, not yet bootstrapped safe: one signer group with
    /// <paramref name="members"/> and <paramref name="threshold"/>. Built with simulation; the safe's account must be
    /// funded (a separate transfer) for box storage first.
    /// </summary>
    Task<SafeBuild> BuildBootstrapAsync(string network, string sender, ulong appId, string groupName, IReadOnlyList<string> members, int threshold, CancellationToken ct = default);

    /// <summary>The safes among <paramref name="appIds"/> that <paramref name="member"/> belongs to.</summary>
    Task<IReadOnlyList<SafeSummary>> ListAsync(string network, string member, IReadOnlyList<ulong> appIds, CancellationToken ct = default);

    /// <summary>Newest proposals of one safe, with whether <paramref name="member"/> already approved each.</summary>
    Task<SafeProposalPage> ProposalsAsync(string network, ulong appId, string? member, CancellationToken ct = default);
}

public sealed class SafeBuilder(IBuildersClient builders) : ISafeBuilder
{
    public const string CreateBuilder = "safe.create";
    public const string ListBuilder = "safe.list";
    public const string ProposalsBuilder = "safe.proposals";

    /// <summary>algo-safe action bits: payment, asset transfer and application call (ACT_PAY | ACT_AXFER | ACT_APPL).</summary>
    public const int InitialAllowedActions = 1 | 2 | 4;

    public async Task<SafeBuild> BuildDeployAsync(string network, string sender, string name, CancellationToken ct = default)
    {
        var result = await builders.BuildAsync(CreateBuilder, network, new { step = "deploy", sender, name, simulate = true }, ct);
        var (group, preview) = Check(result, sender, "deploy");
        if (group.Transactions.Count != 1 || group.Transactions[0] is not ApplicationCreateTransaction)
            throw new SafeBuildException("The deployment is not a single application-create call.");
        if (!string.Equals(Str(preview, "name"), name, StringComparison.Ordinal))
            throw new SafeBuildException("The builder named a different safe.");
        ulong? validator = ulong.TryParse(Str(preview, "validatorAppId"), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
        return Finish(result, group, sender, "deploy", validator);
    }

    public async Task<SafeBuild> BuildBootstrapAsync(string network, string sender, ulong appId, string groupName, IReadOnlyList<string> members, int threshold, CancellationToken ct = default)
    {
        var parameters = new
        {
            step = "bootstrap",
            sender,
            appId = appId.ToString(CultureInfo.InvariantCulture),
            simulate = true,
            groups = new[]
            {
                new
                {
                    name = groupName,
                    threshold,
                    allowedActions = InitialAllowedActions,
                    members = members.Select(m => new { address = m }).ToArray(),
                },
            },
        };
        var result = await builders.BuildAsync(CreateBuilder, network, parameters, ct);
        var (group, preview) = Check(result, sender, "bootstrap");
        // Bootstrap and finalize are calls to the safe itself, nothing else (funding is a separate transfer).
        foreach (var t in group.Transactions)
            if (t is not ApplicationCallTransaction
                || t is ApplicationCreateTransaction or ApplicationUpdateTransaction or ApplicationDeleteTransaction
                    or ApplicationClearStateTransaction or ApplicationCloseOutTransaction
                || AppId(t) != appId)
                throw new SafeBuildException("The bootstrap group contains a transaction that is not a call to the safe.");
        if (Str(preview, "appId") != appId.ToString(CultureInfo.InvariantCulture))
            throw new SafeBuildException("The builder facts name a different safe.");

        // The signer group the builder describes must be exactly the one requested.
        try
        {
            var groups = preview.GetProperty("groups");
            if (groups.GetArrayLength() != 1) throw new SafeBuildException("The builder describes a different number of signer groups.");
            var g = groups[0];
            var described = g.GetProperty("members").EnumerateArray().Select(m => m.GetString()).ToHashSet(StringComparer.Ordinal);
            if (!described.SetEquals(members) || described.Count != members.Count)
                throw new SafeBuildException("The builder describes different signers.");
            if (Str(g, "threshold") != threshold.ToString(CultureInfo.InvariantCulture))
                throw new SafeBuildException("The builder describes a different threshold.");
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
        {
            throw new SafeBuildException("The builder facts are malformed.");
        }
        return Finish(result, group, sender, "bootstrap", null);
    }

    public async Task<IReadOnlyList<SafeSummary>> ListAsync(string network, string member, IReadOnlyList<ulong> appIds, CancellationToken ct = default)
    {
        if (appIds.Count == 0) return [];
        var data = await builders.ReadAsync(ListBuilder, network,
            new { member, appIds = appIds.Select(a => a.ToString(CultureInfo.InvariantCulture)).ToArray() }, ct);
        try
        {
            if (Str(data, "member") != member) throw new SafeBuildException("The safe listing is for a different member.");
            var asked = appIds.ToHashSet();
            var list = new List<SafeSummary>();
            foreach (var s in Items(data, "safes"))
            {
                var appId = U64(s, "appId");
                if (!asked.Contains(appId)) throw new SafeBuildException("The safe listing has a safe that was not asked for.");
                var groups = Items(s, "groups").Select(g => new SafeGroupSummary(
                    Str(g, "id") ?? "", Str(g, "name") ?? "", (int)U64(g, "threshold"), (int)U64(g, "memberCount"),
                    Items(g, "allowedActions").Select(a => a.GetString() ?? "").ToList(),
                    g.TryGetProperty("isAdminGroup", out var ia) && ia.ValueKind == JsonValueKind.True,
                    long.TryParse(Str(g, "membershipEpoch"), NumberStyles.None, CultureInfo.InvariantCulture, out var ep) ? ep : null)).ToList();
                list.Add(new SafeSummary(appId, Str(s, "address") ?? "", Str(s, "name") ?? "",
                    s.TryGetProperty("paused", out var p) && p.ValueKind == JsonValueKind.True, groups));
            }
            return list;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new SafeBuildException("The safe listing has an unexpected shape.");
        }
    }

    public async Task<SafeProposalPage> ProposalsAsync(string network, ulong appId, string? member, CancellationToken ct = default)
    {
        var p = new Dictionary<string, object?> { ["appId"] = appId.ToString(CultureInfo.InvariantCulture), ["limit"] = 25 };
        if (member is not null) p["member"] = member;
        var data = await builders.ReadAsync(ProposalsBuilder, network, p, ct);
        try
        {
            if (U64(data, "appId") != appId) throw new SafeBuildException("The proposal listing is for a different safe.");
            var proposals = Items(data, "proposals").Select(x => new SafeProposalSummary(
                U64(x, "proposalId"), Str(x, "groupId") ?? "", Str(x, "kind") ?? "", Str(x, "status") ?? "",
                (int)U64(x, "approvals"), (int)U64(x, "threshold"), Str(x, "proposer") ?? "",
                U64(x, "expiryRound"), U64(x, "payloadVersion"),
                x.TryGetProperty("approvedByMember", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False ? a.GetBoolean() : null,
                x.TryGetProperty("payload", out var pl) ? pl.Clone() : default)).ToList();
            return new SafeProposalPage(appId, Str(data, "safeAddress") ?? "", proposals,
                data.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new SafeBuildException("The proposal listing has an unexpected shape.");
        }
    }

    private static (GroupedTransactions Group, JsonElement Preview) Check(BuildResult result, string sender, string step)
    {
        if (result.Preview is not { ValueKind: JsonValueKind.Object } preview)
            throw new SafeBuildException("The builder returned no preview facts.");
        if (Str(preview, "kind") != "safe.create" || Str(preview, "step") != step)
            throw new SafeBuildException("The builder returned a different kind of group.");
        // Creating a safe is valued at zero: the builder must say so, and the group must carry no payment.
        if (Str(preview, "valueMicroAlgo") != "0")
            throw new SafeBuildException("The builder values the safe creation above zero.");

        GroupedTransactions group;
        try { group = AlgorandGroup.Ensure(AlgorandGroup.Decode(result.Transactions)); }
        catch (Exception e) when (e is GroupException or ArgumentException or InvalidOperationException or FormatException)
        {
            throw new SafeBuildException("The builder returned a group that could not be read.");
        }
        if (group.Transactions.Any(t => !string.Equals(t.Sender.EncodeAsString(), sender, StringComparison.Ordinal)))
            throw new SafeBuildException("The built group has a transaction from a different sender.");
        if (group.Transactions.Any(t => t.RekeyTo is { } r && r.Bytes.Any(b => b != 0)))
            throw new SafeBuildException("The built group rekeys the sender.");
        return (group, preview);
    }

    private static SafeBuild Finish(BuildResult result, GroupedTransactions group, string sender, string step, ulong? validator)
    {
        var (first, last) = group.ValidityWindow();
        SafeSimulation? sim = null;
        if (result.Simulation is { ValueKind: JsonValueKind.Object } s)
            sim = new SafeSimulation(s.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True, Str(s, "failureMessage"));
        return new SafeBuild(step, sender, group, first, last, sim, validator);
    }

    // The SDK declares ApplicationId on the concrete call types, not on the abstract base.
    private static ulong? AppId(Transaction tx) => tx.GetType().GetProperty("ApplicationId")?.GetValue(tx) as ulong?;

    private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static ulong U64(JsonElement e, string name) =>
        ulong.Parse(Str(e, name) ?? throw new KeyNotFoundException(name), NumberStyles.None, CultureInfo.InvariantCulture);
}
