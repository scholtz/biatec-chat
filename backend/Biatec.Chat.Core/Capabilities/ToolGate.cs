using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Core.Capabilities;

/// <summary>A policy event recorded on every rejected tool call. Arguments are deliberately not included.</summary>
public sealed record PolicyEvent(
    string Kind,
    string ToolName,
    string Reason,
    SessionTier Tier,
    string? SessionId,
    string? TenantId,
    DateTimeOffset At,
    IReadOnlyList<string>? Violations = null);

/// <summary>Receives policy events. The evidence log (a later task) implements this.</summary>
public interface IPolicyEventSink
{
    void Emit(PolicyEvent policyEvent);
}

/// <summary>Collects events in memory; used by tests and as a default sink.</summary>
public sealed class InMemoryPolicyEventSink : IPolicyEventSink
{
    private const int Capacity = 10_000;
    private int _count;
    private readonly ConcurrentQueue<PolicyEvent> _events = new();
    public IReadOnlyList<PolicyEvent> Events => _events.ToArray();

    public void Emit(PolicyEvent policyEvent)
    {
        _events.Enqueue(policyEvent);
        if (Interlocked.Increment(ref _count) <= Capacity) return;
        if (_events.TryDequeue(out _)) Interlocked.Decrement(ref _count);
    }
}

/// <summary>Outcome of a gate check. <see cref="ModelError"/> is the text returned to the model as an error tool result.</summary>
public sealed record GateDecision(bool Allowed, Capability? Capability, string? Reason, string? ModelError)
{
    public static GateDecision Allow(Capability c) => new(true, c, null, null);
}

/// <summary>
/// Server-side tool gate. Checks every call against tier, token scopes, feature flags, the capability registry and the
/// capability's JSON schema, independent of what the model was told. Fails closed on anything unknown.
/// </summary>
public sealed class ToolGate(CapabilityRegistry registry, IPolicyEventSink sink, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public CapabilityRegistry Registry => registry;

    /// <summary>The tool list to send to the model for this context.</summary>
    public IReadOnlyList<Capability> ToolsFor(GateContext ctx) => registry.ToolsFor(Normalize(ctx));

    /// <summary>Checks a tool call proposed by the model. Emits a policy event on every rejection.</summary>
    public GateDecision Check(string? toolName, JsonNode? arguments, GateContext ctx)
    {
        ctx = Normalize(ctx);
        var name = toolName ?? "";
        var cap = registry.Find(name);
        if (cap is null) return Reject(name, ctx, "unknown_tool", $"Tool '{Sanitize(name)}' does not exist.");
        if (cap.ToolName is null) return Reject(name, ctx, "not_a_tool", $"'{cap.Name}' is not a callable tool.");

        if (CapabilityRegistry.Visibility(cap, ctx) is { } why)
        {
            var msg = why switch
            {
                "tier_too_low" => $"'{cap.Name}' needs a signed-in user. Call requestSignIn instead and explain in general terms what the action would do.",
                "scope_missing" => $"'{cap.Name}' needs a permission the session does not hold. Offer the user to re-authorize with the needed scope.",
                _ => $"Tool '{Sanitize(name)}' does not exist.", // same text as unknown_tool: no capability enumeration
            };
            return Reject(name, ctx, why, msg);
        }

        // Models send null/absent arguments for tools without parameters.
        var args = arguments ?? new JsonObject();
        IReadOnlyList<string> violations;
        try { violations = JsonSchemaValidator.Validate(cap.InputSchema!, args); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fail closed: a validator fault is a rejection, never an exception that skips the gate.
            return Reject(name, ctx, "schema_invalid", "Arguments could not be validated against the tool schema.");
        }
        if (violations.Count > 0)
            return Reject(name, ctx, "schema_invalid",
                "Arguments do not match the tool schema: " + string.Join("; ", violations.Take(5)) + ".", violations);

        return GateDecision.Allow(cap);
    }

    /// <summary>
    /// Scope check before every signing or limits call to Biatec OIDC: the stored access token must hold the
    /// capability's sign scope (or manage-limits for limits.update).
    /// </summary>
    public GateDecision CheckSigningScope(string capabilityName, GateContext ctx)
    {
        ctx = Normalize(ctx);
        var cap = registry.Find(capabilityName);
        if (cap is null) return Reject(capabilityName, ctx, "unknown_tool", $"Tool '{Sanitize(capabilityName)}' does not exist.");
        if (CapabilityRegistry.Visibility(cap, ctx) is { } why)
            return Reject(capabilityName, ctx, why, $"'{cap.Name}' is not available.");
        if (cap.ToolName is null || (cap.SignScope is null && cap.Valuation != ValuationBasis.LimitsChange))
            return Reject(capabilityName, ctx, "not_signing_capability", $"'{cap.Name}' does not sign or change limits.");
        if (cap.SignScope is { } scope && !ctx.Scopes.Contains(scope, StringComparer.Ordinal))
            return Reject(capabilityName, ctx, "scope_missing",
                $"'{cap.Name}' needs the '{scope}' permission. Offer the user to re-authorize with it.");
        return GateDecision.Allow(cap);
    }

    private static GateContext Normalize(GateContext ctx) => ctx.Scopes is null ? ctx with { Scopes = [] } : ctx;

    private GateDecision Reject(string tool, GateContext ctx, string reason, string modelError, IReadOnlyList<string>? violations = null)
    {
        try
        {
            sink.Emit(new PolicyEvent("tool_call_rejected", Sanitize(tool), reason, ctx.Tier, ctx.SessionId, ctx.TenantId,
                _time.GetUtcNow(), violations));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failing evidence sink must never turn a rejection into an exception.
        }
        return new GateDecision(false, registry.Find(tool), reason, modelError);
    }

    // Tool names come from the model; keep them short and printable before logging or echoing.
    private static string Sanitize(string s)
    {
        return new string(s.Where(ch => ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-')
            .Take(64).ToArray());
    }
}
