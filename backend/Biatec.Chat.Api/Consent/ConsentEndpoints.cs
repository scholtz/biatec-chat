using System.Text.Json;
using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Chat.Confirm;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Consent;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Consent;

/// <summary>
/// The line the preview card shows about phone approval: predicted from Biatec OIDC <c>GET /consent/settings</c>, or the
/// fallback "may need approval on your phone" when that endpoint is missing or fails.
/// </summary>
public sealed class ConsentPredictor(ITokenVault vault, IConsentSettingsClient settings, IOptions<OidcOptions> oidc, ILogger<ConsentPredictor> log)
{
    public async Task<string> PredictAsync(ChatSession session, decimal? valueEur, CancellationToken ct)
    {
        try
        {
            var bearer = await vault.GetAccessTokenAsync(session.Id.ToString("N"), ct);
            if (bearer is null) return ConsentPrediction.FallbackText;
            var endpoint = new Uri(oidc.Value.Issuer.TrimEnd('/') + "/consent/settings");
            return ConsentPrediction.Describe(await settings.GetAsync(endpoint, bearer, ct), valueEur);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Consent prediction fell back: {ErrorType}", ex.GetType().Name);
            return ConsentPrediction.FallbackText;
        }
    }
}

public sealed record ConsentPollResponse
{
    public required Contracts.ConsentStatus Consent { get; init; }
    /// <summary>Set once the consent has ended: the outcome of the confirm path (landed, submitted, failed).</summary>
    public ConfirmResponse? Result { get; init; }
}

public static class ConsentRegistration
{
    public static IServiceCollection AddConsent(this IServiceCollection services)
    {
        services.AddHttpClient<ICibaTokenClient, CibaTokenClient>(c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient<IConsentSettingsClient, ConsentSettingsClient>(c => c.Timeout = TimeSpan.FromSeconds(5));
        services.AddOptions<ConsentStreamOptions>();
        services.TryAddScoped<ConsentOrchestrator>();
        services.TryAddScoped<ConsentPredictor>();
        return services;
    }
}

public static class ConsentEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// POST /chat/previews/{previewId}/consent polls once. POST /chat/previews/{previewId}/consent/stream is the consent
    /// status SSE: a <c>consent_status</c> event per poll, then a <c>confirm_result</c> event when the consent has ended.
    /// GET /chat/previews/{previewId}/consent/prediction is the card text. The global CSRF guard and the session cookie apply.
    /// </summary>
    public static IEndpointRouteBuilder MapConsent(this IEndpointRouteBuilder app)
    {
        app.MapPost("/chat/previews/{previewId:guid}/consent", async (Guid previewId, HttpContext ctx, ChatDbContext db, ConsentOrchestrator consent, TimeProvider clock) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var tenant = ctx.GetTenant()!;
            var session = await AuthEndpoints.FindLiveAsync(db, tenant, SessionEndpoints.ReadCookie(ctx.Request), clock.GetUtcNow(), ctx.RequestAborted);
            if (session is null || session.Tier == Biatec.Chat.Core.Data.SessionTier.Anonymous) return Results.Unauthorized();
            try
            {
                var step = await consent.AdvanceAsync(tenant, session, previewId, ctx.RequestAborted);
                return Results.Json(new ConsentPollResponse { Consent = step.Status, Result = step.Result is null ? null : ToResponse(step.Result) }, Json);
            }
            catch (PreviewException ex) { return Problem(ex); }
        }).WithName("PollConsent");

        app.MapPost("/chat/previews/{previewId:guid}/consent/stream", async (Guid previewId, HttpContext ctx, ChatDbContext db, ConsentOrchestrator consent, TimeProvider clock, IOptions<ConsentStreamOptions> options) =>
        {
            var tenant = ctx.GetTenant()!;
            var session = await AuthEndpoints.FindLiveAsync(db, tenant, SessionEndpoints.ReadCookie(ctx.Request), clock.GetUtcNow(), ctx.RequestAborted);
            if (session is null || session.Tier == Biatec.Chat.Core.Data.SessionTier.Anonymous) return Results.Unauthorized();

            ConsentAdvance first;
            try { first = await consent.AdvanceAsync(tenant, session, previewId, ctx.RequestAborted); }
            catch (PreviewException ex) { return Problem(ex); }

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            try
            {
                var step = first;
                while (true)
                {
                    await WriteAsync(ctx.Response, "consent_status", new ConsentStatusEvent(step.Status), ctx.RequestAborted);
                    if (step.Terminal)
                    {
                        if (step.Result is not null) await WriteAsync(ctx.Response, "confirm_result", ToResponse(step.Result), ctx.RequestAborted);
                        break;
                    }
                    await Task.Delay(options.Value.Interval, clock, ctx.RequestAborted);
                    step = await consent.AdvanceAsync(tenant, session, previewId, ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            catch (PreviewException ex)
            {
                try { await WriteAsync(ctx.Response, "error", new { error = ex.Error.ToString(), message = ex.Message }, CancellationToken.None); }
                catch (Exception) { }
            }
            return Results.Empty;
        }).WithName("StreamConsent").Produces(StatusCodes.Status200OK, contentType: "text/event-stream");

        app.MapGet("/chat/previews/{previewId:guid}/consent/prediction", async (Guid previewId, decimal? valueEur, HttpContext ctx, ChatDbContext db, ConsentPredictor predictor, TimeProvider clock) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var tenant = ctx.GetTenant()!;
            var session = await AuthEndpoints.FindLiveAsync(db, tenant, SessionEndpoints.ReadCookie(ctx.Request), clock.GetUtcNow(), ctx.RequestAborted);
            if (session is null || session.Tier == Biatec.Chat.Core.Data.SessionTier.Anonymous) return Results.Unauthorized();
            return Results.Json(new { text = await predictor.PredictAsync(session, valueEur, ctx.RequestAborted) }, Json);
        }).WithName("ConsentPrediction");

        return app;
    }

    private static IResult Problem(PreviewException ex) => Results.Json(new { error = ex.Error.ToString(), message = ex.Message },
        statusCode: ex.Error == PreviewError.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict);

    internal static ConfirmResponse ToResponse(ConfirmResult r) => new()
    {
        Status = r.Outcome switch
        {
            ConfirmOutcome.AgentRestricted => r.ErrorCode ?? "agent_analyze_only",
            ConfirmOutcome.Landed => "landed",
            ConfirmOutcome.Submitted => "submitted",
            ConfirmOutcome.ConsentPending => "consent_pending",
            ConfirmOutcome.SignInUnavailable => "sign_in_unavailable",
            ConfirmOutcome.ScopeRequired => "scope_required",
            ConfirmOutcome.SignInRequired => "sign_in_required",
            _ => "failed",
        },
        Message = r.Message, TxIds = r.TxIds, Consent = r.Consent, ReauthorizeScope = r.ReauthorizeScope, ResourceContent = r.ResourceContent,
    };

    private static async Task WriteAsync(HttpResponse res, string type, object payload, CancellationToken ct)
    {
        var data = JsonSerializer.Serialize(payload, payload is ChatEvent ? typeof(ChatEvent) : payload.GetType(), Json);
        await res.WriteAsync($"event: {type}\ndata: {data}\n\n", ct);
        await res.Body.FlushAsync(ct);
    }
}

public sealed class ConsentStreamOptions
{
    /// <summary>Wait between polls of the token endpoint. The authenticator flow answers within seconds.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);
}
