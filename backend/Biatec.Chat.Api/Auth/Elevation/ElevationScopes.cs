using Biatec.Chat.Api.Vault;

namespace Biatec.Chat.Api.Auth.Elevation;

/// <summary>
/// The fixed allow-list of scopes /auth/elevate may request, and the union rule. `rekey` (or anything else) is never
/// in the list, so it can never be requested through the elevation flow.
/// </summary>
public static class ElevationScopes
{
    public const string Sign = "sign";
    public const string ManageLimits = "manage-limits";
    public const string BaseScope = "openid";

    private static readonly string[] Allowed = [Sign, ManageLimits];

    /// <summary>True only for exactly one allow-listed scope (ordinal, no whitespace, no lists).</summary>
    public static bool IsAllowed(string? scope) => scope is not null && Array.IndexOf(Allowed, scope) >= 0;

    /// <summary>The scopes already held plus the new one, de-duplicated, never fewer than before. Order is stable.</summary>
    public static string[] Union(IEnumerable<string> held, string add)
    {
        var result = new List<string>();
        foreach (var s in held.Append(add))
            if (s.Length > 0 && !result.Contains(s, StringComparer.Ordinal)) result.Add(s);
        if (!result.Contains(BaseScope)) result.Insert(0, BaseScope);
        return [.. result];
    }
}

/// <summary>Outcome of a scope check. When refused, <see cref="Reauthorize"/> tells the widget which scope to offer.</summary>
public sealed record ScopeCheckResult
{
    public required bool Allowed { get; init; }
    /// <summary>The scope to pass to the elevation flow ("Authorize signing"), or null when nothing can be offered.</summary>
    public string? Reauthorize { get; init; }
    public string? Message { get; init; }

    public static ScopeCheckResult Ok { get; } = new() { Allowed = true };
}

public interface IScopeGuard
{
    /// <summary>
    /// Call before every signing (<see cref="ElevationScopes.Sign"/>) or limits (<see cref="ElevationScopes.ManageLimits"/>)
    /// call to Biatec OIDC. Reads the scopes the vaulted tokens were granted; never trusts the model or the browser.
    /// </summary>
    Task<ScopeCheckResult> CheckAsync(string sessionId, string requiredScope, CancellationToken ct = default);
}

public sealed class ScopeGuard(ITokenVault vault, TimeProvider clock) : IScopeGuard
{
    public async Task<ScopeCheckResult> CheckAsync(string sessionId, string requiredScope, CancellationToken ct = default)
    {
        if (!ElevationScopes.IsAllowed(requiredScope))
            return new ScopeCheckResult { Allowed = false, Message = "This capability is not available." };
        var entry = await vault.GetAsync(sessionId, ct);
        if (entry is null || clock.GetUtcNow() >= entry.AbsoluteExpiresAt)
            return new ScopeCheckResult { Allowed = false, Message = "Sign in to continue." };
        if (Array.IndexOf(entry.Scopes, requiredScope) >= 0) return ScopeCheckResult.Ok;
        return new ScopeCheckResult
        {
            Allowed = false,
            Reauthorize = requiredScope,
            Message = requiredScope == ElevationScopes.Sign
                ? "Signing needs your permission. Authorize signing to continue."
                : "Changing limits needs your permission. Authorize limit management to continue.",
        };
    }
}
