using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Api.Hosting;

/// <summary>
/// The public base URL of a tenant's backend: the origin of its registered redirect URI (never a request value) plus the
/// path base the host is mounted under (<see cref="HostMountMiddleware"/>; empty on every unmounted host), so the URLs the
/// backend hands to the browser keep a mount prefix: <c>https://chat.biatec.io</c>, or
/// <c>https://beta.dex.biatec.io/chat</c> on the mounted host. The prefix comes from the configured mount, not from how
/// the redirect URI happens to end, so a tenant that is not mounted keeps exactly the URLs it always had.
/// </summary>
public static class BackendUrls
{
    /// <summary>No trailing slash. Throws for a redirect URI that is not an absolute URI, like the code it replaces.</summary>
    public static string Base(Tenant tenant, string pathBase) =>
        TryBase(tenant, pathBase) ?? throw new UriFormatException($"Tenant {tenant.Id} has no valid redirect URI.");

    /// <summary>The origin only (scheme, host, port), never a mount prefix: what is reported to Biatec OIDC as the backend's origin.</summary>
    public static string Origin(Tenant tenant) => Base(tenant, "");

    /// <summary>The base for the request's own host: its path base is the mount, empty when the host is not mounted.</summary>
    public static string Base(Tenant tenant, HttpRequest request) => Base(tenant, request.PathBase.Value ?? "");

    /// <summary>Null when the tenant has no absolute redirect URI.</summary>
    public static string? TryBase(Tenant tenant, string pathBase) =>
        Uri.TryCreate(tenant.RedirectUri, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) + pathBase.TrimEnd('/') : null;
}
