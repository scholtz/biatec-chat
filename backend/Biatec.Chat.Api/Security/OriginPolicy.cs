using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Api.Security;

public static class OriginPolicy
{
    public const string CsrfHeader = "X-Biatec-Chat";
    public const string ClientKeyHeader = "X-Biatec-Client-Key";

    /// <summary>Endpoints that carry no cookie and accept any origin (/public, and /onboarding which authenticates by DNS proof or operator bearer key).</summary>
    public static bool IsPublic(PathString path) => path.StartsWithSegments("/public") || path.StartsWithSegments("/onboarding");

    /// <summary>The administrator dashboard and its API (docs/admin-dashboard.md): same-origin only, guarded by <c>AdminAuth</c> instead of the tenant allow-list.</summary>
    public static bool IsAdmin(PathString path) => path.StartsWithSegments("/admin");

    /// <summary>The operator approval route: exempt from CSRF (bearer key, no ambient credential) but never gets a CORS wildcard.</summary>
    public static bool IsOperatorOnboarding(PathString path) => path.StartsWithSegments("/onboarding/operator");

    /// <summary>
    /// Browser navigations and metadata that cannot carry the custom header; they have their own protection
    /// (state-to-session binding, Sec-Fetch checks) in the auth tasks.
    /// </summary>
    public static bool IsNavigationOrMetadata(PathString path) =>
        path.StartsWithSegments("/auth") || path.StartsWithSegments("/openapi") || path.StartsWithSegments("/contracts");

    /// <summary>
    /// Canonical "scheme://host[:port]" for an Origin value, or null if it is missing, "null", or not a bare
    /// http(s) origin. Default ports are dropped so https://a.io:443 equals https://a.io.
    /// </summary>
    public static string? Canonicalize(string? origin)
    {
        if (string.IsNullOrEmpty(origin) || origin.Contains(',') || origin.EndsWith('/')) return null;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return null;
        if (u.Scheme is not ("http" or "https")) return null;
        if (u.UserInfo.Length > 0 || u.AbsolutePath != "/" || u.Query.Length > 0 || u.Fragment.Length > 0) return null;
        var host = u.IdnHost.TrimEnd('.').ToLowerInvariant();
        var port = u.IsDefaultPort ? "" : ":" + u.Port;
        return $"{u.Scheme}://{host}{port}";
    }

    /// <summary>The canonical request Origin when it is exactly one value on the tenant allow-list (no wildcard, no suffix match).</summary>
    public static string? AllowedOrigin(Tenant tenant, HttpRequest request) =>
        request.Headers.TryGetValue("Origin", out var values) && values.Count == 1 ? AllowedOrigin(tenant, values[0]) : null;

    /// <summary>The canonical form of <paramref name="origin"/> when it is on the tenant allow-list (no wildcard, no suffix match), else null.</summary>
    public static string? AllowedOrigin(Tenant tenant, string? origin)
    {
        var canonical = Canonicalize(origin);
        if (canonical is null) return null;
        foreach (var allowed in tenant.AllowedOrigins)
        {
            if (string.Equals(Canonicalize(allowed), canonical, StringComparison.Ordinal)) return canonical;
        }
        return null;
    }
}
