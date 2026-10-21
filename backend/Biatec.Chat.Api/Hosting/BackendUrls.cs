using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Api.Hosting;

/// <summary>
/// The public base URL of a tenant's backend, taken from its registered redirect URI and never from a request value:
/// <c>https://chat.biatec.io</c> for <c>https://chat.biatec.io/auth/callback</c>, and
/// <c>https://beta.dex.biatec.io/chat</c> for <c>https://beta.dex.biatec.io/chat/auth/callback</c>, so the URLs the
/// backend hands to the browser keep a mount prefix.
/// </summary>
public static class BackendUrls
{
    private const string CallbackPath = "/auth/callback";

    /// <summary>No trailing slash. Throws for a redirect URI that is not an absolute URI, like the code it replaces.</summary>
    public static string Base(Tenant tenant) => TryBase(tenant) ?? throw new UriFormatException($"Tenant {tenant.Id} has no valid redirect URI.");

    /// <summary>Null when the tenant has no absolute redirect URI.</summary>
    public static string? TryBase(Tenant tenant)
    {
        if (!Uri.TryCreate(tenant.RedirectUri, UriKind.Absolute, out var uri)) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        // A redirect URI that does not end in /auth/callback keeps the previous behaviour: the origin only.
        return path.EndsWith(CallbackPath, StringComparison.OrdinalIgnoreCase)
            ? uri.GetLeftPart(UriPartial.Authority) + path[..^CallbackPath.Length]
            : uri.GetLeftPart(UriPartial.Authority);
    }
}
