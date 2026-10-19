using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Api.Chat.Confirm;

public enum SettlementKind
{
    /// <summary>Settled through the handler; the step lands.</summary>
    Settled,
    /// <summary>Rejected before anything was broadcast; the preview must be rebuilt.</summary>
    NotBroadcast,
    /// <summary>Unknown outcome; the group may have been broadcast. The step stays submitted and is reconciled.</summary>
    Unknown,
}

public sealed record SettlementResult(SettlementKind Kind, string Message, string? Content = null, string? ErrorCode = null);

/// <summary>
/// Told when a step has landed, with the confirmation algod reported (for example the app id a deployment created).
/// Runs after the step is recorded; a failing observer never changes the confirm outcome.
/// </summary>
public interface IStepLandedObserver
{
    Task OnLandedAsync(string tenantId, ActionPreview preview, ActionStep step, Biatec.Chat.Tools.Signing.TxConfirmation confirmation, CancellationToken ct);
}

/// <summary>
/// A capability that does not broadcast the signed group through Biatec MCP but settles it itself (x402: the facilitator
/// settles, then the paid resource is fetched). Resolved by <see cref="ConfirmService"/> by preview capability.
/// </summary>
public interface ISettlementHandler
{
    string Capability { get; }
    Task<SettlementResult> SettleAsync(ActionPreview preview, ActionStep step, IReadOnlyList<string> signedGroupBase64, CancellationToken ct);
}
