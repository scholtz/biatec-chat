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
    // The verdict per tenant instance. The store hands out a new instance when it reloads a row, so a corrected row is
    // checked (and logged) anew and nothing outlives its tenant; Lazy makes the check, and its log line, run once.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Biatec.Chat.Core.Data.Tenant, Lazy<bool>> _consistent = new();

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
        return _consistent.GetValue(tenant, t => new Lazy<bool>(() =>
        {
            if (Hosting.MountConsistency.IsConsistent(t, mount))
            {
                // The other direction is only logged: a tenant whose redirect URI carries a prefix on a host that has no mount
                // keeps the URLs it always had, but it is almost certainly a mount that was removed from the overlay.
                if (mount.Length == 0 && Hosting.MountConsistency.CarriesAPrefix(t))
                    log.LogError("Tenant {TenantId} has the redirect URI {RedirectUri}, which carries a path prefix, but host {Host} is not mounted (Hosting:Mounts): its popup URLs have no prefix.",
                        t.Id, t.RedirectUri, t.Host);
                return true;
            }
            log.LogError("Tenant {TenantId} is served under the mount {Mount} but its redirect URI is {RedirectUri}, not {Expected}: sign-in is refused for it until the tenant row is corrected.",
                t.Id, mount, t.RedirectUri, Hosting.MountConsistency.Expected(t, mount));
            return false;
        })).Value;
    }

    /// <summary>The popup start and the popup navigations of sign-in and elevation; the OIDC callback and logout are not part of it.</summary>
    private static bool IsSignInFlow(PathString path) =>
        path.StartsWithSegments("/auth/popup") || path.StartsWithSegments("/auth/login") || path.StartsWithSegments("/auth/elevate");
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
