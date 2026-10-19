using Amazon.BedrockRuntime.Model;

namespace Biatec.Chat.Api.Model;

/// <summary>Which configured model a request uses. Anonymous sessions run on the cheaper model.</summary>
public enum ModelProfile
{
    Anonymous,
    Authenticated,
}

/// <summary>
/// One model turn. The system prompt is the two stable parts, in this order, each followed by a cache point:
/// the persona and refusal rules, then the tier's capability catalogue. Volatile data belongs in tool results.
/// </summary>
public sealed record ModelRequest
{
    public required ModelProfile Profile { get; init; }
    public required string Persona { get; init; }
    public required string Catalogue { get; init; }
    public required IReadOnlyList<Message> Messages { get; init; }
    public IReadOnlyList<ToolSpecification> Tools { get; init; } = [];

    /// <summary>Overrides Bedrock:MaxTokens for this call; it can only lower the configured value.</summary>
    public int? MaxTokens { get; init; }
}

public abstract record ModelStreamEvent;

public sealed record ModelTextDelta(string Text) : ModelStreamEvent;

/// <summary>A complete tool call proposed by the model. <c>InputJson</c> is untrusted until the orchestrator validates it.</summary>
public sealed record ModelToolUse(string ToolUseId, string Name, string InputJson) : ModelStreamEvent;

/// <summary>
/// End of the assistant message. <c>Message</c> is the reconstructed assistant turn (text and tool-use blocks),
/// ready to append to the conversation before sending tool results back.
/// </summary>
public sealed record ModelMessageStop(string StopReason, Message Message) : ModelStreamEvent;

public sealed record ModelUsage(
    string ModelId,
    int InputTokens,
    int OutputTokens,
    int CacheReadInputTokens,
    int CacheWriteInputTokens,
    long LatencyMs) : ModelStreamEvent;

public interface IModelClient
{
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct = default);
}
