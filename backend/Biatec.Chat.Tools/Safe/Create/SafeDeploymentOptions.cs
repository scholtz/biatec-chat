namespace Biatec.Chat.Tools.Safe.Create;

/// <summary>
/// The algo-safe release the backend accepts for a deployment on one network (BCHAT-010). When a pin is configured the sidecar's
/// deploy call must carry exactly these programs and name this validator; without one the deployment is not constrained.
/// Compute the hashes from the release you deploy: SHA-256 of the compiled approval and clear-state program bytes, lowercase hex.
/// </summary>
public sealed class SafeDeploymentPin
{
    public string? ApprovalSha256 { get; set; }

    public string? ClearSha256 { get; set; }

    public ulong? ValidatorAppId { get; set; }
}

public sealed class SafeDeploymentOptions
{
    public const string Section = "Safe:Deployment";

    /// <summary>Pins by network name ("mainnet", "testnet").</summary>
    public Dictionary<string, SafeDeploymentPin> Pins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A pin that exists but pins nothing (every field empty) would look like protection while allowing anything.</summary>
    public static bool Valid(SafeDeploymentOptions o) => o.Pins.Values.All(p => !string.IsNullOrWhiteSpace(p.ApprovalSha256) || !string.IsNullOrWhiteSpace(p.ClearSha256) || p.ValidatorAppId is not null);
}
