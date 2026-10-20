using Biatec.Chat.Api.Evidence.Jwks;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Api.Evidence.Export;

public static class EvidenceExportEndpoints
{
    public const string Path = "/session/evidence";

    /// <summary>
    /// GET /session/evidence downloads the signed evidence bundle of the caller's own conversation. The conversation
    /// comes only from the session cookie, never from a parameter, so no one can export another conversation. It never
    /// creates a session. The widget's "Download evidence" link points here.
    /// </summary>
    public static IEndpointRouteBuilder MapEvidenceExport(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, async (HttpContext ctx, ChatDbContext db, IEvidenceLog log, IEvidenceKeyStore keys, TimeProvider clock, IConfiguration config) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers.Append("Vary", "Cookie");
            var tenant = ctx.GetTenant()!;
            if (ctx.Request.Headers.ContainsKey("Origin") && Security.OriginPolicy.AllowedOrigin(tenant, ctx.Request) is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var token = SessionEndpoints.ReadCookie(ctx.Request);
            if (!SessionService.WellFormed(token)) return Results.Unauthorized();
            var id = SessionService.IdFromToken(token!);
            var now = clock.GetUtcNow();
            var session = await db.ChatSessions.AsNoTracking().Where(Sessions.SessionLiveness.IsLive(db, now))
                .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenant.Id, ctx.RequestAborted);
            if (session is null) return Results.Unauthorized();

            var chain = await log.GetChainAsync(session.ConversationId, ctx.RequestAborted);
            if (chain.Count == 0) return Results.Problem("This conversation has no evidence yet.", statusCode: StatusCodes.Status404NotFound);

            // Prefer the configured public base URL: behind a proxy the request scheme and host can be internal.
            var publicBase = config["Evidence:PublicBaseUrl"]?.Trim().TrimEnd('/');
            var jwksUrl = (string.IsNullOrEmpty(publicBase) ? $"{ctx.Request.Scheme}://{ctx.Request.Host}" : publicBase) + EvidenceJwks.JwksPath;
            var bundle = await EvidenceBundleBuilder.BuildAsync(session.ConversationId, chain, keys, jwksUrl, now, ctx.RequestAborted);
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"biatec-evidence-{session.ConversationId:N}.json\"";
            return Results.Content(bundle.ToJsonString(), "application/json");
        }).WithName("ExportEvidence").ExcludeFromDescription();
        return app;
    }
}
