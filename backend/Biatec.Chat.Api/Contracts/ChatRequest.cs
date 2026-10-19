namespace Biatec.Chat.Api.Contracts;

/// <summary>Body of POST /chat and POST /public/chat.</summary>
public sealed record ChatMessageRequest
{
    /// <summary>User text. Untrusted input.</summary>
    public required string Message { get; init; }
    /// <summary>Existing conversation, or null to start a new one.</summary>
    public string? ConversationId { get; init; }
    /// <summary>Untrusted host-page context (route, product), data only.</summary>
    public HostContext? Context { get; init; }
    /// <summary>Widget version, recorded as session metadata.</summary>
    public string? WidgetVersion { get; init; }
}

public sealed record HostContext
{
    public string? Route { get; init; }
    public string? Product { get; init; }
    public string? Locale { get; init; }
}

/// <summary>Body of POST /actions/{previewId}/confirm and /cancel.</summary>
public sealed record ActionDecisionRequest
{
    /// <summary>Group hash the user saw; the backend rejects the call if it differs from the stored preview.</summary>
    public required string GroupHash { get; init; }
}
