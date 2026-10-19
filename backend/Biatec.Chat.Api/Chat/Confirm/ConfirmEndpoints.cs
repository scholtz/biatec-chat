using SessionTier = Biatec.Chat.Core.Data.SessionTier;
using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Signing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Chat.Confirm;

public sealed record ConfirmRequest
{
    /// <summary>The group hash of the step being confirmed, exactly as the preview card showed it.</summary>
    public string? GroupHash { get; init; }
}

public sealed record ConfirmResponse
{
    /// <summary>landed, submitted, consent_pending, sign_in_unavailable, scope_required, sign_in_required or failed.</summary>
    public required string Status { get; init; }
    public required string Message { get; init; }
    public IReadOnlyList<string> TxIds { get; init; } = [];
    /// <summary>Set when Status is consent_pending: the same shape the chat stream's consent_status event carries.</summary>
    public Contracts.ConsentStatus? Consent { get; init; }
    /// <summary>Set when Status is scope_required: the scope to pass to the elevation popup.</summary>
    public string? ReauthorizeScope { get; init; }
    /// <summary>Set after a settled x402 payment: the paid resource's content. Untrusted seller data, shown as data only.</summary>
    public string? ResourceContent { get; init; }
}

public static class ConfirmRegistration
{
    public static IServiceCollection AddConfirm(this IServiceCollection services)
    {
        services.AddOptions<ConfirmationOptions>().BindConfiguration(ConfirmationOptions.Section);
        services.TryAddScoped<ActionPreviewService>();
        services.TryAddScoped<ConfirmService>();
        services.TryAddSingleton<ITransactionConfirmer, AlgodTransactionConfirmer>();
        services.AddHttpClient(AlgodTransactionConfirmer.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        // Signing goes straight to Biatec OIDC (the configured issuer), never through Biatec MCP.
        services.AddHttpClient<IWalletSigningClient, WalletSigningClient>((sp, c) =>
        {
            c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<OidcOptions>>().Value.Issuer.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        return services;
    }
}

public static class ConfirmEndpoints
{
    /// <summary>
    /// POST /chat/previews/{previewId}/confirm. The global CSRF guard applies (custom header, registered Origin, client
    /// key). Only an authenticated session of the preview's own conversation can confirm.
    /// </summary>
    public static IEndpointRouteBuilder MapConfirm(this IEndpointRouteBuilder app)
    {
        app.MapPost("/chat/previews/{previewId:guid}/confirm", async (Guid previewId, ConfirmRequest? body, HttpContext ctx,
            Biatec.Chat.Core.Data.ChatDbContext db, ConfirmService confirm, TimeProvider clock) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var tenant = ctx.GetTenant()!;
            var session = await AuthEndpoints.FindLiveAsync(db, tenant, SessionEndpoints.ReadCookie(ctx.Request), clock.GetUtcNow(), ctx.RequestAborted);
            if (session is null || session.Tier == SessionTier.Anonymous) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(body?.GroupHash)) return Results.Problem("groupHash is required.", statusCode: StatusCodes.Status400BadRequest);

            ConfirmResult r;
            try { r = await confirm.ConfirmAsync(tenant, session, previewId, body.GroupHash, ctx.RequestAborted); }
            catch (PreviewException ex)
            {
                var status = ex.Error switch
                {
                    PreviewError.NotFound => StatusCodes.Status404NotFound,
                    PreviewError.Expired => StatusCodes.Status410Gone,
                    _ => StatusCodes.Status409Conflict,
                };
                return Results.Json(new { error = ex.Error.ToString(), message = ex.Message }, statusCode: status);
            }

            return ToHttp(r);
        }).WithName("ConfirmPreview");

        // POST /chat/previews/{previewId}/reconcile: re-checks a Submitted step on chain (lazy reconciliation).
        app.MapPost("/chat/previews/{previewId:guid}/reconcile", async (Guid previewId, HttpContext ctx,
            Biatec.Chat.Core.Data.ChatDbContext db, ConfirmService confirm, TimeProvider clock) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var tenant = ctx.GetTenant()!;
            var session = await AuthEndpoints.FindLiveAsync(db, tenant, SessionEndpoints.ReadCookie(ctx.Request), clock.GetUtcNow(), ctx.RequestAborted);
            if (session is null || session.Tier == SessionTier.Anonymous) return Results.Unauthorized();
            try { return ToHttp(await confirm.ReconcileAsync(session, previewId, ctx.RequestAborted)); }
            catch (PreviewException ex)
            {
                var status = ex.Error == PreviewError.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict;
                return Results.Json(new { error = ex.Error.ToString(), message = ex.Message }, statusCode: status);
            }
        }).WithName("ReconcilePreview");

        return app;
    }

    private static IResult ToHttp(ConfirmResult r)
    {
            var (code, label) = r.Outcome switch
            {
                ConfirmOutcome.Landed => (StatusCodes.Status200OK, "landed"),
                ConfirmOutcome.Submitted => (StatusCodes.Status202Accepted, "submitted"),
                ConfirmOutcome.ConsentPending => (StatusCodes.Status202Accepted, "consent_pending"),
                ConfirmOutcome.SignInUnavailable => (StatusCodes.Status503ServiceUnavailable, "sign_in_unavailable"),
                ConfirmOutcome.ScopeRequired => (StatusCodes.Status403Forbidden, "scope_required"),
                ConfirmOutcome.SignInRequired => (StatusCodes.Status401Unauthorized, "sign_in_required"),
                _ => (StatusCodes.Status422UnprocessableEntity, "failed"),
            };
            return Results.Json(new ConfirmResponse
            {
                Status = label, Message = r.Message, TxIds = r.TxIds, Consent = r.Consent, ReauthorizeScope = r.ReauthorizeScope, ResourceContent = r.ResourceContent,
            }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
            }, statusCode: code);
    }
}
