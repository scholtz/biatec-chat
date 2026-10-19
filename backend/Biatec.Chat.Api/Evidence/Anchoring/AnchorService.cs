using System.Text.Json;
using System.Text.Json.Nodes;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Evidence.Anchoring;

public sealed class AnchoringOptions
{
    public const string Section = "Evidence:Anchoring";

    /// <summary>Off by default: anchoring needs a funded evidence account. Use testnet unless an operator decides otherwise.</summary>
    public bool Enabled { get; set; }

    /// <summary>Key of the network in the <c>Algod</c> section (for example "testnet").</summary>
    public string Network { get; set; } = "testnet";

    /// <summary>The 25-word mnemonic of the evidence account. Supply only through env vars or a secret store, never a committed file.</summary>
    public string? AccountMnemonic { get; set; }

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>A conversation with no new entry for this long is anchored one last time, then dropped from later ticks.</summary>
    public TimeSpan IdleDrop { get; set; } = TimeSpan.FromMinutes(30);

    public int MaxBroadcastAttempts { get; set; } = 3;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long one broadcast attempt waits for the transaction to confirm.</summary>
    public TimeSpan ConfirmTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ConfirmPollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>Sends one anchor transaction (0 ALGO self-payment from the evidence account) and waits for confirmation.</summary>
public interface IAnchorBroadcaster
{
    /// <returns>The confirmed transaction ID. Throws when the transaction was not confirmed.</returns>
    Task<string> BroadcastAsync(byte[] note, CancellationToken ct);
}

public sealed record AnchorTickResult(EvidenceAnchor? Anchor, int Leaves, bool Lost);

/// <summary>
/// One anchor tick: snapshot the head of every conversation that is open or ended since its last anchor, build the
/// Merkle root, put it in the note of a transaction, and persist the anchor and one leaf per conversation once it is
/// confirmed. A tick that cannot be confirmed is retried and, when it is still lost, its sequence number is consumed so
/// the next anchor's note records the gap, also after a restart. Every attempt is written to EvidenceAnchorAttempts before it
/// is broadcast; an attempt whose transaction confirmed but whose anchor row could not be stored is completed at the
/// start of the next tick.
/// </summary>
public sealed class AnchorService(
    ChatDbContext db,
    IAnchorBroadcaster broadcaster,
    IOptions<AnchoringOptions> options,
    ILogger<AnchorService> log,
    TimeProvider? time = null)
{
    public const string EndKind = "end";
    private readonly AnchoringOptions _o = options.Value;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private sealed record Head(Guid ConversationId, long Seq, string EntryHash, string Kind, DateTimeOffset SignedAt);

    public async Task<AnchorTickResult> TickAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        await RecoverAsync(ct);
        var heads = await db.EvidenceEntries.AsNoTracking()
            .Where(e => e.Seq == db.EvidenceEntries.Where(x => x.ConversationId == e.ConversationId).Max(x => x.Seq))
            .Select(e => new Head(e.ConversationId, e.Seq, e.EntryHash, e.Kind, e.SignedAt))
            .ToListAsync(ct);
        var lastLeaf = (await db.EvidenceAnchorLeaves.AsNoTracking()
                .GroupBy(l => l.ConversationId)
                .Select(g => new { ConversationId = g.Key, Seq = g.Max(l => l.AnchoredSeq) })
                .ToListAsync(ct))
            .ToDictionary(x => x.ConversationId, x => x.Seq);

        var picked = heads.Where(h => Due(h, lastLeaf.GetValueOrDefault(h.ConversationId, 0), now)).ToList();
        if (picked.Count == 0) return new AnchorTickResult(null, 0, false);

        var tree = MerkleTree.Build(picked.Select(h => new AnchorLeafInput(h.ConversationId, h.Seq, h.EntryHash)));

        var previous = await LatestAnchorAsync(ct);
        var lastAnchorSeq = previous?.SequenceNumber ?? 0;
        var attempts = await db.EvidenceAnchorAttempts.AsNoTracking().Select(a => new { a.SequenceNumber, a.Status }).ToListAsync(ct);
        var gapFrom = attempts.Where(a => a.Status == LostStatus && a.SequenceNumber > lastAnchorSeq).Select(a => (long?)a.SequenceNumber).Min();
        var seq = Math.Max(lastAnchorSeq, attempts.Select(a => a.SequenceNumber).DefaultIfEmpty(0).Max()) + 1;
        var note = new AnchorNote(tree.Root, seq, previous?.AnchorTxId, gapFrom);
        var noteBytes = note.ToBytes();

        // Consume the sequence number durably before anything is broadcast.
        var attempt = new EvidenceAnchorAttempt
        {
            SequenceNumber = seq,
            Status = PendingStatus,
            MerkleRoot = tree.Root,
            PreviousAnchorTxId = previous?.AnchorTxId,
            GapFrom = note.GapFrom?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Leaves = JsonSerializer.Serialize(picked.Select(h => new AnchorLeafInput(h.ConversationId, h.Seq, h.EntryHash))),
            CreatedAt = _time.GetUtcNow(),
        };
        db.EvidenceAnchorAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);

        // The broadcaster is idempotent per note: a retry first checks whether the earlier transaction confirmed and
        // never sends a second transaction while the first can still be confirmed.
        string? txId = null;
        for (var n = 1; n <= Math.Max(1, _o.MaxBroadcastAttempts) && !ct.IsCancellationRequested; n++)
        {
            try { txId = await broadcaster.BroadcastAsync(noteBytes, ct); break; }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Anchor {Seq} broadcast attempt {Attempt} failed.", seq, n);
                if (n < _o.MaxBroadcastAttempts && _o.RetryDelay > TimeSpan.Zero) await Task.Delay(_o.RetryDelay, ct);
            }
        }
        ct.ThrowIfCancellationRequested();

        if (txId is null)
        {
            // Lost: the sequence number stays consumed (durably); the next anchor records the gap.
            attempt.Status = LostStatus;
            await SaveWithRetryAsync(seq, null, ct);
            log.LogError("Anchor {Seq} lost after {Attempts} attempts; the next anchor will record the gap.", seq, _o.MaxBroadcastAttempts);
            return new AnchorTickResult(null, picked.Count, true);
        }

        // Confirmed on chain: remember the tx id first (small write), so a failure while storing the proofs is recoverable.
        attempt.TxId = txId;
        try { await SaveWithRetryAsync(seq, txId, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Could not record tx {TxId} of anchor {Seq} on its attempt row.", txId, seq);
            db.ChangeTracker.Clear();
            throw;
        }

        var anchor = StoreAnchor(attempt.SequenceNumber, txId, attempt.PreviousAnchorTxId, tree, attempt.GapFrom);
        db.EvidenceAnchorAttempts.Remove(attempt);
        try { await SaveWithRetryAsync(seq, txId, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The attempt row (with the tx id) survives, so the next tick completes the anchor. Do not keep the half-added entities.
            db.ChangeTracker.Clear();
            log.LogError(e, "Persisting anchor {Seq} (tx {TxId}) failed; it will be completed on the next tick.", seq, txId);
            throw;
        }
        return new AnchorTickResult(anchor, picked.Count, false);
    }

    public const string PendingStatus = "pending";
    public const string LostStatus = "lost";

    private async Task<(long SequenceNumber, string AnchorTxId)?> LatestAnchorAsync(CancellationToken ct)
    {
        var p = await db.EvidenceAnchors.AsNoTracking().OrderByDescending(a => a.SequenceNumber)
            .Select(a => new { a.SequenceNumber, a.AnchorTxId }).FirstOrDefaultAsync(ct);
        return p is null ? null : (p.SequenceNumber, p.AnchorTxId);
    }

    private async Task SaveWithRetryAsync(long seq, string? txId, CancellationToken ct)
    {
        for (var n = 1; ; n++)
        {
            try { await db.SaveChangesAsync(ct); return; }
            catch (Exception e) when (n < 5 && e is not OperationCanceledException)
            {
                log.LogError(e, "Persisting anchor {Seq} (tx {TxId}) failed, attempt {Attempt}.", seq, txId, n);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * n), ct);
            }
        }
    }

    private EvidenceAnchor StoreAnchor(long seq, string txId, string? previousTxId, MerkleResult tree, string? gapFrom)
    {
        var anchor = new EvidenceAnchor
        {
            Id = Guid.NewGuid(),
            SequenceNumber = seq,
            PreviousAnchorTxId = previousTxId,
            AnchorTxId = txId,
            MerkleRoot = tree.Root,
            AnchoredAt = _time.GetUtcNow(),
            GapFrom = gapFrom,
        };
        db.EvidenceAnchors.Add(anchor);
        foreach (var p in tree.Proofs)
        {
            db.EvidenceAnchorLeaves.Add(new EvidenceAnchorLeaf
            {
                AnchorId = anchor.Id,
                ConversationId = p.Leaf.ConversationId,
                AnchoredSeq = p.Leaf.AnchoredSeq,
                HeadHash = p.Leaf.HeadHash,
                LeafIndex = p.LeafIndex,
                SiblingHashes = SerializeSiblings(p.Siblings),
            });
        }
        return anchor;
    }

    /// <summary>
    /// Start of every tick: finishes attempts left behind by a crash or a failed save. One whose transaction is known
    /// to be confirmed gets its anchor row and proofs rebuilt from the stored leaves; one that never got a tx id is
    /// marked lost (this assumes a single anchoring instance).
    /// </summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        var stale = await db.EvidenceAnchorAttempts.Where(a => a.Status == PendingStatus).OrderBy(a => a.SequenceNumber).ToListAsync(ct);
        foreach (var a in stale)
        {
            if (a.TxId is null) { a.Status = LostStatus; continue; }
            var leaves = JsonSerializer.Deserialize<List<AnchorLeafInput>>(a.Leaves) ?? [];
            var tree = MerkleTree.Build(leaves);
            if (tree.Root != a.MerkleRoot) { log.LogError("Attempt {Seq} leaves do not match its root; marking it lost.", a.SequenceNumber); a.Status = LostStatus; continue; }
            if (!await db.EvidenceAnchors.AnyAsync(x => x.SequenceNumber == a.SequenceNumber, ct))
                StoreAnchor(a.SequenceNumber, a.TxId, a.PreviousAnchorTxId, tree, a.GapFrom);
            db.EvidenceAnchorAttempts.Remove(a);
            log.LogWarning("Completed anchor {Seq} (tx {TxId}) from its attempt row.", a.SequenceNumber, a.TxId);
        }
        if (stale.Count > 0) await SaveWithRetryAsync(0, null, ct);
    }

    /// <summary>Open (changed or not), changed since the last anchor, or an end marker not yet anchored; idle ones get one last anchor.</summary>
    private bool Due(Head h, long lastAnchoredSeq, DateTimeOffset now)
    {
        if (lastAnchoredSeq == 0 || lastAnchoredSeq < h.Seq) return true;
        if (h.Kind == EndKind) return false; // end marker already anchored
        return now - h.SignedAt < _o.IdleDrop;
    }

    public static string SerializeSiblings(IEnumerable<MerkleSibling> siblings) =>
        new JsonArray(siblings.Select(s => (JsonNode)new JsonObject { ["hash"] = s.Hash, ["side"] = s.Side }).ToArray()).ToJsonString();

    public static IReadOnlyList<MerkleSibling> ParseSiblings(string json) =>
        (JsonNode.Parse(json) as JsonArray ?? throw new JsonException("siblingHashes must be an array."))
            .Select(n => new MerkleSibling((string)n!["hash"]!, (string)n["side"]!)).ToList();
}
