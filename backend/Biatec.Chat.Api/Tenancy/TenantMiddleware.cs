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
    // The verdict per tenant, redirect URI and mount, so a request does not parse the URI again; a corrected tenant row has
    // another redirect URI and is checked (and logged) anew.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _consistent = new(StringComparer.Ordinal);

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
        if (mount.Length == 0) return true;
        return _consistent.GetOrAdd($"{tenant.Id}|{tenant.Host}|{tenant.RedirectUri}|{mount}", _ =>
        {
            var ok = Hosting.MountConsistency.IsConsistent(tenant, mount);
            if (!ok)
                log.LogError("Tenant {TenantId} is served under the mount {Mount} but its redirect URI is {RedirectUri}, not {Expected}: sign-in is refused for it until the tenant row is corrected.",
                    tenant.Id, mount, tenant.RedirectUri, Hosting.MountConsistency.Expected(tenant, mount));
            return ok;
        });
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
