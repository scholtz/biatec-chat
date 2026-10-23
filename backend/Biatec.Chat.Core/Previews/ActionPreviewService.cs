using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Core.Previews;

/// <summary>
/// Shared preview machinery for every capability: persists previews and steps bound to a group hash, enforces the
/// expiry and the 600 second Confirm gate, rebuilds expired previews as new linked previews, and sequences
/// multi-step actions so a later step is only built after the earlier one has landed.
/// </summary>
public sealed class ActionPreviewService(ChatDbContext db, TimeProvider clock, IEvidenceRecorder? recorder = null)
{
    private Task RecordAsync(Guid conversationId, string kind, System.Text.Json.Nodes.JsonObject payload, CancellationToken ct) =>
        recorder is null ? Task.CompletedTask : recorder.RecordAsync(conversationId, kind, payload, ct);

    /// <summary>Landed means confirmed on chain; a submitted step's outcome is still unknown.</summary>
    private static bool IsLanded(StepStatus s) => s == StepStatus.Confirmed;

    private void RequireFuture(IEnumerable<NewStepSpec> specs)
    {
        var now = clock.GetUtcNow();
        if (specs.Any(s => s.ExpiresAt <= now))
            throw new PreviewException(PreviewError.InvalidSpec, "A step must expire in the future.");
    }

    public async Task<PreviewSnapshot> CreateAsync(NewPreviewSpec spec, CancellationToken ct = default)
    {
        if (spec.Steps.Count == 0) throw new PreviewException(PreviewError.InvalidSpec, "A preview needs at least one step.");
        RequireFuture(spec.Steps);
        // Erasure serializes with this on the per-conversation lock: a turn that was already in flight when the user erased their data
        // creates no preview (its canonical JSON and unsigned group would be readable personal data) (BCHAT-013).
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await ConversationLock.AcquireAsync(db, spec.ConversationId, ct);
        if (await db.Conversations.AsNoTracking().AnyAsync(c => c.Id == spec.ConversationId && c.ErasedAt != null, ct))
            throw new PreviewException(PreviewError.InvalidState, "The conversation was erased, so no preview is created.");
        var preview = new ActionPreview
        {
            Id = Guid.NewGuid(),
            ConversationId = spec.ConversationId,
            Capability = spec.Capability,
            CanonicalJson = spec.CanonicalJson,
            PredictedValueEur = spec.PredictedValueEur,
            PredictedNeedsConsent = spec.PredictedNeedsConsent,
            Status = ActionPreviewStatus.Proposed,
        };
        var steps = spec.Steps.Select((s, i) => NewStep(preview.Id, i, s)).ToList();
        preview.ExpiresAt = steps.Min(s => s.ExpiresAt);
        db.ActionPreviews.Add(preview);
        db.ActionSteps.AddRange(steps);
        await db.SaveChangesAsync(ct);
        await RecordAsync(preview.ConversationId, EvidenceKinds.ActionPreview, PreviewEvidence.Preview(preview, steps), ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return Snapshot(preview, steps, null);
    }

    public async Task<PreviewSnapshot> GetAsync(Guid conversationId, Guid previewId, DateTimeOffset? sessionAbsoluteExpiry = null, CancellationToken ct = default)
    {
        var (preview, steps) = await LoadAsync(conversationId, previewId, ct);
        return Snapshot(preview, steps, sessionAbsoluteExpiry);
    }

    /// <summary>
    /// The user's Confirm. Refused when the preview is not awaiting confirmation, when <paramref name="groupHash"/>
    /// is not the hash of the step being confirmed, when the group has expired (the preview is then marked
    /// Expired and must be rebuilt) and when under 600 seconds remain.
    /// </summary>
    public async Task<PreviewSnapshot> ConfirmAsync(Guid conversationId, Guid previewId, string groupHash, DateTimeOffset? sessionAbsoluteExpiry = null, CancellationToken ct = default)
    {
        var (preview, steps) = await LoadAsync(conversationId, previewId, ct);
        if (preview.Status == ActionPreviewStatus.Expired)
            throw new PreviewException(PreviewError.Expired, "The preview has expired; it must be rebuilt.");
        if (preview.Status != ActionPreviewStatus.Proposed)
            throw new PreviewException(PreviewError.InvalidState, $"The preview is {preview.Status}, not awaiting confirmation.");

        var current = steps.FirstOrDefault(s => s.Status == StepStatus.Pending)
            ?? throw new PreviewException(PreviewError.InvalidState, "The preview has no pending step.");
        var now = clock.GetUtcNow();
        if (now >= current.ExpiresAt)
        {
            preview.Status = ActionPreviewStatus.Expired;
            foreach (var s in steps.Where(s => s.Status == StepStatus.Pending)) s.Status = StepStatus.Expired;
            await db.SaveChangesAsync(ct);
            throw new PreviewException(PreviewError.Expired, "The group has expired; the preview must be rebuilt.");
        }

        if (!string.Equals(current.SubjectHash, groupHash?.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            throw new PreviewException(PreviewError.HashMismatch, "The confirmed hash is not the hash of the pending group.");

        var remaining = PreviewPolicy.RemainingSeconds(current.ExpiresAt, now, sessionAbsoluteExpiry);
        if (!PreviewPolicy.CanConfirm(remaining))
            throw new PreviewException(PreviewError.TooCloseToExpiry,
                $"Only {remaining} s remain; at least {PreviewPolicy.MinConfirmSeconds} s are needed. Rebuild the preview.");

        // Atomic: of two concurrent Confirms only one flips Proposed to Confirmed.
        var flipped = await db.ActionPreviews
            .Where(p => p.Id == preview.Id && p.Status == ActionPreviewStatus.Proposed)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, ActionPreviewStatus.Confirmed), ct);
        if (flipped == 0) throw new PreviewException(PreviewError.InvalidState, "The preview was already confirmed or changed.");
        var status = db.Entry(preview).Property(p => p.Status);
        status.CurrentValue = ActionPreviewStatus.Confirmed;
        status.OriginalValue = ActionPreviewStatus.Confirmed; // already persisted by the bulk update
        return Snapshot(preview, steps, sessionAbsoluteExpiry);
    }

    /// <summary>
    /// Records that a step has landed on chain, with its transaction IDs. The preview must have been confirmed;
    /// <paramref name="isFinalStep"/> completes it.
    /// </summary>
    public async Task MarkStepLandedAsync(Guid conversationId, Guid previewId, int seq, IEnumerable<string> txIds, bool isFinalStep = false, CancellationToken ct = default)
    {
        var ids = txIds.ToArray();
        if (ids.Length == 0 || ids.Any(string.IsNullOrWhiteSpace))
            throw new PreviewException(PreviewError.InvalidSpec, "A landed step needs its transaction IDs.");
        var (preview, steps) = await LoadAsync(conversationId, previewId, ct);
        if (preview.Status is not (ActionPreviewStatus.Confirmed or ActionPreviewStatus.InProgress))
            throw new PreviewException(PreviewError.InvalidState, $"A {preview.Status} preview has no step in flight.");
        var step = steps.FirstOrDefault(s => s.Seq == seq)
            ?? throw new PreviewException(PreviewError.NotFound, $"Step {seq} not found.");
        if (step.Status is StepStatus.Confirmed or StepStatus.Expired or StepStatus.Cancelled or StepStatus.Failed)
            throw new PreviewException(PreviewError.InvalidState, $"Step {seq} is {step.Status}.");
        if (steps.Any(x => x.Seq < seq && !IsLanded(x.Status)))
            throw new PreviewException(PreviewError.InvalidState, "An earlier step has not landed.");
        if (isFinalStep && steps.Any(x => x.Seq != seq && !IsLanded(x.Status)))
            throw new PreviewException(PreviewError.InvalidState, "Other steps have not landed, so this is not the final step.");
        step.Status = StepStatus.Confirmed;
        step.TxIds = ids;
        // Between steps the preview stays InProgress; the last step completes it.
        preview.Status = isFinalStep ? ActionPreviewStatus.Completed : ActionPreviewStatus.InProgress;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Builds the next step of a multi-step action. Every earlier step must have landed; the new step brings its
    /// own validity window and the preview's expiry becomes that step's expiry.
    /// </summary>
    public async Task<PreviewSnapshot> AddNextStepAsync(Guid conversationId, Guid previewId, NewStepSpec spec, CancellationToken ct = default)
    {
        RequireFuture([spec]);
        var (preview, steps) = await LoadAsync(conversationId, previewId, ct);
        if (preview.Status is ActionPreviewStatus.Cancelled or ActionPreviewStatus.Failed or ActionPreviewStatus.Expired or ActionPreviewStatus.Completed)
            throw new PreviewException(PreviewError.InvalidState, $"The preview is {preview.Status}.");
        if (steps.Any(s => !IsLanded(s.Status)))
            throw new PreviewException(PreviewError.PreviousStepNotLanded, "A later step is built only after the previous one has landed.");

        var step = NewStep(preview.Id, steps.Count == 0 ? 0 : steps.Max(s => s.Seq) + 1, spec);
        db.ActionSteps.Add(step);
        steps.Add(step);
        preview.ExpiresAt = step.ExpiresAt;
        preview.Status = ActionPreviewStatus.Proposed;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            db.Entry(step).State = EntityState.Detached;
            await db.Entry(preview).ReloadAsync(ct);
            // The (PreviewId, Seq) unique index caught a concurrent AddNextStep.
            throw new PreviewException(PreviewError.InvalidState, "The next step was added concurrently.");
        }
        await RecordAsync(preview.ConversationId, EvidenceKinds.ActionStepAdded, new System.Text.Json.Nodes.JsonObject
        {
            ["previewId"] = preview.Id.ToString(), ["step"] = PreviewEvidence.Step(step),
        }, ct);
        return Snapshot(preview, steps, null);
    }

    /// <summary>
    /// Replaces an expired (or about to expire) preview with a new one linked through
    /// <c>RebuiltAsPreviewId</c>. Steps that already landed are copied over as completed, with their transaction
    /// IDs, and are never rebuilt; <paramref name="newSteps"/> are the remaining steps and must carry new hashes.
    /// </summary>
    public async Task<PreviewSnapshot> RebuildAsync(Guid conversationId, Guid previewId, string canonicalJson, IReadOnlyList<NewStepSpec> newSteps,
        decimal? predictedValueEur = null, bool predictedNeedsConsent = false, CancellationToken ct = default)
    {
        if (newSteps.Count == 0) throw new PreviewException(PreviewError.InvalidSpec, "A rebuilt preview needs at least one step.");
        RequireFuture(newSteps);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (old, oldSteps) = await LoadAsync(conversationId, previewId, ct);
        if (old.RebuiltAsPreviewId is not null)
            throw new PreviewException(PreviewError.AlreadyRebuilt, "The preview was already rebuilt.");
        if (old.Status is not (ActionPreviewStatus.Proposed or ActionPreviewStatus.Expired))
            throw new PreviewException(PreviewError.InvalidState, $"A {old.Status} preview cannot be rebuilt; a confirmed one may already be signing.");

        if (oldSteps.Any(x => x.Status is StepStatus.Submitted or StepStatus.Signed or StepStatus.AwaitingConsent))
            throw new PreviewException(PreviewError.InvalidState, "A step is in flight; its outcome must be known before rebuilding.");
        var oldPending = oldSteps.Where(s => !IsLanded(s.Status)).Select(s => s.SubjectHash).ToHashSet(StringComparer.Ordinal);
        if (newSteps.Any(s => oldPending.Contains(s.SubjectHash)))
            throw new PreviewException(PreviewError.RebuildReusedHash, "A rebuilt group must have a new hash.");

        var created = new ActionPreview
        {
            Id = Guid.NewGuid(),
            ConversationId = old.ConversationId,
            Capability = old.Capability,
            CanonicalJson = canonicalJson,
            PredictedValueEur = predictedValueEur,
            PredictedNeedsConsent = predictedNeedsConsent,
            Status = ActionPreviewStatus.Proposed,
        };
        var steps = new List<ActionStep>();
        foreach (var landed in oldSteps.Where(s => IsLanded(s.Status)).OrderBy(s => s.Seq))
        {
            steps.Add(new ActionStep
            {
                Id = Guid.NewGuid(), PreviewId = created.Id, Seq = steps.Count, Kind = landed.Kind,
                SubjectHash = landed.SubjectHash, UnsignedGroup = landed.UnsignedGroup, LimitsRequest = landed.LimitsRequest,
                SenderAddress = landed.SenderAddress, Network = landed.Network, FirstValid = landed.FirstValid,
                LastValid = landed.LastValid, ExpiresAt = landed.ExpiresAt, Status = landed.Status, TxIds = landed.TxIds,
            });
        }
        foreach (var spec in newSteps) steps.Add(NewStep(created.Id, steps.Count, spec));
        created.ExpiresAt = steps.Where(s => s.Status == StepStatus.Pending).Min(s => s.ExpiresAt);
        db.ActionPreviews.Add(created);
        db.ActionSteps.AddRange(steps);
        try
        {
            await db.SaveChangesAsync(ct);
            await ClaimOldAsync();
        }
        catch
        {
            Detach(created, steps);
            throw;
        }
        await tx.CommitAsync(ct);
        Detach(old, oldSteps); // the bulk updates bypassed the tracker; do not leave stale entities behind
        await RecordAsync(created.ConversationId, EvidenceKinds.ActionPreview, PreviewEvidence.Preview(created, steps, old.Id), ct);
        return Snapshot(created, steps, null);

        async Task ClaimOldAsync()
        {
        // Claim the old preview atomically so two concurrent rebuilds cannot both succeed.
        var claimed = await db.ActionPreviews
            .Where(p => p.Id == old.Id && p.RebuiltAsPreviewId == null
                        && (p.Status == ActionPreviewStatus.Proposed || p.Status == ActionPreviewStatus.Expired))
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.RebuiltAsPreviewId, created.Id)
                                      .SetProperty(p => p.Status, ActionPreviewStatus.Expired), ct);
        if (claimed == 0) throw new PreviewException(PreviewError.AlreadyRebuilt, "The preview was already rebuilt.");
        await db.ActionSteps
            .Where(s => s.PreviewId == old.Id && s.Status == StepStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, StepStatus.Expired), ct);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException?.GetType().GetProperty("SqlState")?.GetValue(e.InnerException) as string == "23505";

    /// <summary>
    /// Marks a preview whose group window has passed as Expired (atomic; only Proposed or Confirmed previews).
    /// The signing step calls this when it finds a confirmed group past its expiry, so the preview is rebuilt.
    /// </summary>
    public async Task<bool> ExpireIfLapsedAsync(Guid conversationId, Guid previewId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var n = await db.ActionPreviews
            .Where(p => p.Id == previewId && p.ConversationId == conversationId && p.ExpiresAt <= now
                        && (p.Status == ActionPreviewStatus.Proposed || p.Status == ActionPreviewStatus.Confirmed)
                        // A step already signing or submitted may still land; its outcome must be recorded, not expired.
                        && !db.ActionSteps.Any(x => x.PreviewId == p.Id
                            && (x.Status == StepStatus.Signed || x.Status == StepStatus.Submitted || x.Status == StepStatus.AwaitingConsent)))
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, ActionPreviewStatus.Expired), ct);
        if (n == 0) return false;
        await db.ActionSteps.Where(x => x.PreviewId == previewId && x.Status == StepStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, StepStatus.Expired), ct);
        // The bulk updates bypassed the tracker; drop this preview's stale tracked copies.
        foreach (var e in db.ChangeTracker.Entries<ActionPreview>().Where(e => e.Entity.Id == previewId).ToList()) e.State = EntityState.Detached;
        foreach (var e in db.ChangeTracker.Entries<ActionStep>().Where(e => e.Entity.PreviewId == previewId).ToList()) e.State = EntityState.Detached;
        return true;
    }

    private void Detach(ActionPreview p, IEnumerable<ActionStep> steps)
    {
        foreach (var s in steps) db.Entry(s).State = EntityState.Detached;
        db.Entry(p).State = EntityState.Detached;
    }

    private async Task<(ActionPreview, List<ActionStep>)> LoadAsync(Guid conversationId, Guid previewId, CancellationToken ct)
    {
        var preview = await db.ActionPreviews.SingleOrDefaultAsync(p => p.Id == previewId && p.ConversationId == conversationId, ct)
            ?? throw new PreviewException(PreviewError.NotFound, "Preview not found.");
        var steps = await db.ActionSteps.Where(s => s.PreviewId == previewId).OrderBy(s => s.Seq).ToListAsync(ct);
        return (preview, steps);
    }

    private static ActionStep NewStep(Guid previewId, int seq, NewStepSpec s) => new()
    {
        Id = Guid.NewGuid(), PreviewId = previewId, Seq = seq, Kind = s.Kind, SubjectHash = s.SubjectHash,
        UnsignedGroup = s.UnsignedGroup, LimitsRequest = s.LimitsRequest, SenderAddress = s.SenderAddress,
        Network = s.Network, FirstValid = s.FirstValid, LastValid = s.LastValid, ExpiresAt = s.ExpiresAt,
        Status = StepStatus.Pending,
    };

    private PreviewSnapshot Snapshot(ActionPreview preview, IReadOnlyList<ActionStep> steps, DateTimeOffset? sessionAbsoluteExpiry)
    {
        var now = clock.GetUtcNow();
        var remaining = PreviewPolicy.RemainingSeconds(preview.ExpiresAt, now, sessionAbsoluteExpiry);
        string? reason = null;
        if (preview.Status == ActionPreviewStatus.Expired) reason = "expired";
        else if (preview.Status != ActionPreviewStatus.Proposed) reason = "not-awaiting-confirmation";
        else if (remaining == 0) reason = "expired";
        else if (!PreviewPolicy.CanConfirm(remaining)) reason = "too-close-to-expiry";
        return new PreviewSnapshot(preview, steps, new PreviewCountdown(remaining, reason is null, reason));
    }
}
