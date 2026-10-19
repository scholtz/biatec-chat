using System.Text.Json.Serialization;

namespace Biatec.Chat.Api.Contracts;

/// <summary>Lifecycle of an action preview or of one of its steps.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActionPreviewStatus>))]
public enum ActionPreviewStatus
{
    AwaitingConfirmation,
    AwaitingScope,
    AwaitingApproval,
    Signing,
    Submitted,
    Completed,
    Cancelled,
    Expired,
    Failed,
}

/// <summary>An asset amount with enough information to render it and to apply policy.</summary>
public sealed record AssetAmount
{
    /// <summary>Asset identifier: ASA ID, "ALGO", or "BTC".</summary>
    public required string AssetId { get; init; }
    public required string Symbol { get; init; }
    /// <summary>Amount in base units, as a decimal string to avoid precision loss.</summary>
    public required string Amount { get; init; }
    public required int Decimals { get; init; }
    /// <summary>Fiat value used for policy decisions, if known.</summary>
    public string? FiatValue { get; init; }
    public string? FiatCurrency { get; init; }
}

/// <summary>One step of a (possibly multi-step) action. Each step has its own group hash.</summary>
public sealed record ActionStep
{
    public required string StepId { get; init; }
    public required int Index { get; init; }
    public required string Title { get; init; }
    public required ActionPreviewStatus Status { get; init; }
    /// <summary>Single binding value for the signing call of this step.</summary>
    public required string GroupHash { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public IReadOnlyList<AssetAmount> Assets { get; init; } = [];
    public string? FeeAmount { get; init; }
    public string? SlippagePercent { get; init; }
    /// <summary>Simulation or decoded-transaction summary, where available.</summary>
    public string? SimulationSummary { get; init; }
    /// <summary>Transaction ID once submitted.</summary>
    public string? TransactionId { get; init; }
    public string? ExplorerUrl { get; init; }
}

/// <summary>Spending-limit bucket the action counts against.</summary>
public sealed record LimitHeadroom
{
    public required string Bucket { get; init; }
    public required string Remaining { get; init; }
    public required string Currency { get; init; }
}

/// <summary>Structured preview rendered by the widget as a card. Never free model text.</summary>
public sealed record ActionPreview
{
    public required string PreviewId { get; init; }
    /// <summary>Capability name, e.g. wallet.transfer.</summary>
    public required string Capability { get; init; }
    /// <summary>Human-readable intent, e.g. "Swap 100 USDC for at least 91.80 EURS on Biatec DEX".</summary>
    public required string Intent { get; init; }
    /// <summary>Network name, e.g. algorand-mainnet.</summary>
    public required string Network { get; init; }
    /// <summary>Genesis ID (chain identifier for Bitcoin).</summary>
    public required string GenesisId { get; init; }
    public required ActionPreviewStatus Status { get; init; }
    public required IReadOnlyList<ActionStep> Steps { get; init; }
    public LimitHeadroom? Limit { get; init; }
    /// <summary>Predicted: Biatec OIDC still decides whether authenticator approval is needed.</summary>
    public bool ExpectsAuthenticatorApproval { get; init; }
    public string? ApprovalReason { get; init; }
    /// <summary>Group validity; the widget shows a countdown and disables Confirm when low.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
