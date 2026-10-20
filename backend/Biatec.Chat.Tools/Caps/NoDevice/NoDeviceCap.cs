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
/// <param name="ThresholdEur">The user's consent threshold for this client, never above the deployment ceiling.</param>
public sealed record NoDeviceStatus(decimal DailyCapEur, decimal ThresholdEur, bool AlwaysAsk);

public interface INoDeviceGate
{
    /// <summary>
    /// Null when the rule does not apply: the switch is off, the user has an active device, or the backend cannot tell
    /// (<c>GET /consent/settings</c> missing or failing; then OIDC's own step-up challenge is relied on, as row 10 says).
    /// </summary>
    /// <param name="sessionKey">The chat session the settings are cached under.</param>
    Task<NoDeviceStatus?> CheckAsync(string sessionKey, string bearer, CancellationToken ct = default);
}

/// <summary>The gate when <c>Caps:NoDevice:Enabled</c> is false: never applies and resolves nothing.</summary>
public sealed class DisabledNoDeviceGate : INoDeviceGate
{
    public static DisabledNoDeviceGate Instance { get; } = new();
    public Task<NoDeviceStatus?> CheckAsync(string sessionKey, string bearer, CancellationToken ct = default) => Task.FromResult<NoDeviceStatus?>(null);
}

/// <summary>
/// Per-pod cache in front of <c>GET /consent/settings</c>, so a confirm does not wait for it every time: a session's
/// settings for <see cref="SettingsTtl"/>, and an endpoint that answered 404 / 501 is not called for <see cref="MissingTtl"/>.
/// Failures are not cached.
/// </summary>
public sealed class NoDeviceSettingsCache(TimeProvider clock)
{
    public static readonly TimeSpan SettingsTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MissingTtl = TimeSpan.FromMinutes(10);
    private const int MaxEntries = 10_000;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (ConsentSettings Settings, DateTimeOffset Until)> _sessions = new();
    private long _missingUntilTicks;

    public bool EndpointMissing => clock.GetUtcNow().UtcTicks < Interlocked.Read(ref _missingUntilTicks);

    public void MarkMissing() => Interlocked.Exchange(ref _missingUntilTicks, (clock.GetUtcNow() + MissingTtl).UtcTicks);

    public bool TryGet(string sessionKey, out ConsentSettings settings)
    {
        settings = null!;
        if (!_sessions.TryGetValue(sessionKey, out var e)) return false;
        if (e.Until <= clock.GetUtcNow()) { _sessions.TryRemove(sessionKey, out _); return false; }
        settings = e.Settings;
        return true;
    }

    public void Put(string sessionKey, ConsentSettings settings)
    {
        var now = clock.GetUtcNow();
        if (_sessions.Count >= MaxEntries)
            foreach (var kv in _sessions)
                if (kv.Value.Until <= now) _sessions.TryRemove(kv.Key, out _);
        if (_sessions.Count >= MaxEntries) _sessions.Clear();
        _sessions[sessionKey] = (settings, now + SettingsTtl);
    }
}

/// <param name="settingsEndpoint">Built on first use, so an empty or invalid issuer never breaks resolving the confirm path; null means the backend cannot tell.</param>
public sealed class NoDeviceGate(IConsentSettingsClient settings, Func<Uri?> settingsEndpoint, NoDeviceCapOptions options, NoDeviceSettingsCache cache) : INoDeviceGate
{
    public async Task<NoDeviceStatus?> CheckAsync(string sessionKey, string bearer, CancellationToken ct = default)
    {
        if (!options.Enabled) return null;
        if (!cache.TryGet(sessionKey, out var s))
        {
            if (cache.EndpointMissing || settingsEndpoint() is not { } endpoint) return null;
            var fetched = await settings.FetchAsync(endpoint, bearer, ct);
            if (fetched.EndpointMissing) cache.MarkMissing();
            if (fetched.Settings is null) return null;
            s = fetched.Settings;
            cache.Put(sessionKey, s);
        }
        if (s.DeviceActive) return null;
        var ceiling = CapDefaults.ConsentThresholdCeilingEur;
        return new NoDeviceStatus(Math.Max(0m, options.DailyCapEur), Math.Min(s.ThresholdEur ?? ceiling, ceiling), s.AlwaysAsk);
    }
}

public static class NoDevicePolicy
{
    /// <summary>The day's total for a user without an active device would exceed the reduced cap.</summary>
    public const string DailyCapReason = "no_device_daily_cap";
    /// <summary>The action needs phone approval and the user has no active device to give it.</summary>
    public const string NeedsDeviceReason = "no_active_device";
    /// <summary>The reduced cap applies but cannot be checked (no signed-in user or no cap store): refused, fail closed.</summary>
    public const string UncheckedReason = "no_device_cap_unavailable";

    public const string EnrollHint = "Enroll the Biatec authenticator on your phone to raise this limit and approve larger actions.";

    /// <summary>
    /// Whether the action would need phone approval: above the user's threshold, "always ask", consent forced by the
    /// preview or a cap, or a value the backend cannot predict (treated as above threshold, as unknown calls are).
    /// Cooling-off and first use are not here: for a user without a device rules 4 and 8 apply the reduced daily cap
    /// instead. If Biatec OIDC (which does not implement rule 4 yet) still answers with a step-up, the confirm path
    /// refuses at once rather than wait for a phone the user does not have.
    /// </summary>
    public static bool NeedsPhone(NoDeviceStatus s, decimal? valueEur, bool consentForced) =>
        consentForced || s.AlwaysAsk || valueEur is not { } v || v > s.ThresholdEur;

    public static string Message(string reason) => reason switch
    {
        DailyCapReason => $"You've reached this site's daily limit for accounts without an active authenticator. Nothing was signed. {EnrollHint}",
        UncheckedReason => $"Your account has no active authenticator, and this site's daily limit for such accounts couldn't be checked, so this can't be signed now. Nothing was signed. {EnrollHint}",
        _ => $"This action needs approval on your phone, and you have no active Biatec authenticator yet. Nothing was signed. {EnrollHint}",
    };

    /// <summary>The user's own-funds spend counted against the reduced cap: in-flight reservations and final rows of the last day.</summary>
    public static IQueryable<SoftCapSpend> CountedToday(IQueryable<SoftCapSpend> mine, DateTimeOffset now)
    {
        var cutoff = now - NoDeviceDefaults.Window;
        return mine.Where(x => x.ValueSource != SpendCaps.SafeApproveReserved && x.ValueSource != SpendCaps.SafeExecuteReserved
                               && (x.Status == SoftCapStatus.Reserved || (x.SpentAt != null && x.SpentAt > cutoff)));
    }
}
