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
}

/// <summary>Sends one anchor transaction (0 ALGO self-payment from the evidence account) and waits for confirmation.</summary>
public interface IAnchorBroadcaster
{
    /// <returns>The confirmed transaction ID. Throws when the transaction was not confirmed.</returns>
    Task<string> BroadcastAsync(byte[] note, CancellationToken ct);
}

/// <summary>Remembers sequence numbers whose tick was lost, until the next anchor lands and records them as a gap.</summary>
public sealed class AnchorGapTracker
{
    private readonly object _lock = new();
    private long? _from;
    private long _count;

    public void RecordLost(long seq)
    {
        lock (_lock) { _from ??= seq; _count++; }
    }

    public (long? From, long Count) Peek() { lock (_lock) return (_from, _count); }

    public void Clear() { lock (_lock) { _from = null; _count = 0; } }
}

public sealed record AnchorTickResult(EvidenceAnchor? Anchor, int Leaves, bool Lost);

/// <summary>
/// One anchor tick: snapshot the head of every conversation that is open or ended since its last anchor, build the
/// Merkle root, put it in the note of a transaction, and persist the anchor and one leaf per conversation once it is
/// confirmed. A tick that cannot be confirmed is retried and, when it is still lost, its sequence number is consumed so
/// the next anchor's note records the gap.
/// </summary>
public sealed class AnchorService(
    ChatDbContext db,
    IAnchorBroadcaster broadcaster,
    AnchorGapTracker gaps,
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

        var previous = await db.EvidenceAnchors.AsNoTracking().OrderByDescending(a => a.SequenceNumber)
            .Select(a => new { a.SequenceNumber, a.AnchorTxId }).FirstOrDefaultAsync(ct);
        var (gapFrom, gapCount) = gaps.Peek();
        var seq = (previous?.SequenceNumber ?? 0) + 1 + gapCount;
        var note = new AnchorNote(tree.Root, seq, previous?.AnchorTxId, gapFrom);
        var noteBytes = note.ToBytes();

        string? txId = null;
        for (var attempt = 1; attempt <= Math.Max(1, _o.MaxBroadcastAttempts) && !ct.IsCancellationRequested; attempt++)
        {
            try { txId = await broadcaster.BroadcastAsync(noteBytes, ct); break; }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Anchor {Seq} broadcast attempt {Attempt} failed.", seq, attempt);
                if (attempt < _o.MaxBroadcastAttempts && _o.RetryDelay > TimeSpan.Zero) await Task.Delay(_o.RetryDelay, ct);
            }
        }
        ct.ThrowIfCancellationRequested();

        if (txId is null)
        {
            // Lost: the sequence number stays consumed; the next anchor records the gap.
            gaps.RecordLost(seq);
            log.LogError("Anchor {Seq} lost after {Attempts} attempts; the next anchor will record the gap.", seq, _o.MaxBroadcastAttempts);
            return new AnchorTickResult(null, picked.Count, true);
        }

        var anchor = new EvidenceAnchor
        {
            Id = Guid.NewGuid(),
            SequenceNumber = seq,
            PreviousAnchorTxId = previous?.AnchorTxId,
            AnchorTxId = txId,
            MerkleRoot = tree.Root,
            AnchoredAt = _time.GetUtcNow(),
            GapFrom = note.GapFrom?.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
        // The transaction is already on chain: keep trying to record it rather than lose the proofs.
        for (var attempt = 1; ; attempt++)
        {
            try { await db.SaveChangesAsync(ct); break; }
            catch (Exception e) when (attempt < 5 && e is not OperationCanceledException)
            {
                log.LogError(e, "Persisting anchor {Seq} (tx {TxId}) failed, attempt {Attempt}.", seq, txId, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
            }
        }
        gaps.Clear();
        return new AnchorTickResult(anchor, picked.Count, false);
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
