using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Biatec.Chat.Api.Chat.Cancel;

public sealed record CancelResponse
{
    /// <summary>Always <c>cancelled</c> on a 200.</summary>
    public required string Status { get; init; }
    public required string Message { get; init; }
}

public static class CancelRegistration
{
    public static IServiceCollection AddCancel(this IServiceCollection services)
    {
        services.TryAddScoped<CancelService>();
        return services;
    }
}

public static class CancelEndpoints
{
    /// <summary>
    /// POST /chat/previews/{previewId}/cancel. The global CSRF guard applies. Only an authenticated session of the
    /// preview's own conversation can cancel. Allowed while the agent is paused or analyze-only.
    /// </summary>
    public static IEndpointRouteBuilder MapCancel(this IEndpointRouteBuilder app)
    {
        app.MapPost("/chat/previews/{previewId:guid}/cancel", async (Guid previewId, ActionDecisionRequest body, HttpContext ctx,
            ChatDbContext db, CancelService cancel, TimeProvider clock) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var tenant = ctx.GetTenant()!;
            var session = await AuthEndpoints.FindLiveAsync(db, tenant, SessionEndpoints.ReadCookie(ctx.Request), clock.GetUtcNow(), ctx.RequestAborted);
            if (session is null || session.Tier == Biatec.Chat.Core.Data.SessionTier.Anonymous) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(body.GroupHash)) return Results.Problem("groupHash is required.", statusCode: StatusCodes.Status400BadRequest);

            var r = await cancel.CancelAsync(session, previewId, body.GroupHash, ctx.RequestAborted);
            return r.Outcome switch
            {
                CancelOutcome.Cancelled or CancelOutcome.AlreadyCancelled => Results.Json(new CancelResponse { Status = r.Status!, Message = r.Message }),
                CancelOutcome.NotFound => Results.Json(new { error = "NotFound", message = r.Message }, statusCode: StatusCodes.Status404NotFound),
                CancelOutcome.Foreign => Results.Unauthorized(),
                _ => Results.Json(new { error = r.Outcome.ToString(), message = r.Message }, statusCode: StatusCodes.Status409Conflict),
            };
        }).WithName("CancelPreview");
        return app;
    }
}
