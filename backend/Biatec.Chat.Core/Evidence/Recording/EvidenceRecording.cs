using System.Text.Json.Nodes;

namespace Biatec.Chat.Core.Evidence;

/// <summary>Entry kinds written by the recording hooks (T-035). Kinds written elsewhere (anchoring, confirm) are not repeated.</summary>
public static class EvidenceKinds
{
    public const string SessionStarted = "session_started";
    public const string TierChanged = "tier_changed";
    public const string UserMessage = "user_message";
    public const string AssistantMessage = "assistant_message";
    public const string ToolCall = "tool_call";
    public const string ToolResult = "tool_result";
    public const string PolicyRejection = "tool_call_rejected";
    public const string ActionPreview = "action_preview";
    public const string ActionStepAdded = "action_step_added";
    public const string UserConfirmed = "user_confirmed";
    public const string SigningRequested = "signing_requested";
    public const string SigningOutcome = "signing_outcome";
    public const string ConsentReceipt = "consent_receipt";
}

/// <summary>
/// Best-effort recording of evidence from the orchestrator, preview, confirm and session paths. A failing append is
/// logged by exception type only and never surfaces to the caller: evidence must not turn a user action into an error.
/// </summary>
public interface IEvidenceRecorder
{
    Task RecordAsync(Guid conversationId, string kind, JsonObject payload, CancellationToken ct = default);
}

/// <summary>
/// Schema-driven redaction. A (sub)schema carrying <c>"sensitive": true</c> marks the value at that position;
/// it is replaced by <see cref="Placeholder"/>. Walks properties, additionalProperties, items, prefixItems and
/// allOf/anyOf/oneOf.
/// </summary>
public static class SensitiveRedactor
{
    public const string Placeholder = "[redacted]";
    private const int MaxDepth = 32;

    /// <summary>True when the schema marks anything as sensitive.</summary>
    public static bool HasSensitive(JsonNode? schema, int depth = 0)
    {
        if (schema is not JsonObject o || depth > MaxDepth) return false;
        if (IsSensitive(o)) return true;
        foreach (var (key, child) in o)
        {
            switch (key)
            {
                case "properties" when child is JsonObject props:
                    if (props.Any(p => HasSensitive(p.Value, depth + 1))) return true;
                    break;
                case "items" or "additionalProperties":
                    if (HasSensitive(child, depth + 1)) return true;
                    break;
                case "prefixItems" or "allOf" or "anyOf" or "oneOf" when child is JsonArray arr:
                    if (arr.Any(a => HasSensitive(a, depth + 1))) return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>Returns a redacted copy; the input is not modified. With no schema the value is returned as a copy.</summary>
    public static JsonNode? Redact(JsonNode? value, JsonObject? schema) => Walk(value?.DeepClone(), schema, 0);

    private static bool IsSensitive(JsonObject schema) =>
        schema["sensitive"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static JsonNode? Walk(JsonNode? value, JsonObject? schema, int depth)
    {
        if (schema is null || value is null) return value;
        if (depth > MaxDepth) return JsonValue.Create(Placeholder); // fail closed on absurdly deep data
        if (IsSensitive(schema)) return JsonValue.Create(Placeholder);

        foreach (var key in new[] { "allOf", "anyOf", "oneOf" })
            if (schema[key] is JsonArray alts)
                foreach (var alt in alts.OfType<JsonObject>())
                {
                    value = Walk(value, alt, depth + 1);
                    if (value is JsonValue v && v.TryGetValue<string>(out var s) && s == Placeholder) return value;
                }

        switch (value)
        {
            case JsonObject obj:
                var props = schema["properties"] as JsonObject;
                var extra = schema["additionalProperties"] as JsonObject;
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var child = props?[name] as JsonObject ?? extra;
                    if (child is null) continue;
                    var current = obj[name];
                    var walked = Walk(current, child, depth + 1);
                    if (!ReferenceEquals(walked, current)) obj[name] = walked;
                }
                break;
            case JsonArray arr:
                var items = schema["items"] as JsonObject;
                var prefix = schema["prefixItems"] as JsonArray;
                for (var i = 0; i < arr.Count; i++)
                {
                    var s = prefix is not null && i < prefix.Count ? prefix[i] as JsonObject : items;
                    if (s is null) continue;
                    var current = arr[i];
                    var walked = Walk(current, s, depth + 1);
                    if (!ReferenceEquals(walked, current)) arr[i] = walked;
                }
                break;
        }
        return value;
    }
}
