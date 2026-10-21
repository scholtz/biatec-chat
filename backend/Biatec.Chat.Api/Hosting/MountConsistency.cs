using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Api.Hosting;

/// <summary>
/// The OIDC redirect URI is the exact stored value and Biatec OIDC matches it exactly, so the tenant of a mounted host
/// must say <c>https://host/chat/auth/callback</c>, byte for byte. One that says otherwise would send the sign-in callback
/// to the wrong application, so the sign-in and elevation flow is refused for it (503) instead of failing later in the
/// popup, and it is logged once.
/// </summary>
public static class MountConsistency
{
    /// <summary>
    /// True for every unmounted host; for a mounted one only when the stored redirect URI is an absolute URI whose path is
    /// exactly the mount followed by /auth/callback (compared as written, case included, with no query or fragment). The
    /// host part is not rebuilt, so <c>https://host:443/chat/auth/callback</c> is as good as <c>https://host/chat/auth/callback</c>.
    /// </summary>
    public static bool IsConsistent(Tenant tenant, string pathBase)
    {
        if (pathBase.Length == 0) return true;
        var path = pathBase + "/auth/callback";
        return Uri.TryCreate(tenant.RedirectUri, UriKind.Absolute, out var uri)
               && uri.AbsolutePath == path && uri.Query.Length == 0 && uri.Fragment.Length == 0
               && tenant.RedirectUri.EndsWith(path, StringComparison.Ordinal);
    }

    /// <summary>The base URL for popup and card links, or null when the tenant is not usable for sign-in on this host.</summary>
    public static string? SignInBase(Tenant tenant, string pathBase) =>
        IsConsistent(tenant, pathBase) ? BackendUrls.TryBase(tenant, pathBase) : null;

    public static bool IsConsistent(Tenant tenant, HttpRequest request) => IsConsistent(tenant, request.PathBase.Value ?? "");

    public static string? Expected(Tenant tenant, string pathBase) =>
        BackendUrls.TryBase(tenant, pathBase) is { } b ? b + "/auth/callback" : null;

    public static IResult Misconfigured() => Results.Problem(
        "This host is served under a path prefix, but its tenant's redirect URI does not include it, so sign-in cannot complete here.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
