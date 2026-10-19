using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Api.Evidence.Anchoring;

/// <summary>An anchor as exported in a bundle (<see cref="Id"/> resolves entries' anchorRef to a transaction) or read from the chain.</summary>
public sealed record AnchorRecord(Guid Id, long Seq, string TxId, string Root, string? PrevTxId, long? GapFrom);

/// <summary>One conversation's inclusion proof in one anchor, as carried in the bundle.</summary>
public sealed record AnchorProof(
    string AnchorTxId, long AnchorSeq, Guid ConversationId, long AnchoredSeq, string HeadHash, int LeafIndex, IReadOnlyList<MerkleSibling> SiblingHashes);

/// <summary>What the export (T-036) embeds: the anchors the conversation refers to and its proofs.</summary>
public sealed record AnchorExport(IReadOnlyList<AnchorRecord> Anchors, IReadOnlyList<AnchorProof> Proofs);

public sealed record AnchorVerification(bool Valid, bool FullyAnchored, long? UnanchoredFromSeq, bool EndAnchored, IReadOnlyList<string> Errors)
{
    public bool Verified => Valid && FullyAnchored;
}

/// <summary>Reads the proofs for the export from the database.</summary>
public static class AnchorExporter
{
    public static async Task<AnchorExport> ExportAsync(ChatDbContext db, Guid conversationId, CancellationToken ct = default)
    {
        var leaves = await db.EvidenceAnchorLeaves.AsNoTracking().Where(l => l.ConversationId == conversationId).ToListAsync(ct);
        var refs = await db.EvidenceEntries.AsNoTracking().Where(e => e.ConversationId == conversationId && e.AnchorRef != null)
            .Select(e => e.AnchorRef!.Value).ToListAsync(ct);
        var ids = leaves.Select(l => l.AnchorId).Concat(refs).Distinct().ToList();
        var anchors = await db.EvidenceAnchors.AsNoTracking().Where(a => ids.Contains(a.Id)).OrderBy(a => a.SequenceNumber).ToListAsync(ct);
        var byId = anchors.ToDictionary(a => a.Id);
        var proofs = leaves.OrderBy(l => byId[l.AnchorId].SequenceNumber).Select(l => new AnchorProof(
            byId[l.AnchorId].AnchorTxId, byId[l.AnchorId].SequenceNumber, l.ConversationId, l.AnchoredSeq, l.HeadHash, l.LeafIndex,
            AnchorService.ParseSiblings(l.SiblingHashes))).ToList();
        return new AnchorExport(
            anchors.Select(a => new AnchorRecord(a.Id, a.SequenceNumber, a.AnchorTxId, a.MerkleRoot, a.PreviousAnchorTxId,
                a.GapFrom is null ? null : long.Parse(a.GapFrom))).ToList(),
            proofs);
    }
}

/// <summary>
/// Offline check of a conversation's anchoring, given the chain's anchors (read from the indexer and parsed from the
/// notes; this is the source of truth), the bundle's entries, its proofs, and its anchor list (for anchorRef lookups).
/// Rules follow PRODUCT-DESCRIPTION.md (threat model): every on-chain anchor between the first one containing the
/// conversation and the one containing its end marker needs a proof, each anchored head must equal the entry hash at
/// that seq, the first containing anchor is at most two on-chain anchors after the recorded one, and entries after the
/// last proof are reported as signed but not yet anchored.
/// </summary>
public static class AnchorVerifier
{
    public const int MaxFirstInclusionDistance = 2;

    public static AnchorVerification Verify(
        IReadOnlyList<EvidenceEntry> entries,
        IReadOnlyList<AnchorProof> proofs,
        IReadOnlyList<AnchorRecord> bundleAnchors,
        IReadOnlyList<AnchorRecord> chain)
    {
        var errors = new List<string>();
        if (entries.Count == 0) return new AnchorVerification(false, false, null, false, ["No entries."]);
        var ordered = entries.OrderBy(e => e.Seq).ToList();
        var conv = ordered[0].ConversationId;
        var bySeq = ordered.ToDictionary(e => e.Seq);
        var lastSeq = ordered[^1].Seq;

        // 1. The chain itself: ordered, linked by previous tx id, gaps declared.
        var chainSorted = chain.OrderBy(a => a.Seq).ToList();
        var indexOfTx = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < chainSorted.Count; i++)
        {
            var a = chainSorted[i];
            if (!indexOfTx.TryAdd(a.TxId, i)) { errors.Add($"Anchor transaction {a.TxId} appears twice on the chain."); continue; }
            if (i == 0) continue;
            var p = chainSorted[i - 1];
            if (a.Seq == p.Seq) errors.Add($"Two anchors share sequence {a.Seq}.");
            if (a.PrevTxId != p.TxId) errors.Add($"Anchor {a.Seq} does not reference the previous anchor transaction.");
            if (a.Seq != p.Seq + 1 && a.GapFrom != p.Seq + 1) errors.Add($"Anchor {a.Seq} skips sequence numbers without recording the gap.");
        }

        // 2. Each proof: on chain, root matches, Merkle path recomputes, head equals the bundle's entry hash.
        var proofIdx = new List<(int Index, AnchorProof Proof)>();
        foreach (var pr in proofs)
        {
            if (pr.ConversationId != conv) { errors.Add("Proof for another conversation."); continue; }
            if (!indexOfTx.TryGetValue(pr.AnchorTxId, out var idx)) { errors.Add($"Proof refers to anchor {pr.AnchorTxId}, which is not on the chain."); continue; }
            var anchor = chainSorted[idx];
            if (pr.AnchorSeq != anchor.Seq) errors.Add($"Proof for anchor {anchor.Seq} states sequence {pr.AnchorSeq}.");
            string root;
            try { root = MerkleTree.ComputeRoot(pr.ConversationId, pr.AnchoredSeq, pr.HeadHash, pr.SiblingHashes); }
            catch (Exception e) when (e is FormatException or ArgumentException) { errors.Add($"Proof for anchor {anchor.Seq} is malformed."); continue; }
            if (root != anchor.Root) { errors.Add($"Proof for anchor {anchor.Seq} does not lead to the on-chain Merkle root."); continue; }
            if (!bySeq.TryGetValue(pr.AnchoredSeq, out var entry)) { errors.Add($"Proof for anchor {anchor.Seq} anchors seq {pr.AnchoredSeq}, which the bundle lacks."); continue; }
            if (entry.EntryHash != pr.HeadHash) { errors.Add($"Anchor {anchor.Seq} anchored a head that differs from the bundle's entryHash at seq {pr.AnchoredSeq}."); continue; }
            proofIdx.Add((idx, pr));
        }
        if (proofIdx.GroupBy(p => p.Index).Any(g => g.Count() > 1)) errors.Add("More than one proof for the same anchor.");
        proofIdx = proofIdx.GroupBy(p => p.Index).Select(g => g.First()).OrderBy(p => p.Index).ToList();
        for (var i = 1; i < proofIdx.Count; i++)
            if (proofIdx[i].Proof.AnchoredSeq < proofIdx[i - 1].Proof.AnchoredSeq) errors.Add("Anchored seq goes backwards between anchors.");

        // 3. Starts: the first entry and every resume entry record the latest confirmed anchor.
        int RefIndex(EvidenceEntry e, out bool ok)
        {
            ok = true;
            if (e.AnchorRef is not { } id) return -1;
            var tx = bundleAnchors.FirstOrDefault(a => a.Id == id)?.TxId;
            if (tx is not null && indexOfTx.TryGetValue(tx, out var i)) return i;
            ok = false;
            return -1;
        }
        var first = RefIndex(ordered[0], out var firstOk);
        if (!firstOk) errors.Add("The first entry's anchorRef is not an anchor on the chain.");
        var resumes = new List<(long Seq, int RefIndex)>();
        foreach (var r in ordered.Where(e => e.Kind == AnchoredEvidenceWriter.ResumeKind && e.Seq > 1))
        {
            var ri = RefIndex(r, out var ok);
            if (!ok) errors.Add($"Resume entry {r.Seq} refers to an anchor that is not on the chain.");
            else resumes.Add((r.Seq, ri));
        }

        // 4. Runs of consecutive on-chain anchors; each run must start where a first or resume entry allows, and no
        //    on-chain anchor inside a run may lack a proof.
        var runs = new List<List<(int Index, AnchorProof Proof)>>();
        foreach (var p in proofIdx)
        {
            if (runs.Count > 0 && runs[^1][^1].Index + 1 == p.Index) runs[^1].Add(p);
            else runs.Add([p]);
        }
        // A missing proof strictly inside what should be one run shows up as two runs; a run boundary needs a resume.
        var usedResumes = new HashSet<long>();
        for (var r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            var start = run[0].Index;
            bool Within(int refIndex) => start > refIndex && start <= refIndex + MaxFirstInclusionDistance;
            if (r == 0)
            {
                if (!Within(first)) errors.Add($"The first anchor containing the conversation ({chainSorted[start].Seq}) is not within {MaxFirstInclusionDistance} anchors after the one the first entry recorded.");
            }
            else
            {
                var prevLast = runs[r - 1][^1].Proof.AnchoredSeq;
                var match = resumes.FirstOrDefault(x => !usedResumes.Contains(x.Seq) && x.Seq > prevLast && x.Seq <= run[0].Proof.AnchoredSeq && Within(x.RefIndex));
                if (match == default) errors.Add($"Anchor {chainSorted[start].Seq} follows a gap in proofs that no resume entry explains (a proof is missing).");
                else usedResumes.Add(match.Seq);
            }
        }
        if (runs.Count == 0 && chainSorted.Count - 1 >= first + MaxFirstInclusionDistance + 1)
            errors.Add("The chain has anchors well after the first entry's anchorRef but the bundle carries no proof.");

        // 5. End and unanchored tail.
        var endAnchored = false;
        long? unanchored = null;
        var lastProofSeq = proofIdx.Count == 0 ? 0 : proofIdx[^1].Proof.AnchoredSeq;
        if (lastProofSeq < lastSeq) unanchored = lastProofSeq + 1;
        if (ordered[^1].Kind == AnchorService.EndKind) endAnchored = lastProofSeq == lastSeq;

        return new AnchorVerification(errors.Count == 0, errors.Count == 0 && unanchored is null, unanchored, endAnchored, errors);
    }
}
