using Biatec.Chat.Api.Tenancy;

namespace Biatec.Chat.Api.Security;

/// <summary>
/// Credentialed CORS with the exact origin echoed, never a wildcard on a credentialed route. Origins outside the
/// tenant allow-list get no CORS headers (and a 403 on a preflight). /public/* is cookie-less and answers any
/// origin with a non-credentialed wildcard.
/// </summary>
public sealed class CorsMiddleware(RequestDelegate next)
{
    private const string AllowedHeaders = "Content-Type, " + OriginPolicy.CsrfHeader + ", " + OriginPolicy.ClientKeyHeader + ", Last-Event-ID";

    /// <summary>Response headers cross-origin script must read on /public/chat (the anonymous capability and continuation token).</summary>
    public const string PublicExposedHeaders = "X-Biatec-Conversation, X-Biatec-Continuation";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var tenant = ctx.GetTenant();
        var hasOrigin = req.Headers.ContainsKey("Origin");
        var isPreflight = HttpMethods.IsOptions(req.Method) && hasOrigin && req.Headers.ContainsKey("Access-Control-Request-Method");

        if (hasOrigin) res.Headers.Append("Vary", "Origin");

        if (tenant is not null && hasOrigin)
        {
            if (OriginPolicy.IsPublic(req.Path) && !OriginPolicy.IsOperatorOnboarding(req.Path))
            {
                res.Headers["Access-Control-Allow-Origin"] = "*";
                res.Headers["Access-Control-Expose-Headers"] = PublicExposedHeaders;
            }
            else if (OriginPolicy.AllowedOrigin(tenant, req) is { } origin)
            {
                res.Headers["Access-Control-Allow-Origin"] = origin;
                res.Headers["Access-Control-Allow-Credentials"] = "true";
            }
            else if (isPreflight)
            {
                res.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (isPreflight)
            {
                res.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, PATCH, DELETE";
                res.Headers["Access-Control-Allow-Headers"] = AllowedHeaders;
                res.Headers["Access-Control-Max-Age"] = "600";
                res.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
        }

        await next(ctx);
    }
}
