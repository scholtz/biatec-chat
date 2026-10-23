using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Api.Tenancy;

public static class TenantHttpContextExtensions
{
    private const string Key = "biatec.tenant";

    public static Tenant? GetTenant(this HttpContext ctx) => ctx.Items.TryGetValue(Key, out var t) ? t as Tenant : null;
    internal static void SetTenant(this HttpContext ctx, Tenant tenant) => ctx.Items[Key] = tenant;
}

/// <summary>
/// Resolves the tenant from the Host header alone. X-Forwarded-Host and friends are never read: header based
/// proxying is unsupported by design. A host served under a path prefix is configured, not detected
/// (<see cref="Hosting.HostMountMiddleware"/> runs first and leaves the path without the prefix). An unknown host is
/// a 404. /health is exempt because orchestrator probes address the pod by IP.
/// </summary>
public sealed class TenantMiddleware(RequestDelegate next, ITenantStore store, ILogger<TenantMiddleware> log)
{
    // The check below is a few string operations on a row the store caches anyway, so it runs per request on a mounted host
    // (and once per request on any other host). Only the log line needs a memory: one per distinct problem (tenant, redirect
    // URI, mount), so a persistent misconfiguration is reported once however often the store reloads the row, and a
    // corrected row that breaks again is reported again.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.Request.Path.StartsWithSegments("/health"))
        {
            await next(ctx);
            return;
        }

        var host = TenantHost.Normalize(ctx.Request.Host);
        var tenant = host is null ? null : await store.FindByHostAsync(host, ctx.RequestAborted);
        if (tenant is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.SetTenant(tenant);
        if (!MountIsConsistent(ctx, tenant) && IsSignInFlow(ctx.Request.Path))
        {
            await Hosting.MountConsistency.Misconfigured().ExecuteAsync(ctx);
            return;
        }
        await next(ctx);
    }

    /// <summary>
    /// A mounted host's tenant whose redirect URI does not carry the mount (<see cref="Hosting.MountConsistency"/>) is logged
    /// as an error once; the flows that start or continue a sign-in or an elevation are refused for it right here, in one
    /// place, instead of in each endpoint (see <see cref="IsSignInFlow"/>).
    /// </summary>
    private bool MountIsConsistent(HttpContext ctx, Biatec.Chat.Core.Data.Tenant tenant)
    {
        var mount = ctx.Request.PathBase.Value ?? "";
        if (Hosting.MountConsistency.IsConsistent(tenant, mount))
        {
            // The other direction is only logged: a tenant whose redirect URI carries a prefix on a host that has no mount
            // keeps the URLs it always had, but it is almost certainly a mount that was removed from the overlay.
            if (mount.Length == 0 && Hosting.MountConsistency.CarriesAPrefix(tenant) && _reported.TryAdd($"unmounted|{tenant.Id}|{tenant.Host}|{tenant.RedirectUri}", 0))
                log.LogError("Tenant {TenantId} has the redirect URI {RedirectUri}, which carries a path prefix, but host {Host} is not mounted (Hosting:Mounts): its popup URLs have no prefix.",
                    tenant.Id, tenant.RedirectUri, tenant.Host);
            return true;
        }
        if (_reported.TryAdd($"mounted|{tenant.Id}|{tenant.Host}|{tenant.RedirectUri}|{mount}", 0))
            log.LogError("Tenant {TenantId} is served under the mount {Mount} but its redirect URI is {RedirectUri}, not {Expected}: sign-in is refused for it until the tenant row is corrected.",
                tenant.Id, mount, tenant.RedirectUri, Hosting.MountConsistency.Expected(tenant, mount));
        return false;
    }

    /// <summary>Every auth route (popup, login, elevate, callback, and whatever is added) except logout, which has to keep working.</summary>
    private static bool IsSignInFlow(PathString path) => (path.StartsWithSegments("/auth") && !path.StartsWithSegments("/auth/logout")) || path.StartsWithSegments("/admin/auth");
}

public static class TenancyRegistration
{
    public static IServiceCollection AddTenancy(this IServiceCollection services)
    {
        services.AddSingleton<ITenantStore, DbTenantStore>();
        return services;
    }

    /// <summary>Tenant resolution, then credentialed CORS, then the CSRF guard.</summary>
    public static IApplicationBuilder UseTenancy(this IApplicationBuilder app)
    {
        app.UseMiddleware<TenantMiddleware>();
        app.UseMiddleware<Security.CorsMiddleware>();
        app.UseMiddleware<Security.CsrfMiddleware>();
        return app;
    }
}
