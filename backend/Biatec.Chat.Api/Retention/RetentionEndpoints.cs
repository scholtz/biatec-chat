using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Evidence.Export;
using Biatec.Chat.Api.Evidence.Jwks;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Retention;

public sealed record ConversationSummary
{
    public required Guid ConversationId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastMessageAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    /// <summary>The conversation of the calling session.</summary>
    public required bool Current { get; init; }
    public required int EvidenceEntries { get; init; }
}

public sealed record ConversationList
{
    public required IReadOnlyList<ConversationSummary> Conversations { get; init; }
}

public sealed record ErasureResponse
{
    /// <summary>Always <c>erased</c> on a 200.</summary>
    public required string Status { get; init; }
    public required int Conversations { get; init; }
    public required int PayloadsDeleted { get; init; }
    /// <summary>The session's fresh conversation: later turns are written there, so they can be erased again.</summary>
    public string? ConversationId { get; init; }
}

public static class RetentionRegistration
{
    public static IServiceCollection AddRetention(this IServiceCollection services)
    {
        services.AddOptions<RetentionOptions>().BindConfiguration(RetentionOptions.Section);
        services.AddScoped<RetentionService>();
        services.AddHostedService<RetentionHostedService>();
        return services;
    }
}

public static class RetentionEndpoints
{
    public const string ListPath = "/session/conversations";
    public const string ErasePath = "/session/erase";

    /// <summary>
    /// The widget's export view and the user's erasure. None of these creates a session; each needs a live session
    /// cookie. GET /session/conversations lists the caller's own conversations: every conversation of the signed-in
    /// user in this tenant, or only the session's own for a guest. GET /session/conversations/{id}/evidence downloads
    /// the bundle of one of those. POST /session/erase (signed-in only; the global CSRF guard applies: custom header,
    /// registered Origin, client key) removes payloads and salts immediately, blanks the fields the spec lists, and moves the
    /// session to a fresh conversation whose id the response returns.
    /// </summary>
    public static IEndpointRouteBuilder MapRetention(this IEndpointRouteBuilder app)
    {
        app.MapGet(ListPath, async (HttpContext ctx, ChatDbContext db, TimeProvider clock) =>
        {
            NoStore(ctx);
            var tenant = ctx.GetTenant()!;
            if (ForeignOrigin(ctx, tenant)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var session = await AuthEndpoints.FindLiveAsync(db, tenant, SessionEndpoints.ReadCookie(ctx.Request), clock.GetUtcNow(), ctx.RequestAborted);
            if (session is null) return Results.Unauthorized();

            var list = await Owned(db, session)
                .OrderByDescending(c => c.LastMessageAt)
                .Select(c => new ConversationSummary
                {
                    ConversationId = c.Id,
                    CreatedAt = c.CreatedAt,
                    LastMessageAt = c.LastMessageAt,
                    EndedAt = c.EndedAt,
                    Current = c.Id == session.ConversationId,
                    EvidenceEntries = db.EvidenceEntries.Count(e => e.ConversationId == c.Id),
                })
                .ToListAsync(ctx.RequestAborted);
            return Results.Ok(new ConversationList { Conversations = list });
        }).WithName("ListOwnConversations").Produces<ConversationList>();

        app.MapGet(ListPath + "/{conversationId:guid}/evidence", async (Guid conversationId, HttpContext ctx, ChatDbContext db,
            IEvidenceLog log, IEvidenceKeyStore keys, TimeProvider clock, IConfiguration config) =>
        {
            NoStore(ctx);
            var tenant = ctx.GetTenant()!;
            if (ForeignOrigin(ctx, tenant)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var now = clock.GetUtcNow();
            var session = await AuthEndpoints.FindLiveAsync(db, tenant, SessionEndpoints.ReadCookie(ctx.Request), now, ctx.RequestAborted);
            if (session is null) return Results.Unauthorized();
            // Someone else's conversation answers exactly like a missing one.
            if (!await Owned(db, session).AnyAsync(c => c.Id == conversationId, ctx.RequestAborted))
                return Results.Problem("No such conversation.", statusCode: StatusCodes.Status404NotFound);

            var chain = await log.GetChainAsync(conversationId, ctx.RequestAborted);
            if (chain.Count == 0) return Results.Problem("This conversation has no evidence yet.", statusCode: StatusCodes.Status404NotFound);
            var jwksUrl = EvidenceJwks.PublicUrl(ctx, config);
            var bundle = await EvidenceBundleBuilder.BuildAsync(conversationId, chain, keys, jwksUrl, now, ctx.RequestAborted);
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"biatec-evidence-{conversationId:N}.json\"";
            return Results.Content(bundle.ToJsonString(), "application/json");
        }).WithName("ExportOwnConversationEvidence").ExcludeFromDescription();

        app.MapPost(ErasePath, async (HttpContext ctx, ChatDbContext db, RetentionService retention, TimeProvider clock) =>
        {
            NoStore(ctx);
            var tenant = ctx.GetTenant()!;
            var session = await AuthEndpoints.FindLiveAsync(db, tenant, SessionEndpoints.ReadCookie(ctx.Request), clock.GetUtcNow(), ctx.RequestAborted);
            // Anonymous conversations cannot be tied to a requester: they are removed only by the 30-day retention.
            if (session is null || session.Tier == SessionTier.Anonymous || string.IsNullOrEmpty(session.OidcSub)) return Results.Unauthorized();

            var r = await retention.EraseAsync(tenant.Id, session.OidcSub, session.Id, ctx.RequestAborted);
            return r.Outcome == ErasureOutcome.ActionInFlight
                ? Results.Json(new { error = "ActionInFlight", message = "Cancel or finish the open action first, then erase your data." }, statusCode: StatusCodes.Status409Conflict)
                : Results.Ok(new ErasureResponse { Status = "erased", Conversations = r.Conversations, PayloadsDeleted = r.PayloadsDeleted,
                    ConversationId = r.NewConversationId?.ToString() });
        }).WithName("EraseOwnData").Produces<ErasureResponse>();

        return app;
    }

    /// <summary>The session's own conversation, plus, when signed in, every conversation of the same user in this tenant.</summary>
    private static IQueryable<Conversation> Owned(ChatDbContext db, ChatSession s)
    {
        var sub = s.Tier == SessionTier.Anonymous ? null : s.OidcSub;
        return db.Conversations.AsNoTracking().Where(c => c.TenantId == s.TenantId
            && (c.Id == s.ConversationId || (sub != null && c.OidcSub == sub)));
    }

    private static bool ForeignOrigin(HttpContext ctx, Tenant tenant) =>
        ctx.Request.Headers.ContainsKey("Origin") && Security.OriginPolicy.AllowedOrigin(tenant, ctx.Request) is null;

    private static void NoStore(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Append("Vary", "Cookie");
    }
}

/// <summary>
/// Runs the retention rules once <see cref="RetentionOptions.StartupDelay"/> after startup, then every
/// <see cref="RetentionOptions.Interval"/>. A failure is logged by type and the loop goes on.
/// </summary>
public sealed class RetentionHostedService(IServiceScopeFactory scopes, IOptions<RetentionOptions> options, TimeProvider clock, ILogger<RetentionHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        try
        {
            await Task.Delay(options.Value.StartupDelay, clock, stoppingToken);
            using var timer = new PeriodicTimer(options.Value.Interval, clock);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var r = await scope.ServiceProvider.GetRequiredService<RetentionService>().ApplyAsync(stoppingToken);
                    if (r.PayloadsDeleted + r.TranscriptsCleared + r.AnonymousRemoved + r.RecordsRemoved > 0)
                        log.LogInformation("Retention: {Payloads} payloads deleted, {Messages} messages cleared, {Anonymous} anonymous and {Records} expired conversations removed",
                            r.PayloadsDeleted, r.TranscriptsCleared, r.AnonymousRemoved, r.RecordsRemoved);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError("Retention run failed: {ErrorType}", ex.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}
