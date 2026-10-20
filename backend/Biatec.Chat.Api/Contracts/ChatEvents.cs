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
[JsonDerivedType(typeof(SafeCardEvent), "safe_card")]
[JsonDerivedType(typeof(DataCardEvent), "data_card")]
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

/// <summary>A Biatec Safe card (safes, signer groups, proposal queue) to render, from safe.list or safe.create.</summary>
public sealed record SafeCardEvent(SafeCard Card) : ChatEvent;

/// <summary>
/// A structured data card (T-067: kind "holdings", from <c>wallet.balance</c>). Built from the tool result, never from
/// model text. Amounts are decimal strings.
/// </summary>
public sealed record DataCardEvent : ChatEvent
{
    /// <summary>Card kind; "holdings".</summary>
    public required string Kind { get; init; }
    public required string Network { get; init; }
    public required IReadOnlyList<string> Addresses { get; init; }
    public required IReadOnlyList<Biatec.Chat.Api.Chat.DataCards.DataCardAsset> Assets { get; init; }
    /// <summary>ISO 4217 code of the fiat values, when the assets carry them.</summary>
    public string? FiatCurrency { get; init; }
    public required DateTimeOffset AsOf { get; init; }
    /// <summary>True when some holdings (for example ASAs without resolved decimals) are not listed on the card.</summary>
    public bool Partial { get; init; }
}

/// <summary>Authenticator approval progress.</summary>
public sealed record ConsentStatusEvent(ConsentStatus Consent) : ChatEvent;

[JsonConverter(typeof(JsonStringEnumConverter<SignInReason>))]
public enum SignInReason
{
    SignIn,
    ElevateScope,
}

/// <summary>
/// Offer to sign in (or to authorize an extra scope). When <paramref name="Unavailable"/> is false, <paramref name="SignInUrl"/>
/// is the backend endpoint the widget POSTs for the one-time popup URL. When true, sign-in cannot happen on this transport
/// and <paramref name="SignInUrl"/> is a plain page to open in a new tab (the "sign-in unavailable here" card).
/// </summary>
public sealed record SignInCardEvent(string SignInUrl, SignInReason Reason, string? Scope, string? Description, bool Unavailable) : ChatEvent;

/// <summary>Terminal or recoverable error. Messages are safe to show to the user.</summary>
public sealed record ErrorEvent(string Code, string Message, bool Retryable) : ChatEvent;
