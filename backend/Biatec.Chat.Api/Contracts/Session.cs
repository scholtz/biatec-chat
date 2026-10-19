using System.Text.Json.Serialization;

namespace Biatec.Chat.Api.Contracts;

/// <summary>Session tier. Anonymous sessions can only discover; Authenticated sessions can build previews and act.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionTier>))]
public enum SessionTier
{
    Anonymous,
    Authenticated,
}

/// <summary>Progress of a sign-in popup flow, identified by its authorization request ID.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SignInStatus>))]
public enum SignInStatus
{
    None,
    Pending,
    Complete,
    Failed,
}

/// <summary>The session as the widget sees it. Never contains tokens.</summary>
public sealed record SessionState
{
    public required string SessionId { get; init; }
    public required SessionTier Tier { get; init; }
    /// <summary>Signed-in email, null for guests.</summary>
    public string? Email { get; init; }
    /// <summary>Active primary address, null for guests.</summary>
    public string? PrimaryAddress { get; init; }
    /// <summary>OIDC scopes the session currently holds (names only, never token material).</summary>
    public required IReadOnlyList<string> Scopes { get; init; }
    /// <summary>Authorization request ID of the latest sign-in or elevation popup, if any.</summary>
    public string? AuthRequestId { get; init; }
    public SignInStatus SignInStatus { get; init; }
    /// <summary>Session expiry, used by the widget to warn before a multi-step action runs out of time.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
    /// <summary>What Biatec AI may do: analyze (read only) or prepare. Anonymous sessions are always analyze.</summary>
    public Biatec.Chat.Api.Agent.AgentModeValue AgentMode { get; init; } = Biatec.Chat.Api.Agent.AgentModeValue.Analyze;
    /// <summary>True while the user has paused Biatec AI.</summary>
    public bool Paused { get; init; }
}

/// <summary>A clickable prompt suggested for an empty or ongoing session.</summary>
public sealed record Suggestion
{
    public required string Id { get; init; }
    /// <summary>Text shown on the chip.</summary>
    public required string Label { get; init; }
    /// <summary>Message sent when the chip is clicked.</summary>
    public required string Prompt { get; init; }
    /// <summary>Minimum tier needed to act on it.</summary>
    public SessionTier MinTier { get; init; }
}

public sealed record SuggestionList
{
    public required IReadOnlyList<Suggestion> Suggestions { get; init; }
}
