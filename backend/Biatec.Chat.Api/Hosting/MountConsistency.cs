using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Api.Hosting;

/// <summary>
/// The OIDC redirect URI is the exact stored value, so the tenant of a mounted host must say
/// <c>https://host/chat/auth/callback</c>. One that says otherwise would send the sign-in callback to the wrong
/// application, so sign-in is refused for it (503) instead of failing later in the popup, and it is logged once.
/// </summary>
public static class MountConsistency
{
    /// <summary>True for every unmounted host; for a mounted one only when the redirect URI carries the mount exactly.</summary>
    public static bool IsConsistent(Tenant tenant, string pathBase)
    {
        if (pathBase.Length == 0) return true;
        return string.Equals(tenant.RedirectUri, Expected(tenant, pathBase), StringComparison.OrdinalIgnoreCase);
    }

    public static string? Expected(Tenant tenant, string pathBase) =>
        BackendUrls.TryBase(tenant, pathBase) is { } b ? b + "/auth/callback" : null;

    public static IResult Misconfigured() => Results.Problem(
        "This host is served under a path prefix, but its tenant's redirect URI does not include it, so sign-in cannot complete here.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
