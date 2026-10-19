using System.Text.Json;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Tools.Mcp;
using Biatec.Chat.Tools.Wallet.Transfer;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Tools.Safe.Create;

/// <summary>A proposal still waiting for approvals (active) or for execution (ready), with its progress.</summary>
public sealed record SafePendingProposal(
    ulong ProposalId,
    string GroupId,
    string Kind,
    string Status,
    int Approvals,
    int Threshold,
    string Proposer,
    ulong ExpiryRound,
    ulong PayloadVersion,
    bool? ApprovedByYou,
    JsonElement Payload)
{
    public string Progress => $"{Approvals} of {Threshold} approvals";
}

/// <summary>The data of one safe card: the safe, the user's signer groups in it, and its pending proposals.</summary>
public sealed record SafeCardData(
    ulong AppId,
    string Address,
    string Name,
    string Network,
    bool Paused,
    IReadOnlyList<string> YourAddresses,
    IReadOnlyList<SafeGroupSummary> Groups,
    IReadOnlyList<SafePendingProposal> PendingProposals,
    bool ProposalsTruncated);

public sealed record SafeListOutcome(bool IsError, string Message, IReadOnlyList<SafeCardData> Safes)
{
    public static SafeListOutcome Error(string message) => new(true, message, []);
}

/// <summary>
/// <c>safe.list</c>: the safes the signed-in user is a member of, with pending proposals and approval progress. The
/// candidates are the safes created through this tenant's chat plus any app ids the user named; membership is read
/// from the chain by algo-safe in the sidecar, so a safe the user is not in never shows.
/// </summary>
public sealed class SafeListService(IBiatecMcpClient mcp, ISafeBuilder builder, ChatDbContext db, TimeProvider clock)
{
    public const int MaxCandidates = 50;
    public const int MaxAddresses = 5;
    public const int MaxSafesWithProposals = 10;

    public async Task<SafeListOutcome> ListAsync(McpCallContext ctx, string tenantId, string network, IReadOnlyList<ulong> namedAppIds, CancellationToken ct)
    {
        var own = await mcp.ListAlgorandAddressesAsync(ctx, ct);
        if (own.IsError) return SafeListOutcome.Error("The addresses could not be read. Tell the user to try again.");
        var addresses = McpWalletJson.Addresses(own.Json).Distinct(StringComparer.Ordinal).Take(MaxAddresses).ToList();
        if (addresses.Count == 0) return new SafeListOutcome(false, "The user has no Algorand addresses, so they are not a member of any safe.", []);

        var known = await db.KnownSafes.AsNoTracking()
            .Where(k => k.TenantId == tenantId && k.Network == network)
            .OrderByDescending(k => k.CreatedAt).Select(k => k.AppId).Take(MaxCandidates).ToListAsync(ct);
        var candidates = namedAppIds.Concat(known.Select(k => (ulong)k)).Where(a => a > 0).Distinct().Take(MaxCandidates).ToList();
        if (candidates.Count == 0)
            return new SafeListOutcome(false, "No safes are known yet. If the user is a member of a safe, ask for its app id and call safe.list again with safeAppIds.", []);

        // Which of the candidates each of the user's addresses is a member of.
        var found = new Dictionary<ulong, (SafeSummary Safe, List<string> Members)>();
        foreach (var address in addresses)
            foreach (var s in await builder.ListAsync(network, address, candidates, ct))
            {
                if (!found.TryGetValue(s.AppId, out var entry)) found[s.AppId] = entry = (s, []);
                entry.Members.Add(address);
            }

        // A named safe the user turned out to be a member of is remembered, so it is listed without its id next time.
        var knownSet = known.Select(k => (ulong)k).ToHashSet();
        foreach (var appId in found.Keys.Where(a => !knownSet.Contains(a) && a <= long.MaxValue))
        {
            var row = new KnownSafe { Id = Guid.NewGuid(), TenantId = tenantId, Network = network, AppId = (long)appId, CreatedAt = clock.GetUtcNow() };
            db.KnownSafes.Add(row);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.Entry(row).State = EntityState.Detached; }
        }

        var cards = new List<SafeCardData>();
        foreach (var (appId, (safe, members)) in found.OrderBy(f => candidates.IndexOf(f.Key)))
        {
            IReadOnlyList<SafePendingProposal> pending = [];
            var truncated = false;
            if (cards.Count < MaxSafesWithProposals)
            {
                var page = await builder.ProposalsAsync(network, appId, members[0], ct);
                truncated = page.Truncated;
                pending = page.Proposals.Where(p => p.Status is "active" or "ready")
                    .Select(p => new SafePendingProposal(p.ProposalId, p.GroupId, p.Kind, p.Status, p.Approvals, p.Threshold, p.Proposer,
                        p.ExpiryRound, p.PayloadVersion, p.ApprovedByMember, p.Payload)).ToList();
            }
            cards.Add(new SafeCardData(appId, safe.Address, safe.Name, network, safe.Paused, members, safe.Groups, pending, truncated));
        }

        var message = cards.Count == 0
            ? "The user is not a member of any known safe. If they are a member of one, ask for its app id and call safe.list again with safeAppIds."
            : $"The user is a member of {cards.Count} safe(s).";
        return new SafeListOutcome(false, message, cards);
    }
}
