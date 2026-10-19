using System.Text.Json.Nodes;
using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Core.Capabilities;

/// <summary>How the value of an action is computed for the consent threshold.</summary>
public enum ValuationBasis
{
    /// <summary>Nothing moves; value is zero.</summary>
    None,
    /// <summary>Biatec OIDC decodes the value from the built transaction group, never the chat backend.</summary>
    DecodedByOidc,
    /// <summary>Valued at zero for the user's thresholds (safe execute, safe create, proposal only).</summary>
    ZeroValued,
    /// <summary>Biatec OIDC issues a step-up challenge whenever a limit is raised.</summary>
    LimitsChange,
}

/// <summary>Optional gates that hide a capability regardless of tier and scopes.</summary>
public enum CapabilityFlag
{
    None,
    /// <summary>Platform feature flag for x402.pay (off until after the hackathon).</summary>
    X402,
    /// <summary>Per-tenant flag (Tenant.limitsUpdateEnabled) for limits.update.</summary>
    LimitsUpdate,
}

/// <summary>
/// A named, versioned thing the assistant can do. <see cref="ToolName"/> is null for capabilities that have no
/// tool of their own (the explain.* capabilities are answered from model knowledge plus kb.search).
/// </summary>
public sealed record Capability(
    string Name,
    int Version,
    SessionTier MinTier,
    IReadOnlyList<string> BuildScopes,
    string? SignScope,
    bool MovesFunds,
    ValuationBasis Valuation,
    string Description,
    JsonObject? InputSchema,
    CapabilityFlag Flag = CapabilityFlag.None)
{
    /// <summary>The tool name sent to the model, or null if the capability has no tool.</summary>
    public string? ToolName => InputSchema is null ? null : Name;

    /// <summary>
    /// True when the capability moves funds or builds a signable group or a limits raise (safe.create, safe.propose,
    /// limits.update included). These are withheld while the session is in analyze mode or paused.
    /// </summary>
    public bool IsFundMoving => MovesFunds || SignScope is not null || Valuation == ValuationBasis.LimitsChange;
}

/// <summary>Context of one gate decision: who is calling and what the deployment enables.</summary>
public sealed record GateContext(
    SessionTier Tier,
    IReadOnlyCollection<string> Scopes,
    bool X402Enabled = false,
    bool LimitsUpdateEnabled = false,
    string? SessionId = null,
    string? TenantId = null,
    AgentMode Mode = AgentMode.Prepare,
    bool Paused = false);
