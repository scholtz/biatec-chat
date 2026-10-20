using System.Text.Json.Nodes;
using Biatec.Chat.Api.Evidence.Anchoring;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Biatec.Chat.Api.Evidence.Recording;

/// <summary>
/// Appends to the evidence log from the request scope. The log (and with it the signing key store) is resolved lazily
/// and every failure is swallowed after logging its type, so a missing key store never breaks a chat turn. A
/// conversation's first entry goes through <see cref="AnchoredEvidenceWriter"/> when it is registered, so it carries
/// the anchor reference the spec requires.
/// </summary>
public sealed class EvidenceRecorder(IServiceProvider services, ILogger<EvidenceRecorder> log) : IEvidenceRecorder
{
    public async Task RecordAsync(Guid conversationId, string kind, JsonObject payload, CancellationToken ct = default)
    {
        try
        {
            var evidence = services.GetRequiredService<IEvidenceLog>();
            var db = services.GetRequiredService<ChatDbContext>();
            var first = !await db.EvidenceEntries.AsNoTracking().AnyAsync(e => e.ConversationId == conversationId, ct);
            if (first && services.GetService<AnchoredEvidenceWriter>() is { } writer)
                await writer.AppendFirstAsync(conversationId, kind, payload, ct);
            else
                await evidence.AppendAsync(conversationId, kind, payload, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("Could not record evidence {Kind}: {ErrorType}", kind, ex.GetType().Name);
        }
    }
}

/// <summary>Payload builders shared by the hooks. Integers that can grow large are strings; no network addresses of clients are ever included.</summary>
public static class EvidencePayloads
{
    /// <summary>Redacts a JSON text by schema. Unparseable text under a schema that marks anything sensitive is withheld whole.</summary>
    public static string RedactJsonText(string text, JsonObject? schema)
    {
        if (schema is null || !SensitiveRedactor.HasSensitive(schema)) return text;
        try
        {
            var node = JsonNode.Parse(text);
            return SensitiveRedactor.Redact(node, schema)?.ToJsonString() ?? "null";
        }
        catch (System.Text.Json.JsonException)
        {
            return SensitiveRedactor.Placeholder;
        }
    }

    public static JsonObject Message(string kind, string text, string? modelId = null, int toolCalls = 0)
    {
        var o = new JsonObject { ["text"] = text };
        if (modelId is not null) o["modelId"] = modelId;
        if (kind == EvidenceKinds.AssistantMessage) o["toolCalls"] = toolCalls;
        return o;
    }

    public static JsonObject ToolCall(string toolUseId, string name, string redactedInput) => new()
    {
        ["toolUseId"] = toolUseId, ["name"] = name, ["input"] = redactedInput,
    };

    public static JsonObject ToolResult(string toolUseId, string name, bool isError, string redactedContent, string? guidance = null)
    {
        var o = new JsonObject { ["toolUseId"] = toolUseId, ["name"] = name, ["isError"] = isError, ["content"] = redactedContent };
        if (guidance is not null) o["guidance"] = guidance;
        return o;
    }

    /// <summary>
    /// Builder for the authenticator's consent receipt (recorded by the consent completion path, T-049). The receipt
    /// is kept verbatim: it carries the device key identifier and signature and cannot be redacted.
    /// </summary>
    public static JsonObject ConsentReceipt(Guid previewId, Guid stepId, string authReqId, string groupHash, string receiptJson) => new()
    {
        ["previewId"] = previewId.ToString(), ["stepId"] = stepId.ToString(), ["authReqId"] = authReqId,
        ["groupHash"] = groupHash, ["receipt"] = receiptJson,
    };
}

/// <summary>
/// Records every rejected tool call from the gate. The gate hands over a session id, not a conversation id, so the
/// conversation is looked up; arguments are never recorded (the event does not carry them).
/// </summary>
public sealed class EvidencePolicyEventSink(IServiceScopeFactory scopes, ILogger<EvidencePolicyEventSink> log) : IPolicyEventSink
{
    public void Emit(PolicyEvent policyEvent)
    {
        try
        {
            // The gate is synchronous; run on the pool so a request's synchronization context cannot deadlock.
            Task.Run(() => RecordAsync(policyEvent)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogError("Could not record policy event: {ErrorType}", ex.GetType().Name);
        }
    }

    private async Task RecordAsync(PolicyEvent e)
    {
        if (!Guid.TryParseExact(e.SessionId, "N", out var sessionId)) return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var conversationId = await db.ChatSessions.AsNoTracking().Where(s => s.Id == sessionId)
            .Select(s => (Guid?)s.ConversationId).FirstOrDefaultAsync();
        if (conversationId is null) return;
        var payload = new JsonObject
        {
            ["event"] = e.Kind, ["tool"] = e.ToolName, ["reason"] = e.Reason, ["tier"] = e.Tier.ToString(),
            ["tenantId"] = e.TenantId, ["at"] = e.At.ToString("O"), ["violationCount"] = e.Violations?.Count ?? 0,
        };
        await scope.ServiceProvider.GetRequiredService<IEvidenceRecorder>().RecordAsync(conversationId.Value, EvidenceKinds.PolicyRejection, payload);
    }
}

public static class EvidenceRecordingRegistration
{
    /// <summary>Registers the recorder and routes the tool gate's policy events into the evidence log. Call after AddChat and AddEvidence.</summary>
    public static IServiceCollection AddEvidenceRecording(this IServiceCollection services)
    {
        services.TryAddScoped<IEvidenceRecorder, EvidenceRecorder>();
        services.Replace(ServiceDescriptor.Singleton<IPolicyEventSink, EvidencePolicyEventSink>());
        return services;
    }
}
