using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Retention;

public sealed record RetentionResult(int PayloadsDeleted, int TranscriptsCleared, int AnonymousRemoved, int RecordsRemoved);

public enum ErasureOutcome { Erased, ActionInFlight }

public sealed record ErasureResult(ErasureOutcome Outcome, int Conversations, int PayloadsDeleted);

/// <summary>
/// Retention and deletion as one rule (PRODUCT-DESCRIPTION.md, Session Recording And Evidence). What goes: evidence
/// payloads and salts, message content, tool calls and results, <c>ActionPreview.canonicalJson</c>,
/// <c>ActionStep.unsignedGroup</c>, <c>limitsRequest</c> and <c>senderAddress</c>, and on erasure the conversation's
/// <c>oidcSub</c>. What stays: every entry's payload hash, the chain and its signatures, group hashes
/// (<c>subjectHash</c>), <c>txIds</c> and consent receipts, so an exported bundle still verifies.
/// <para>
/// Never touched here: <c>PendingRevocation</c> (kept until revocation succeeds or the token expires; the revocation
/// service owns it), <c>ChatSession</c> rows (the sweeper ends them, revoking first; a conversation that still has one
/// is skipped), and <c>SafeApprovalCount</c> / <c>SoftCapSpend</c> (the T-025 pruner).
/// </para>
/// </summary>
public sealed class RetentionService(ChatDbContext db, IOptions<RetentionOptions> options, TimeProvider clock)
{
    /// <summary>Written over <c>ActionPreview.canonicalJson</c> (a non-null jsonb column) when it is blanked.</summary>
    public const string BlankCanonicalJson = "{}";

    // A preview in these states is still actionable: blanking its payload would break the confirm path mid-flight.
    private static readonly ActionPreviewStatus[] Actionable = [ActionPreviewStatus.Proposed, ActionPreviewStatus.Confirmed, ActionPreviewStatus.InProgress];

    private RetentionOptions O => options.Value;

    /// <summary>One run of every retention rule, in the order payloads, transcripts, anonymous, records.</summary>
    public async Task<RetentionResult> ApplyAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // 1. Payloads and salts of entries older than the payload window. Hashes, chain and signatures stay.
        var payloadCutoff = now - O.Payloads;
        var payloads = await db.EvidenceEntries
            .Where(e => e.SignedAt < payloadCutoff && (e.Payload != null || e.Salt != null))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Payload, (string?)null).SetProperty(e => e.Salt, (string?)null), ct);

        // 2. Transcripts (and the preview and step payload columns) of conversations idle past the payload window.
        var stale = db.Conversations.Where(c => c.LastMessageAt < payloadCutoff).Select(c => c.Id);
        var transcripts = await BlankAsync(stale, now, ct);

        // 3. Anonymous conversations: no oidcSub and no action previews (an erased signed-in conversation with a
        //    fund-moving action also has a null oidcSub, and its receipts and tx ids are kept for the record window).
        //    The conversation and its rows go; the chain stays, without payloads, until the record window.
        var anonymousCutoff = now - O.AnonymousConversations;
        var anonymous = 0;
        var anonIds = await db.Conversations
            .Where(c => c.OidcSub == null && c.LastMessageAt < anonymousCutoff)
            .Where(c => !db.ActionPreviews.Any(p => p.ConversationId == c.Id))
            .Where(c => !db.ChatSessions.Any(s => s.ConversationId == c.Id))
            .OrderBy(c => c.LastMessageAt).Select(c => c.Id).Take(O.BatchSize).ToListAsync(ct);
        if (anonIds.Count > 0)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await DeletePayloadsAsync(anonIds, ct);
            await db.PublicConversationStates.Where(p => anonIds.Contains(p.ConversationId)).ExecuteDeleteAsync(ct);
            await db.PopupAuthRequests.Where(p => anonIds.Contains(p.ConversationId)).ExecuteDeleteAsync(ct);
            // Cascades to messages, previews, steps and consent requests.
            anonymous = await db.Conversations.Where(c => anonIds.Contains(c.Id)).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }

        // 4. Records past the record window: the whole conversation, its chain, receipts and tx ids, and anchor leaves.
        var recordCutoff = now - O.Records;
        var records = 0;
        var recordIds = await db.Conversations
            .Where(c => c.LastMessageAt < recordCutoff)
            .Where(c => !db.ChatSessions.Any(s => s.ConversationId == c.Id))
            .Where(c => !db.EvidenceEntries.Any(e => e.ConversationId == c.Id && e.SignedAt >= recordCutoff))
            .OrderBy(c => c.LastMessageAt).Select(c => c.Id).Take(O.BatchSize).ToListAsync(ct);
        // Chains whose conversation row is already gone (anonymous retention) and whose newest entry is past the window.
        var orphanIds = await db.EvidenceEntries
            .Where(e => !db.Conversations.Any(c => c.Id == e.ConversationId))
            .GroupBy(e => e.ConversationId)
            .Where(g => g.Max(e => e.SignedAt) < recordCutoff)
            .Select(g => g.Key).Take(O.BatchSize).ToListAsync(ct);
        var ids = recordIds.Concat(orphanIds).Distinct().ToList();
        if (ids.Count > 0)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.EvidenceEntries.Where(e => ids.Contains(e.ConversationId)).ExecuteDeleteAsync(ct);
            await db.EvidenceAnchorLeaves.Where(l => ids.Contains(l.ConversationId)).ExecuteDeleteAsync(ct);
            await db.PublicConversationStates.Where(p => ids.Contains(p.ConversationId)).ExecuteDeleteAsync(ct);
            await db.PopupAuthRequests.Where(p => ids.Contains(p.ConversationId)).ExecuteDeleteAsync(ct);
            await db.Conversations.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync(ct);
            records = ids.Count;
            await tx.CommitAsync(ct);
        }

        return new RetentionResult(payloads, transcripts, anonymous, records);
    }

    /// <summary>
    /// Erasure on a signed-in user's request: every conversation of this user in this tenant loses its payloads and
    /// salts, message content, preview and step payload columns, and its <c>oidcSub</c>, all in one transaction.
    /// Refused while one of those conversations has an actionable preview (proposed, confirmed or in progress and not
    /// yet expired), so a fund-moving action is never broken mid-flight; the user cancels it or lets it expire first.
    /// </summary>
    public async Task<ErasureResult> EraseAsync(string tenantId, string oidcSub, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(oidcSub);
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ids = await db.Conversations.Where(c => c.TenantId == tenantId && c.OidcSub == oidcSub).Select(c => c.Id).ToListAsync(ct);
        if (ids.Count == 0) return new ErasureResult(ErasureOutcome.Erased, 0, 0);

        if (await db.ActionPreviews.AnyAsync(p => ids.Contains(p.ConversationId) && Actionable.Contains(p.Status) && p.ExpiresAt > now, ct))
            return new ErasureResult(ErasureOutcome.ActionInFlight, 0, 0);

        var payloads = await DeletePayloadsAsync(ids, ct);
        await BlankAsync(db.Conversations.Where(c => ids.Contains(c.Id)).Select(c => c.Id), now, ct);
        await db.Conversations.Where(c => ids.Contains(c.Id)).ExecuteUpdateAsync(s => s.SetProperty(c => c.OidcSub, (string?)null), ct);
        await tx.CommitAsync(ct);
        return new ErasureResult(ErasureOutcome.Erased, ids.Count, payloads);
    }

    private Task<int> DeletePayloadsAsync(List<Guid> ids, CancellationToken ct) =>
        db.EvidenceEntries.Where(e => ids.Contains(e.ConversationId) && (e.Payload != null || e.Salt != null))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Payload, (string?)null).SetProperty(e => e.Salt, (string?)null), ct);

    /// <summary>
    /// Clears message content, tool calls and tool results, and blanks the preview and step payload columns of the
    /// given conversations. Leaves subjectHash, txIds, consent receipts, statuses and expiries alone. Previews that are
    /// still actionable are skipped (the retention job only reaches them when they are long expired). Returns the
    /// number of messages cleared.
    /// </summary>
    private async Task<int> BlankAsync(IQueryable<Guid> conversationIds, DateTimeOffset now, CancellationToken ct)
    {
        var messages = await db.Messages
            .Where(m => conversationIds.Contains(m.ConversationId) && (m.Content != "" || m.ToolCalls != null || m.ToolResults != null))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Content, "").SetProperty(m => m.ToolCalls, (string?)null).SetProperty(m => m.ToolResults, (string?)null), ct);

        var previews = db.ActionPreviews.Where(p => conversationIds.Contains(p.ConversationId)
                                                   && !(Actionable.Contains(p.Status) && p.ExpiresAt > now));
        await previews.Where(p => p.CanonicalJson != BlankCanonicalJson)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CanonicalJson, BlankCanonicalJson), ct);
        await db.ActionSteps
            .Where(st => previews.Any(p => p.Id == st.PreviewId) && (st.UnsignedGroup != null || st.LimitsRequest != null || st.SenderAddress != null))
            .ExecuteUpdateAsync(s => s.SetProperty(st => st.UnsignedGroup, (string?)null)
                .SetProperty(st => st.LimitsRequest, (string?)null).SetProperty(st => st.SenderAddress, (string?)null), ct);
        return messages;
    }
}
