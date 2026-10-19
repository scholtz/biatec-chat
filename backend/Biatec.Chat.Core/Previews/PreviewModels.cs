using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Core.Previews;

/// <summary>A step to persist. <c>SubjectHash</c> is the group hash (lowercase hex) for a sign step.</summary>
public sealed record NewStepSpec(
    ActionStepKind Kind,
    string SubjectHash,
    DateTimeOffset ExpiresAt,
    string? UnsignedGroup = null,
    string? LimitsRequest = null,
    string? SenderAddress = null,
    string? Network = null,
    long? FirstValid = null,
    long? LastValid = null);

/// <summary>
/// A preview to persist. <c>Steps</c> holds only the steps that are built now; in a multi-step action later steps
/// are added with <see cref="ActionPreviewService.AddNextStepAsync"/> once the earlier one has landed.
/// </summary>
public sealed record NewPreviewSpec(
    Guid ConversationId,
    string Capability,
    string CanonicalJson,
    IReadOnlyList<NewStepSpec> Steps,
    decimal? PredictedValueEur = null,
    bool PredictedNeedsConsent = false);

/// <summary>Countdown fields the widget renders.</summary>
public sealed record PreviewCountdown(int RemainingSeconds, bool CanConfirm, string? DisabledReason);

public sealed record PreviewSnapshot(ActionPreview Preview, IReadOnlyList<ActionStep> Steps, PreviewCountdown Countdown);

public enum PreviewError
{
    NotFound,
    InvalidState,
    Expired,
    TooCloseToExpiry,
    HashMismatch,
    PreviousStepNotLanded,
    AlreadyRebuilt,
    RebuildReusedHash,
    InvalidSpec,
}

public sealed class PreviewException(PreviewError error, string message) : Exception(message)
{
    public PreviewError Error { get; } = error;
}
