using System.Text.Json.Nodes;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Evidence.Anchoring;

/// <summary>
/// Writes the entries whose <c>anchorRef</c> matters: a conversation's first entry, the <c>resume</c> entry after a
/// conversation was dropped from the ticks for being idle, and the <c>end</c> marker the next tick anchors. The
/// anchorRef is the transaction ID of the latest anchor that was confirmed on chain (never one pending or lost); the export
/// carries it as is.
/// </summary>
public sealed class AnchoredEvidenceWriter(IEvidenceLog log, ChatDbContext db, IOptions<AnchoringOptions> options, TimeProvider? time = null)
{
    public const string ResumeKind = "resume";
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Transaction ID of the latest confirmed anchor, or null when none exists yet.</summary>
    public async Task<string?> LatestConfirmedAnchorAsync(CancellationToken ct = default) =>
        await db.EvidenceAnchors.AsNoTracking().OrderByDescending(a => a.SequenceNumber).Select(a => a.AnchorTxId).FirstOrDefaultAsync(ct);

    /// <summary>Appends the conversation's first entry, recording the latest confirmed anchor.</summary>
    public async Task<EvidenceEntry> AppendFirstAsync(Guid conversationId, string kind, JsonObject payload, CancellationToken ct = default) =>
        await log.AppendAsync(conversationId, kind, payload, await LatestConfirmedAnchorAsync(ct), ct);

    /// <summary>
    /// If the conversation was anchored one last time and dropped for being idle, appends a <c>resume</c> entry that
    /// records the latest confirmed anchor and returns it; otherwise returns null and writes nothing.
    /// </summary>
    public async Task<EvidenceEntry?> ResumeIfDroppedAsync(Guid conversationId, JsonObject? payload = null, CancellationToken ct = default)
    {
        var head = await db.EvidenceEntries.AsNoTracking().Where(e => e.ConversationId == conversationId)
            .OrderByDescending(e => e.Seq).Select(e => new { e.Seq, e.Kind, e.SignedAt }).FirstOrDefaultAsync(ct);
        if (head is null || head.Kind == AnchorService.EndKind) return null;
        if (_time.GetUtcNow() - head.SignedAt < options.Value.IdleDrop) return null;
        var lastAnchored = await db.EvidenceAnchorLeaves.AsNoTracking().Where(l => l.ConversationId == conversationId)
            .MaxAsync(l => (long?)l.AnchoredSeq, ct);
        if (lastAnchored != head.Seq) return null; // not yet anchored one last time: the next tick still covers it
        return await log.AppendAsync(conversationId, ResumeKind, payload ?? new JsonObject(), await LatestConfirmedAnchorAsync(ct), ct);
    }

    /// <summary>Appends the end marker (after a resume entry when the conversation was dropped); the next tick anchors it.</summary>
    public async Task<EvidenceEntry> EndAsync(Guid conversationId, JsonObject? payload = null, CancellationToken ct = default)
    {
        await ResumeIfDroppedAsync(conversationId, null, ct);
        return await log.AppendAsync(conversationId, AnchorService.EndKind, payload ?? new JsonObject(), null, ct);
    }
}
