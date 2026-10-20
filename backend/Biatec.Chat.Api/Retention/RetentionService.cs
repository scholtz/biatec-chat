using System.Diagnostics;
using System.Linq.Expressions;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Retention;

public sealed record RetentionResult(int PayloadsDeleted, int TranscriptsCleared, int AnonymousRemoved, int RecordsRemoved);

public enum ErasureOutcome { Erased, ActionInFlight }

/// <param name="NewConversationId">The calling session's fresh conversation after erasure (null when it had none to move).</param>
public sealed record ErasureResult(ErasureOutcome Outcome, int Conversations, int PayloadsDeleted, Guid? NewConversationId = null);

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
/// <para>
/// Every rule works in batches of <see cref="RetentionOptions.BatchSize"/> rows or conversations, and keeps draining
/// until nothing is left or <see cref="RetentionOptions.RunBudget"/> is spent; the next run picks up the rest.
/// </para>
/// </summary>
public sealed class RetentionService(ChatDbContext db, IOptions<RetentionOptions> options, TimeProvider clock)
{
    /// <summary>Written over <c>ActionPreview.canonicalJson</c> (a non-null jsonb column) when it is blanked.</summary>
    public const string BlankCanonicalJson = "{}";

    private static readonly ActionPreviewStatus[] OpenPreview = [ActionPreviewStatus.Proposed, ActionPreviewStatus.Confirmed, ActionPreviewStatus.InProgress];
    private static readonly StepStatus[] OpenStep = [StepStatus.Pending, StepStatus.AwaitingConsent, StepStatus.Signed, StepStatus.Submitted];

    private RetentionOptions O => options.Value;
    private int Batch => Math.Max(1, O.BatchSize);

    /// <summary>
    /// In flight: a proposed preview the user can still confirm, an open preview with a step still to sign, awaiting
    /// consent, signed or submitted and not past its own expiry, or any step with an open ConsentRequest. Mirrors the
    /// in-flight test of <see cref="SessionLiveness"/> (T-080), including its grace, and does not trust the preview's
    /// own ExpiresAt (the earliest step expiry) for a multi-step action.
    /// </summary>
    private Expression<Func<ActionPreview, bool>> InFlight(DateTimeOffset now)
    {
        var cutoff = now - SessionPolicy.InFlightGrace;
        return p => (p.Status == ActionPreviewStatus.Proposed && p.ExpiresAt > now)
                    || (OpenPreview.Contains(p.Status) && db.ActionSteps.Any(st => st.PreviewId == p.Id && OpenStep.Contains(st.Status) && st.ExpiresAt > cutoff))
                    || db.ActionSteps.Any(st => st.PreviewId == p.Id
                                                && db.ConsentRequests.Any(c => c.StepId == st.Id && c.Status == ConsentStatus.Pending && c.ExpiresAt > cutoff));
    }

    private Expression<Func<ActionPreview, bool>> NotInFlight(DateTimeOffset now)
    {
        var e = InFlight(now);
        return Expression.Lambda<Func<ActionPreview, bool>>(Expression.Not(e.Body), e.Parameters);
    }

    /// <summary>One run of every retention rule, in the order payloads, transcripts, anonymous, records.</summary>
    public async Task<RetentionResult> ApplyAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var started = Stopwatch.GetTimestamp();
        bool Spent() => Stopwatch.GetElapsedTime(started) >= O.RunBudget;

        // 1. Payloads and salts of entries older than the payload window. Hashes, chain and signatures stay. The admin
        //    chain is exempt: it holds operator names, reasons and tenant policy values, not end-user personal data, and
        //    is kept whole for the record window (rule 4 removes it as an orphan chain once its newest entry is past it).
        var payloadCutoff = now - O.Payloads;
        var adminChain = Biatec.Chat.Api.Admin.AdminEvidence.ChainId;
        var payloads = await DrainAsync(async () =>
        {
            var ids = await db.EvidenceEntries.Where(e => e.SignedAt < payloadCutoff && (e.Payload != null || e.Salt != null) && e.ConversationId != adminChain)
                .OrderBy(e => e.Id).Select(e => e.Id).Take(Batch).ToListAsync(ct);
            if (ids.Count > 0)
                await db.EvidenceEntries.Where(e => ids.Contains(e.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.Payload, (string?)null).SetProperty(e => e.Salt, (string?)null), ct);
            return (ids.Count, ids.Count);
        }, Spent);

        // 2. Transcripts, each row by its own age: messages by CreatedAt, previews and steps by their own expiry.
        var transcripts = await DrainAsync(async () =>
        {
            var convs = await db.Messages.Where(m => m.CreatedAt < payloadCutoff && (m.Content != "" || m.ToolCalls != null || m.ToolResults != null))
                .Select(m => m.ConversationId).Distinct().Take(Batch).ToListAsync(ct);
            var n = convs.Count == 0 ? 0 : await db.Messages
                .Where(m => convs.Contains(m.ConversationId) && m.CreatedAt < payloadCutoff && (m.Content != "" || m.ToolCalls != null || m.ToolResults != null))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Content, "").SetProperty(m => m.ToolCalls, (string?)null).SetProperty(m => m.ToolResults, (string?)null), ct);
            return (convs.Count, n);
        }, Spent);
        await DrainAsync(async () =>
        {
            var ids = await db.ActionPreviews.Where(p => p.ExpiresAt < payloadCutoff && p.CanonicalJson != BlankCanonicalJson)
                .Where(NotInFlight(now)).OrderBy(p => p.Id).Select(p => p.Id).Take(Batch).ToListAsync(ct);
            if (ids.Count > 0)
                await db.ActionPreviews.Where(p => ids.Contains(p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.CanonicalJson, BlankCanonicalJson), ct);
            return (ids.Count, ids.Count);
        }, Spent);
        await DrainAsync(async () =>
        {
            // A step past its own expiry (by 90 days) can no longer be signed, whatever its preview's other steps do.
            var ids = await db.ActionSteps.Where(st => st.ExpiresAt < payloadCutoff && (st.UnsignedGroup != null || st.LimitsRequest != null || st.SenderAddress != null))
                .OrderBy(st => st.Id).Select(st => st.Id).Take(Batch).ToListAsync(ct);
            if (ids.Count > 0) await BlankStepsAsync(db.ActionSteps.Where(st => ids.Contains(st.Id)), ct);
            return (ids.Count, ids.Count);
        }, Spent);

        // 3. Anonymous conversations: no oidcSub, never erased (an erased signed-in conversation is not anonymous),
        //    no action previews and no session left. The conversation and its rows go; the chain stays, without
        //    payloads, until the record window.
        var anonymousCutoff = now - O.AnonymousConversations;
        var anonymous = await DrainAsync(async () =>
        {
            var ids = await db.Conversations
                .Where(c => c.OidcSub == null && c.ErasedAt == null && c.LastMessageAt < anonymousCutoff)
                .Where(c => !db.ActionPreviews.Any(p => p.ConversationId == c.Id))
                .Where(c => !db.ChatSessions.Any(s => s.ConversationId == c.Id))
                .OrderBy(c => c.LastMessageAt).Select(c => c.Id).Take(Batch).ToListAsync(ct);
            if (ids.Count == 0) return (0, 0);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await DeletePayloadsAsync(ids, ct);
            await db.PublicConversationStates.Where(p => ids.Contains(p.ConversationId)).ExecuteDeleteAsync(ct);
            await db.PopupAuthRequests.Where(p => ids.Contains(p.ConversationId)).ExecuteDeleteAsync(ct);
            // Cascades to messages.
            var n = await db.Conversations.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            return (ids.Count, n);
        }, Spent);

        // 4. Records past the record window: the whole conversation, its chain, receipts and tx ids, and anchor leaves.
        var recordCutoff = now - O.Records;
        var records = await DrainAsync(async () =>
        {
            var recordIds = await db.Conversations
                .Where(c => c.LastMessageAt < recordCutoff)
                .Where(c => !db.ChatSessions.Any(s => s.ConversationId == c.Id))
                .Where(c => !db.EvidenceEntries.Any(e => e.ConversationId == c.Id && e.SignedAt >= recordCutoff))
                .OrderBy(c => c.LastMessageAt).Select(c => c.Id).Take(Batch).ToListAsync(ct);
            // Chains whose conversation row is already gone (anonymous retention) and whose newest entry is past the
            // window. Only entries already past the window are scanned (SignedAt index).
            var orphanIds = await db.EvidenceEntries
                .Where(e => e.SignedAt < recordCutoff && !db.Conversations.Any(c => c.Id == e.ConversationId))
                .Select(e => e.ConversationId).Distinct()
                .Where(id => !db.EvidenceEntries.Any(x => x.ConversationId == id && x.SignedAt >= recordCutoff))
                .Take(Batch).ToListAsync(ct);
            var ids = recordIds.Concat(orphanIds).Distinct().ToList();
            if (ids.Count == 0) return (0, 0);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.EvidenceEntries.Where(e => ids.Contains(e.ConversationId)).ExecuteDeleteAsync(ct);
            await db.EvidenceAnchorLeaves.Where(l => ids.Contains(l.ConversationId)).ExecuteDeleteAsync(ct);
            await db.PublicConversationStates.Where(p => ids.Contains(p.ConversationId)).ExecuteDeleteAsync(ct);
            await db.PopupAuthRequests.Where(p => ids.Contains(p.ConversationId)).ExecuteDeleteAsync(ct);
            // Cascades to messages, previews, steps and consent requests (receipts).
            await db.Conversations.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            // A batch is full when either source filled it.
            return (Math.Max(recordIds.Count, orphanIds.Count), ids.Count);
        }, Spent);

        return new RetentionResult(payloads, transcripts, anonymous, records);
    }

    /// <summary>
    /// Runs <paramref name="batch"/> (which returns how many keys it picked and how many rows it changed) until a batch
    /// comes back short or the run budget is spent. Returns the rows changed.
    /// </summary>
    private async Task<int> DrainAsync(Func<Task<(int Picked, int Changed)>> batch, Func<bool> spent)
    {
        var total = 0;
        while (true)
        {
            var (picked, changed) = await batch();
            total += changed;
            if (picked < Batch || spent()) return total;
        }
    }

    /// <summary>
    /// Erasure on a signed-in user's request: every conversation of this user in this tenant loses its payloads and
    /// salts, message content, preview and step payload columns, and its <c>oidcSub</c>, and is marked erased, all in
    /// one transaction. The sessions still on those conversations move to fresh conversations (with the same
    /// <c>oidcSub</c>), so whatever they write afterwards can be erased again; the old conversations end.
    /// Refused while one of those conversations has an action in flight, so a fund-moving action is never broken
    /// mid-flight; the user cancels it or lets it expire first.
    /// </summary>
    public async Task<ErasureResult> EraseAsync(string tenantId, string oidcSub, Guid? callerSessionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(oidcSub);
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ids = await db.Conversations.Where(c => c.TenantId == tenantId && c.OidcSub == oidcSub).Select(c => c.Id).ToListAsync(ct);
        if (ids.Count == 0) return new ErasureResult(ErasureOutcome.Erased, 0, 0);

        if (await db.ActionPreviews.Where(p => ids.Contains(p.ConversationId)).AnyAsync(InFlight(now), ct))
            return new ErasureResult(ErasureOutcome.ActionInFlight, 0, 0);

        var payloads = await DeletePayloadsAsync(ids, ct);
        await db.Messages.Where(m => ids.Contains(m.ConversationId))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Content, "").SetProperty(m => m.ToolCalls, (string?)null).SetProperty(m => m.ToolResults, (string?)null), ct);
        await db.ActionPreviews.Where(p => ids.Contains(p.ConversationId))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CanonicalJson, BlankCanonicalJson), ct);
        await BlankStepsAsync(db.ActionSteps.Where(st => db.ActionPreviews.Any(p => p.Id == st.PreviewId && ids.Contains(p.ConversationId))), ct);
        await db.Conversations.Where(c => ids.Contains(c.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.OidcSub, (string?)null).SetProperty(c => c.ErasedAt, now), ct);

        // Move the live sessions off the erased conversations: one fresh conversation per old one.
        var sessions = await db.ChatSessions.Where(s => ids.Contains(s.ConversationId)).Select(s => new { s.Id, s.ConversationId }).ToListAsync(ct);
        var fresh = sessions.Select(s => s.ConversationId).Distinct().ToDictionary(c => c, _ => Guid.NewGuid());
        foreach (var id in fresh.Values)
            db.Conversations.Add(new Conversation { Id = id, TenantId = tenantId, OidcSub = oidcSub, CreatedAt = now, LastMessageAt = now });
        await db.SaveChangesAsync(ct);
        foreach (var (old, id) in fresh)
            await db.ChatSessions.Where(s => s.ConversationId == old).ExecuteUpdateAsync(s => s.SetProperty(x => x.ConversationId, id), ct);
        var moved = fresh.Keys.ToList();
        await db.Conversations.Where(c => moved.Contains(c.Id) && c.EndedAt == null).ExecuteUpdateAsync(s => s.SetProperty(c => c.EndedAt, now), ct);
        await tx.CommitAsync(ct);

        var callerOld = sessions.FirstOrDefault(s => s.Id == callerSessionId)?.ConversationId;
        return new ErasureResult(ErasureOutcome.Erased, ids.Count, payloads, callerOld is { } o ? fresh[o] : null);
    }

    private Task<int> DeletePayloadsAsync(List<Guid> ids, CancellationToken ct) =>
        db.EvidenceEntries.Where(e => ids.Contains(e.ConversationId) && (e.Payload != null || e.Salt != null))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Payload, (string?)null).SetProperty(e => e.Salt, (string?)null), ct);

    /// <summary>Blanks unsignedGroup, limitsRequest and senderAddress; subjectHash, txIds, status and expiry stay.</summary>
    private static Task<int> BlankStepsAsync(IQueryable<ActionStep> steps, CancellationToken ct) =>
        steps.ExecuteUpdateAsync(s => s.SetProperty(st => st.UnsignedGroup, (string?)null)
            .SetProperty(st => st.LimitsRequest, (string?)null).SetProperty(st => st.SenderAddress, (string?)null), ct);
}
