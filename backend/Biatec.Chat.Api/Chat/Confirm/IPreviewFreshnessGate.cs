using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Api.Chat.Confirm;

/// <summary>
/// A capability-specific check run at Confirm, before the scope check, consent or signing. Returns a refusal message when
/// the preview has gone stale (the gate has already invalidated it), or null when it may proceed.
/// </summary>
public interface IPreviewFreshnessGate
{
    Task<string?> CheckAsync(ActionPreview preview, ActionStep pendingStep, CancellationToken ct);
}
