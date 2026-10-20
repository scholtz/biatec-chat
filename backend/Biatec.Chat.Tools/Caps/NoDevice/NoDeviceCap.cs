using Biatec.Chat.Core.Data;
using Biatec.Chat.Tools.Consent;

namespace Biatec.Chat.Tools.Caps.NoDevice;

/// <summary>
/// Backend fallback for protocol rule 4 / rule 8 (docs/required-changes-biatec-oidc.md row 10): a user without an active
/// authenticator device gets a reduced daily cap per tenant and cannot sign anything that needs phone approval.
/// Turn it off (<c>Caps:NoDevice:Enabled = false</c>) once Biatec OIDC enforces the rule itself.
/// </summary>
public sealed class NoDeviceCapOptions
{
    public const string Section = "Caps:NoDevice";

    /// <summary>On by default: Biatec OIDC does not enforce the reduced cap yet.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cumulative below-threshold spend of the user's own funds per tenant per rolling day.</summary>
    public decimal DailyCapEur { get; set; } = NoDeviceDefaults.DailyCapEur;
}

public static class NoDeviceDefaults
{
    public const decimal DailyCapEur = 100m;
    public static readonly TimeSpan Window = TimeSpan.FromDays(1);
}

/// <summary>A user Biatec OIDC reports as having no active device (enrolled-but-inactive counts as none).</summary>
/// <param name="ThresholdEur">The user's consent threshold for this client; the deployment ceiling when OIDC does not say.</param>
public sealed record NoDeviceStatus(decimal DailyCapEur, decimal ThresholdEur, bool AlwaysAsk);

public interface INoDeviceGate
{
    /// <summary>
    /// Null when the rule does not apply: the switch is off, the user has an active device, or the backend cannot tell
    /// (<c>GET /consent/settings</c> missing or failing; then OIDC's own step-up challenge is relied on, as row 10 says).
    /// </summary>
    Task<NoDeviceStatus?> CheckAsync(string bearer, CancellationToken ct = default);
}

public sealed class NoDeviceGate(IConsentSettingsClient settings, Uri settingsEndpoint, NoDeviceCapOptions options) : INoDeviceGate
{
    public async Task<NoDeviceStatus?> CheckAsync(string bearer, CancellationToken ct = default)
    {
        if (!options.Enabled) return null;
        var s = await settings.GetAsync(settingsEndpoint, bearer, ct);
        if (s is null || s.DeviceActive) return null;
        return new NoDeviceStatus(Math.Max(0m, options.DailyCapEur), s.ThresholdEur ?? CapDefaults.ConsentThresholdCeilingEur, s.AlwaysAsk);
    }
}

public static class NoDevicePolicy
{
    /// <summary>The day's total for a user without an active device would exceed the reduced cap.</summary>
    public const string DailyCapReason = "no_device_daily_cap";
    /// <summary>The action needs phone approval and the user has no active device to give it.</summary>
    public const string NeedsDeviceReason = "no_active_device";

    public const string EnrollHint = "Enroll the Biatec authenticator on your phone to raise this limit and approve larger actions.";

    /// <summary>
    /// Whether the action would need phone approval: above the user's threshold, "always ask", consent forced by the
    /// preview or a cap, or a value the backend cannot predict (treated as above threshold, as unknown calls are).
    /// Cooling-off and first use are not here: for a user without a device rule 4 applies the reduced cap instead.
    /// </summary>
    public static bool NeedsPhone(NoDeviceStatus s, decimal? valueEur, bool consentForced) =>
        consentForced || s.AlwaysAsk || valueEur is not { } v || v > s.ThresholdEur;

    public static string Message(string reason) => reason == DailyCapReason
        ? $"You've reached this site's daily limit for accounts without an active authenticator. Nothing was signed. {EnrollHint}"
        : $"This action needs approval on your phone, and you have no active Biatec authenticator yet. Nothing was signed. {EnrollHint}";

    /// <summary>The user's own-funds spend counted against the reduced cap: in-flight reservations and final rows of the last day.</summary>
    public static IQueryable<SoftCapSpend> CountedToday(IQueryable<SoftCapSpend> mine, DateTimeOffset now)
    {
        var cutoff = now - NoDeviceDefaults.Window;
        return mine.Where(x => x.ValueSource != SpendCaps.SafeApproveReserved && x.ValueSource != SpendCaps.SafeExecuteReserved
                               && (x.Status == SoftCapStatus.Reserved || (x.SpentAt != null && x.SpentAt > cutoff)));
    }
}
