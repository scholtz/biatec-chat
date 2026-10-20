using System.Text.Json;
using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Tools.Caps;

/// <summary>Platform defaults for the tenant soft cap and the safe caps (PRODUCT-DESCRIPTION.md, Security Model, rule "Tenant-level soft cap").</summary>
public static class CapDefaults
{
    /// <summary>Cumulative below-threshold spend of the user's own funds per rolling hour.</summary>
    public const decimal SoftCapEurPerHour = 1500m;
    /// <summary>The deployment's consent threshold ceiling: the soft cap may never be set below it.</summary>
    public const decimal ConsentThresholdCeilingEur = 1000m;
    public const int SafeApprovalsPerHour = 5;
    public const decimal SafeApprovalEurPerHour = 2000m;
    public const int SafeExecutionsPerHour = 10;

    /// <summary>Every cap is a one-hour rolling window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);
    /// <summary>Rows are pruned once older than the longest window plus one hour.</summary>
    public static readonly TimeSpan Retention = Window + TimeSpan.FromHours(1);
}

/// <summary>The tenant's <c>safeApprovalCaps</c> JSON; a missing or unreadable field falls back to the platform default.</summary>
public sealed record SafeCaps(int ApprovalsPerHour, decimal ApprovalEurPerHour, int ExecutionsPerHour)
{
    public static SafeCaps Default { get; } = new(CapDefaults.SafeApprovalsPerHour, CapDefaults.SafeApprovalEurPerHour, CapDefaults.SafeExecutionsPerHour);

    public static SafeCaps Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Default;
            var r = doc.RootElement;
            return new SafeCaps(
                Int(r, "approvalsPerHour") ?? Int(r, "perHour") ?? Default.ApprovalsPerHour,
                Dec(r, "approvalEurPerHour") ?? Default.ApprovalEurPerHour,
                Int(r, "executionsPerHour") ?? Default.ExecutionsPerHour);
        }
        catch (JsonException) { return Default; }

        static int? Int(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i >= 0 ? i : null;
        static decimal? Dec(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) && d >= 0 ? d : null;
    }
}

public static class SoftCapPolicy
{
    /// <summary>
    /// The cap applied at signing time. A stored value above the platform default only holds for a trust-anchored tenant
    /// (an operator raised it); otherwise it is clamped to the default, so a self-registered tenant can never loosen it.
    /// An unset (zero or negative) value means the platform default.
    /// </summary>
    public static decimal EffectiveCapEur(Tenant tenant)
    {
        var cap = tenant.SoftCapEurPerHour > 0 ? tenant.SoftCapEurPerHour : CapDefaults.SoftCapEurPerHour;
        return !tenant.TrustAnchorVerified && cap > CapDefaults.SoftCapEurPerHour ? CapDefaults.SoftCapEurPerHour : cap;
    }

    /// <summary>
    /// Tenant save rule: the cap must be at least the consent threshold ceiling (the floor), and only an operator may
    /// raise it above the platform default, and only for a tenant that chains to a recognized trust anchor.
    /// Returns an error message, or null when the tenant may be saved.
    /// </summary>
    public static string? ValidateOnSave(Tenant tenant, bool byOperator)
    {
        if (tenant.ConsentThresholdCeilingEur <= 0)
            return "consentThresholdCeilingEur must be positive.";
        if (tenant.SoftCapEurPerHour < tenant.ConsentThresholdCeilingEur)
            return $"softCapEurPerHour must be at least the consent threshold ceiling ({tenant.ConsentThresholdCeilingEur} EUR).";
        if (tenant.SoftCapEurPerHour > CapDefaults.SoftCapEurPerHour && !(byOperator && tenant.TrustAnchorVerified))
            return $"softCapEurPerHour above {CapDefaults.SoftCapEurPerHour} EUR can only be set by an operator for a trust-anchored tenant.";
        return null;
    }
}
