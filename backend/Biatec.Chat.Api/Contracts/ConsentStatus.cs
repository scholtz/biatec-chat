using System.Text.Json.Serialization;

namespace Biatec.Chat.Api.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ConsentState>))]
public enum ConsentState
{
    NotRequired,
    Pending,
    Approved,
    Denied,
    TimedOut,
}

/// <summary>State of authenticator approval for one preview step.</summary>
public sealed record ConsentStatus
{
    public required string PreviewId { get; init; }
    public required string StepId { get; init; }
    public required ConsentState State { get; init; }
    /// <summary>Short binding code the user types into the authenticator.</summary>
    public string? BindingCode { get; init; }
    /// <summary>End of the consent window, for the countdown.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
