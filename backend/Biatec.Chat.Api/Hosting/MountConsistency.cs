using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Api.Hosting;

/// <summary>
/// The OIDC redirect URI is the exact stored value and Biatec OIDC matches it exactly, so the tenant of a mounted host
/// must say <c>https://host/chat/auth/callback</c>, byte for byte. One that says otherwise would send the sign-in callback
/// to the wrong application, so <see cref="Tenancy.TenantMiddleware"/> refuses the sign-in and elevation flow for it (503)
/// instead of letting it fail later in the popup, and logs it once; the sign-in cards fall back to the sign-in page.
/// </summary>
public static class MountConsistency
{
    /// <summary>
    /// True for every unmounted host; for a mounted one only when the stored redirect URI is an absolute URI on the tenant's
    /// own host whose path, as written (case included), is exactly the mount followed by /auth/callback, with nothing in
    /// front of it (no dot segments), no query and no fragment. The host part is not rebuilt, so
    /// <c>https://host:443/chat/auth/callback</c> is as good as <c>https://host/chat/auth/callback</c>.
    /// </summary>
    public static bool IsConsistent(Tenant tenant, string pathBase)
    {
        if (pathBase.Length == 0) return true;
        var path = pathBase + "/auth/callback";
        var raw = tenant.RedirectUri;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;
        // https only (the __Host- cookie is Secure) and no userinfo (it would be copied into every popup and card URL).
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 || !raw.EndsWith(path, StringComparison.Ordinal)) return false;
        if (!string.Equals(uri.Host, tenant.Host, StringComparison.OrdinalIgnoreCase)) return false;
        // What stands in front of the path must be the bare "scheme://authority": no "/x/.." that Uri would normalize away.
        var authority = raw[..^path.Length];
        var afterScheme = authority.IndexOf("://", StringComparison.Ordinal) + 3;
        return afterScheme >= 3 && authority.IndexOf('/', afterScheme) < 0;
    }

    /// <summary>True when the stored redirect URI has a path in front of <c>/auth/callback</c> (it was written for a mount).</summary>
    public static bool CarriesAPrefix(Tenant tenant)
    {
        // The path as written, like IsConsistent reads it: after the authority, before any query or fragment.
        var raw = tenant.RedirectUri;
        var afterScheme = raw.IndexOf("://", StringComparison.Ordinal) + 3;
        if (afterScheme < 3 || raw.IndexOf('/', afterScheme) is not (>= 0 and var slash)) return false;
        var path = raw[slash..].Split('?', '#')[0];
        return path.EndsWith("/auth/callback", StringComparison.Ordinal) && path.Length > "/auth/callback".Length;
    }

    /// <summary>The base URL for popup and card links, or null when the tenant is not usable for sign-in on this host.</summary>
    public static string? SignInBase(Tenant tenant, string pathBase) =>
        IsConsistent(tenant, pathBase) ? BackendUrls.TryBase(tenant, pathBase) : null;

    public static string? Expected(Tenant tenant, string pathBase) =>
        BackendUrls.TryBase(tenant, pathBase) is { } b ? b + "/auth/callback" : null;

    public static IResult Misconfigured() => Results.Problem(
        "This host is served under a path prefix, but its tenant's redirect URI does not include it, so sign-in cannot complete here.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
