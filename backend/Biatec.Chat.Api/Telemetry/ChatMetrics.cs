using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Biatec.Chat.Api.Orchestration;

namespace Biatec.Chat.Api.Telemetry;

/// <summary>
/// The product metrics. Tags are low-cardinality labels only: tier, reason, kind. Never a session, tenant, user,
/// address or anything the visitor typed.
/// </summary>
public sealed class ChatMetrics : IDisposable
{
    public const string MeterName = "Biatec.Chat";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _refusals;
    private readonly Counter<long> _policyViolations;
    private readonly Counter<long> _consent;

    public ChatMetrics()
    {
        _refusals = _meter.CreateCounter<long>("biatec.chat.refusals", "{refusal}", "Actions refused, by session tier and reason.");
        _policyViolations = _meter.CreateCounter<long>("biatec.chat.policy_violations", "{event}", "Tool calls rejected by the server-side gate.");
        _consent = _meter.CreateCounter<long>("biatec.chat.consent", "{event}", "Consent requests and their outcomes (approved, denied, expired).");
    }

    public void Refusal(SessionTier tier, string reason) =>
        _refusals.Add(1, new TagList { { "tier", Tier(tier) }, { "reason", Label(reason) } });

    public void PolicyViolation(PolicyEvent e)
    {
        _policyViolations.Add(1, new TagList { { "kind", Label(e.Kind) }, { "reason", Label(e.Reason) }, { "tier", Tier(e.Tier) } });
        Refusal(e.Tier, e.Reason);
    }

    public void Consent(string evidenceKind) => _consent.Add(1, new TagList { { "kind", Label(evidenceKind) } });

    public void Dispose() => _meter.Dispose();

    private static string Tier(SessionTier tier) => tier.ToString().ToLowerInvariant();

    /// <summary>Reasons and kinds are code-defined identifiers; anything else is folded so it can't grow the series.</summary>
    internal static string Label(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')
            ? value : "other";
}

/// <summary>Counts every gate rejection, then passes it on to the real sink.</summary>
public sealed class MeteringPolicyEventSink(IPolicyEventSink inner, ChatMetrics metrics) : IPolicyEventSink
{
    public void Emit(PolicyEvent policyEvent)
    {
        metrics.PolicyViolation(policyEvent);
        inner.Emit(policyEvent);
    }
}

/// <summary>Counts consent entries (<c>consent_*</c> kinds) as they are appended to the evidence chain.</summary>
public sealed class MeteringEvidenceLog(IEvidenceLog inner, ChatMetrics metrics) : IEvidenceLog
{
    public async Task<EvidenceEntry> AppendAsync(Guid conversationId, string kind, JsonObject payload, Guid? anchorRef = null, CancellationToken ct = default)
    {
        var entry = await inner.AppendAsync(conversationId, kind, payload, anchorRef, ct);
        if (kind.StartsWith("consent_", StringComparison.Ordinal))
            metrics.Consent(kind);
        return entry;
    }

    public Task<bool> DeletePayloadAsync(Guid conversationId, long seq, CancellationToken ct = default) => inner.DeletePayloadAsync(conversationId, seq, ct);
    public Task<int> DeleteAllPayloadsAsync(Guid conversationId, CancellationToken ct = default) => inner.DeleteAllPayloadsAsync(conversationId, ct);
    public Task<IReadOnlyList<EvidenceEntry>> GetChainAsync(Guid conversationId, CancellationToken ct = default) => inner.GetChainAsync(conversationId, ct);
    public Task<EvidenceVerification> VerifyAsync(Guid conversationId, CancellationToken ct = default) => inner.VerifyAsync(conversationId, ct);
}

/// <summary>
/// <c>requestSignIn</c> is the anonymous tier's refusal: the model calls it when a visitor asks for an action.
/// Each call counts as a refusal for the caller's tier.
/// </summary>
public sealed class MeteringSignInExecutor(IToolExecutor inner, ChatMetrics metrics) : IToolExecutor
{
    public const string Reason = "sign_in_required";

    public string CapabilityName => inner.CapabilityName;

    public string? StatusLabel(JsonNode arguments) => inner.StatusLabel(arguments);

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, JsonNode arguments, CancellationToken ct)
    {
        metrics.Refusal(context.Tier, Reason);
        return inner.ExecuteAsync(context, arguments, ct);
    }
}
