using Biatec.Chat.Api.Tenancy;

namespace Biatec.Chat.Api.Security;

/// <summary>
/// State-changing requests on the cookie transport need the custom header and an Origin in the tenant allow-list.
/// The clientKey (a label, not a secret) must be one of the tenant's registered keys on that transport: it is
/// required on state-changing requests and validated whenever it is sent. /public/* has no cookie and is exempt.
/// A missing Origin is refused, since browsers always send it on a cross-origin POST.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var req = ctx.Request;
        var tenant = ctx.GetTenant();
        var path = req.Path;

        var safe = HttpMethods.IsGet(req.Method) || HttpMethods.IsHead(req.Method) || HttpMethods.IsOptions(req.Method);
        // Only GET navigations are exempt; a POST under /auth still needs the header and origin.
        if (tenant is null || path.StartsWithSegments("/health") || OriginPolicy.IsPublic(path) || OriginPolicy.IsAdmin(path) || (safe && OriginPolicy.IsNavigationOrMetadata(path)))
        {
            await next(ctx);
            return;
        }

        if (!safe)
        {
            var header = req.Headers[OriginPolicy.CsrfHeader];
            if (header.Count != 1 || string.IsNullOrWhiteSpace(header[0]) || OriginPolicy.AllowedOrigin(tenant, req) is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        var key = req.Headers[OriginPolicy.ClientKeyHeader];
        if (!safe || key.Count > 0)
        {
            if (key.Count != 1 || string.IsNullOrEmpty(key[0]) || !ClientKeys.Contains(tenant, key[0]!))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        await next(ctx);
    }
}
