using System.Text.Json.Serialization;

namespace Biatec.Chat.Api.Contracts;

/// <summary>
/// One server-sent event of a chat reply. Discriminated by the <c>type</c> property.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(TextDeltaEvent), "text_delta")]
[JsonDerivedType(typeof(ToolStatusEvent), "tool_status")]
[JsonDerivedType(typeof(PreviewCardEvent), "preview_card")]
[JsonDerivedType(typeof(ConsentStatusEvent), "consent_status")]
[JsonDerivedType(typeof(SignInCardEvent), "sign_in_card")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
public abstract record ChatEvent;

/// <summary>Incremental assistant text.</summary>
public sealed record TextDeltaEvent(string Text) : ChatEvent;

[JsonConverter(typeof(JsonStringEnumConverter<ToolRunState>))]
public enum ToolRunState
{
    Started,
    Succeeded,
    Failed,
}

/// <summary>Compact progress line, e.g. "Checking your USDC balance".</summary>
public sealed record ToolStatusEvent(string ToolCallId, string Label, ToolRunState State) : ChatEvent;

/// <summary>An action preview card to render.</summary>
public sealed record PreviewCardEvent(ActionPreview Preview) : ChatEvent;

/// <summary>Authenticator approval progress.</summary>
public sealed record ConsentStatusEvent(ConsentStatus Consent) : ChatEvent;

[JsonConverter(typeof(JsonStringEnumConverter<SignInReason>))]
public enum SignInReason
{
    SignIn,
    ElevateScope,
}

/// <summary>Offer to sign in (or to authorize an extra scope). The URL opens in a popup.</summary>
public sealed record SignInCardEvent(string SignInUrl, SignInReason Reason, string? Scope, string? Description) : ChatEvent;

/// <summary>Terminal or recoverable error. Messages are safe to show to the user.</summary>
public sealed record ErrorEvent(string Code, string Message, bool Retryable) : ChatEvent;
