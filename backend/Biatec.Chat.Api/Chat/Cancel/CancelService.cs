using System.Text.Json.Nodes;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Api.Chat.Cancel;

public enum CancelOutcome
{
    /// <summary>The preview and its open steps are now Cancelled.</summary>
    Cancelled,
    /// <summary>It was already Cancelled; nothing changed.</summary>
    AlreadyCancelled,
    /// <summary>No such preview.</summary>
    NotFound,
    /// <summary>The preview belongs to another conversation.</summary>
    Foreign,
    /// <summary>The group hash is not that of the stored step.</summary>
    HashMismatch,
    /// <summary>Completed, failed, expired, or a signature is already in flight or on chain.</summary>
    NotCancellable,
}

public sealed record CancelResult(CancelOutcome Outcome, string Message, string? Status = null);

/// <summary>
/// The user's Cancel of an open action preview (spec, User Experience 5 and 8). Cancelling never needs permission, so
/// agent mode and pause are deliberately not consulted. It never touches a step that is already signing or submitted.
/// </summary>
public sealed class CancelService(ChatDbContext db, TimeProvider clock, ILogger<CancelService> log, IEvidenceRecorder? recorder = null)
{
    public const string EvidenceKind = "preview_cancelled";

    public async Task<CancelResult> CancelAsync(ChatSession session, Guid previewId, string groupHash, CancellationToken ct)
    {
        var preview = await db.ActionPreviews.AsNoTracking().FirstOrDefaultAsync(p => p.Id == previewId, ct);
        if (preview is null) return new CancelResult(CancelOutcome.NotFound, "Preview not found.");
        if (preview.ConversationId != session.ConversationId) return new CancelResult(CancelOutcome.Foreign, "Not your preview.");

        var steps = await db.ActionSteps.AsNoTracking().Where(s => s.PreviewId == previewId).OrderBy(s => s.Seq).ToListAsync(ct);
        var open = steps.Where(s => s.Status is StepStatus.Pending or StepStatus.AwaitingConsent).ToList();

        // The hash must be that of the step the user was looking at: the open one, or any stored step once none is open.
        var wanted = groupHash.Trim().ToLowerInvariant();
        if (!(open.Count > 0 ? open : steps).Any(s => string.Equals(s.SubjectHash, wanted, StringComparison.Ordinal)))
            return new CancelResult(CancelOutcome.HashMismatch, "The hash is not the hash of the pending group.");

        if (preview.Status == ActionPreviewStatus.Cancelled)
            return new CancelResult(CancelOutcome.AlreadyCancelled, "The preview was already cancelled.", "cancelled");
        if (preview.Status is ActionPreviewStatus.Completed or ActionPreviewStatus.Failed or ActionPreviewStatus.Expired)
            return new CancelResult(CancelOutcome.NotCancellable, $"The preview is {preview.Status}.");
        if (open.Count == 0 || steps.Any(s => s.Status is StepStatus.Signed or StepStatus.Submitted))
            return new CancelResult(CancelOutcome.NotCancellable, "The preview has no step that can still be cancelled.");
        var now = clock.GetUtcNow();
        if (open.All(s => s.ExpiresAt <= now))
            return new CancelResult(CancelOutcome.NotCancellable, "The preview has expired.");
        // Confirmed with a Pending step means signing is in flight right now; only a step awaiting consent can be stopped.
        if (preview.Status == ActionPreviewStatus.Confirmed && open.Any(s => s.Status == StepStatus.Pending))
            return new CancelResult(CancelOutcome.NotCancellable, "Signing is already in progress.");

        // An approved consent is being redeemed (signing may already be under way on the resumed path): too late to cancel.
        var awaitingIds = open.Where(s => s.Status == StepStatus.AwaitingConsent).Select(s => s.Id).ToList();
        if (awaitingIds.Count > 0 && await db.ConsentRequests.AnyAsync(c => awaitingIds.Contains(c.StepId) && c.Status == ConsentStatus.Approved, ct))
            return new CancelResult(CancelOutcome.NotCancellable, "The approval was already given; signing is in progress.");

        // A step awaiting consent is claimed by the resume path with the same conditional update: whoever flips the row first
        // wins, so a cancel that returns 200 can never be followed by a signature (BCHAT-002).
        if (awaitingIds.Count > 0)
        {
            var won = await db.ActionSteps.Where(s => awaitingIds.Contains(s.Id) && s.Status == StepStatus.AwaitingConsent)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, StepStatus.Cancelled), ct);
            if (won != awaitingIds.Count)
                return new CancelResult(CancelOutcome.NotCancellable, "The approval was already given; signing is in progress.");
        }

        // Atomic: only one of a cancel and a concurrent confirm wins the status flip.
        var observed = preview.Status;
        var flipped = await db.ActionPreviews
            .Where(p => p.Id == previewId && p.Status == observed)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, ActionPreviewStatus.Cancelled), ct);
        if (flipped == 0) return new CancelResult(CancelOutcome.NotCancellable, "The preview changed; please look at it again.");

        var openIds = open.Select(s => s.Id).ToList();
        await db.ActionSteps.Where(s => openIds.Contains(s.Id) && (s.Status == StepStatus.Pending || s.Status == StepStatus.AwaitingConsent))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, StepStatus.Cancelled), ct);
        var consents = await db.ConsentRequests
            .Where(c => openIds.Contains(c.StepId) && c.Status == ConsentStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, ConsentStatus.Cancelled), ct);

        if (recorder is not null)
        {
            try
            {
                await recorder.RecordAsync(session.ConversationId, EvidenceKind, new JsonObject
                {
                    ["previewId"] = previewId.ToString(), ["capability"] = preview.Capability,
                    ["stepIds"] = new JsonArray(openIds.Select(i => (JsonNode?)JsonValue.Create(i.ToString())).ToArray()),
                    ["groupHash"] = wanted, ["consentCancelled"] = consents > 0,
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError("Could not record evidence {Kind}: {ErrorType}", EvidenceKind, ex.GetType().Name);
            }
        }
        return new CancelResult(CancelOutcome.Cancelled, "The action was cancelled. Nothing was signed.", "cancelled");
    }
}
