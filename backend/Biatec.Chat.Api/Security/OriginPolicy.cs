using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Api.Security;

public static class OriginPolicy
{
    public const string CsrfHeader = "X-Biatec-Chat";
    public const string ClientKeyHeader = "X-Biatec-Client-Key";

    /// <summary>Endpoints that carry no cookie and accept any origin.</summary>
    public static bool IsPublic(PathString path) => path.StartsWithSegments("/public");

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
    public static string? AllowedOrigin(Tenant tenant, HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out var values) || values.Count != 1) return null;
        var origin = Canonicalize(values[0]);
        if (origin is null) return null;
        foreach (var allowed in tenant.AllowedOrigins)
        {
            if (string.Equals(Canonicalize(allowed), origin, StringComparison.Ordinal)) return origin;
        }
        return null;
    }
}
