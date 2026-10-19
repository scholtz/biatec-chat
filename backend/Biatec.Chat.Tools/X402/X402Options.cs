namespace Biatec.Chat.Tools.X402;

/// <summary>
/// Platform settings for <c>x402.pay</c> (section <c>X402</c>, env <c>X402__*</c>). The platform flag is off by default and
/// a tenant must also have <c>x402Enabled</c>: both gates apply, and the tool gate hides the capability otherwise.
/// </summary>
public sealed record X402Options
{
    public const string Section = "X402";

    /// <summary>Platform feature flag. Off until after the hackathon.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Hosts a resource URL may point to: exact names or <c>*.example.com</c> wildcards. Empty means no resource is allowed
    /// (deny by default), so enabling the flag never makes the backend fetch arbitrary URLs.
    /// </summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    /// <summary>Ports a resource URL may use. Default 443 only.</summary>
    public IReadOnlyList<int> AllowedPorts { get; init; } = [443];

    /// <summary>Base URL of the x402 facilitator (<c>/verify</c>, <c>/settle</c>). Required unless the mock is used.</summary>
    public string? FacilitatorUrl { get; init; }

    /// <summary>Largest USDC amount (base units) one call may pay; protects against a hostile or buggy seller.</summary>
    public ulong MaxAmountBaseUnits { get; init; } = 5_000_000;

    /// <summary>Largest response body read from a resource or facilitator.</summary>
    public int MaxResponseBytes { get; init; } = 64 * 1024;

    public int TimeoutSeconds { get; init; } = 10;
}
