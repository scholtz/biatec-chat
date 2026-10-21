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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);

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
        WarnWhenMountAndRedirectUriDisagree(ctx, tenant);
        await next(ctx);
    }

    /// <summary>
    /// The OIDC redirect URI is the exact stored value, so a mounted host's tenant must say
    /// <c>https://host/chat/auth/callback</c>. One that says otherwise completes sign-in at the wrong application, so it
    /// is logged as an error once per tenant (the popup URLs themselves follow the mount, not the stored value).
    /// </summary>
    private void WarnWhenMountAndRedirectUriDisagree(HttpContext ctx, Biatec.Chat.Core.Data.Tenant tenant)
    {
        if (!ctx.Request.PathBase.HasValue) return;
        var mount = ctx.Request.PathBase.Value!;
        var expected = Hosting.BackendUrls.TryBase(tenant, mount) + "/auth/callback";
        if (string.Equals(tenant.RedirectUri, expected, StringComparison.OrdinalIgnoreCase)) return;
        if (_warned.TryAdd(tenant.Id, 0))
            log.LogError("Tenant {TenantId} is served under the mount {Mount} but its redirect URI is {RedirectUri}, not {Expected}: Biatec OIDC will send the sign-in callback to the wrong application.",
                tenant.Id, mount, tenant.RedirectUri, expected);
    }
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
