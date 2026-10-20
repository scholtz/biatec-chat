using System.Text.Json.Serialization;

namespace Biatec.Chat.Api.Contracts;

/// <summary>Which tool produced a safe card.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SafeCardSource>))]
public enum SafeCardSource
{
    /// <summary><c>safe.list</c>: the user's safes with their pending proposals.</summary>
    List,
    /// <summary><c>safe.create</c>: the safe being created, shown once it is deployed.</summary>
    Create,
}

/// <summary>Where a safe being created stands; only set on cards from <c>safe.create</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SafeCardCreationState>))]
public enum SafeCardCreationState
{
    /// <summary>Deployed; the signer-group setup still has to land.</summary>
    SetupPending,
    /// <summary>Deployed; the safe account needs ALGO for storage before the setup.</summary>
    NeedsFunding,
    /// <summary>Deployed and set up with its signer group.</summary>
    Created,
}

[JsonConverter(typeof(JsonStringEnumConverter<SafeCardProposalState>))]
public enum SafeCardProposalState
{
    AwaitingApprovals,
    ReadyToExecute,
}

/// <summary>A signer group of a safe. Names come from the chain: untrusted text.</summary>
public sealed record SafeCardSignerGroup
{
    /// <summary>Group id on chain; null for a group not read from the chain yet.</summary>
    public string? GroupId { get; init; }
    public required string Name { get; init; }
    public required int Threshold { get; init; }
    public required int MemberCount { get; init; }
    /// <summary>Incremented on every membership change; proposals bind to it. Null when unknown.</summary>
    public long? MembershipEpoch { get; init; }
    public IReadOnlyList<string> AllowedActions { get; init; } = [];
    public bool IsAdminGroup { get; init; }
}

/// <summary>A proposal still waiting for approvals or for execution, with its progress.</summary>
public sealed record SafeCardProposal
{
    public required string ProposalId { get; init; }
    public required string GroupId { get; init; }
    public required string Kind { get; init; }
    public required SafeCardProposalState Status { get; init; }
    public required int Approvals { get; init; }
    public required int Threshold { get; init; }
    /// <summary>Whether one of the user's member addresses approved; null when unknown.</summary>
    public bool? ApprovedByYou { get; init; }
    public required string Proposer { get; init; }
    /// <summary>Decimal string (uint64).</summary>
    public required string PayloadVersion { get; init; }
    /// <summary>Round after which the proposal expires, as a decimal string (uint64).</summary>
    public required string ExpiryRound { get; init; }
}

/// <summary>One Biatec Safe on the card.</summary>
public sealed record SafeCardSafe
{
    /// <summary>Application id, as a decimal string (uint64).</summary>
    public required string AppId { get; init; }
    public required string Address { get; init; }
    /// <summary>Safe name from the chain or the user: untrusted text.</summary>
    public required string Name { get; init; }
    /// <summary>Network name, e.g. algorand-testnet.</summary>
    public required string Network { get; init; }
    public bool Paused { get; init; }
    /// <summary>The user's addresses that are members of this safe.</summary>
    public IReadOnlyList<string> YourAddresses { get; init; } = [];
    public IReadOnlyList<SafeCardSignerGroup> SignerGroups { get; init; } = [];
    public IReadOnlyList<SafeCardProposal> PendingProposals { get; init; } = [];
    /// <summary>More proposals exist than are shown.</summary>
    public bool ProposalsTruncated { get; init; }
    /// <summary>The proposals could not be read, so the queue is unknown (not empty).</summary>
    public bool ProposalsCouldNotBeRead { get; init; }
    /// <summary>Set on cards from <c>safe.create</c> only.</summary>
    public SafeCardCreationState? Creation { get; init; }
}

/// <summary>Structured safe card data, built from the safe tools' results. Never free model text.</summary>
public sealed record SafeCard
{
    public required SafeCardSource Source { get; init; }
    /// <summary>Network name, e.g. algorand-testnet.</summary>
    public required string Network { get; init; }
    public required IReadOnlyList<SafeCardSafe> Safes { get; init; }
    /// <summary>App ids of known safes that could not be read (membership unknown, not "not a member").</summary>
    public IReadOnlyList<string> CouldNotRead { get; init; } = [];
}
