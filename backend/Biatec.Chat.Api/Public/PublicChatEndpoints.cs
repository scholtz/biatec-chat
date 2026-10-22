using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Biatec.Chat.Api.Chat;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Security;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Public;

/// <summary>Distinct refusal codes of the public transport (the widget keys off these).</summary>
public static class PublicCodes
{
    public const string RateLimited = "rate_limited";
    public const string UnknownConversation = "conversation_unknown";
    public const string Adopted = "conversation_adopted";
    public const string Authenticated = "conversation_authenticated";
    public const string IdleClosed = "conversation_idle_closed";
    public const string ContinuationRequired = "continuation_required";
    public const string ContinuationInvalid = "continuation_invalid";
    public const string SessionHasConversation = "session_has_conversation";
    public const string Busy = "turn_in_progress";
    public const string InvalidMessage = "invalid_message";
}

public sealed record AdoptRequest(string? ConversationId, string? ContinuationToken);

/// <summary>Body of POST /public/chat: the shared chat request plus the continuation token.</summary>
public sealed record PublicChatRequest
{
    public required string Message { get; init; }
    public string? ConversationId { get; init; }
    public string? ContinuationToken { get; init; }
    public HostContext? Context { get; init; }
    public string? WidgetVersion { get; init; }
}

/// <summary>
/// Anonymous, cookie-less chat. CORS is the non-credentialed wildcard from <see cref="CorsMiddleware"/>; CSRF does not
/// apply (no ambient credential). Only the anonymous tier is ever offered: the turn is built here with that tier
/// and no sign-in, so tier gates in the orchestrator hold regardless of the model.
/// </summary>
public static class PublicChatEndpoints
{
    private static readonly ConcurrentDictionary<Guid, byte> InFlight = new();

    public static IServiceCollection AddPublicChat(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<PublicChatOptions>(config.GetSection(PublicChatOptions.Section));
        services.TryAddSingleton<IPublicRateLimiter, RedisPublicRateLimiter>();
        return services;
    }

    // 128-bit capability and 256-bit continuation token, base64url.
    internal static string NewSecret(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string HashOf(string purpose, string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(purpose + "|" + secret))).ToLowerInvariant();
    private static bool WellFormed(string? s, int max = 64) => s is { Length: >= 16 } && s.Length <= max && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool CapabilityMatches(PublicConversationState state, string capability) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(state.CapabilityHash), Encoding.ASCII.GetBytes(HashOf("cap", capability)));

    private static IResult Refuse(int status, string code, string message) => Results.Json(new { code, message }, statusCode: status);

    public static IEndpointRouteBuilder MapPublic(this IEndpointRouteBuilder app)
    {
        // Is the calling Origin registered for this tenant? Answers any origin; reveals nothing but a boolean.
        app.MapGet("/public/origin", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var registered = OriginPolicy.AllowedOrigin(ctx.GetTenant()!, ctx.Request) is not null;
            return Results.Ok(new { registered });
        }).WithName("GetPublicOrigin");

        app.MapPost("/public/chat", HandleChatAsync).WithName("PostPublicChat").Produces(StatusCodes.Status200OK, contentType: "text/event-stream");

        // On the cookie transport: the global CSRF middleware applies (custom header, registered Origin, client key).
        app.MapPost("/session/adopt", HandleAdoptAsync).WithName("AdoptPublicConversation").LimitSessionCreation();
        return app;
    }

    private static string? OriginOf(HttpRequest req) =>
        req.Headers.TryGetValue("Origin", out var v) && v.Count == 1 ? OriginPolicy.Canonicalize(v[0]) : null;

    private static async Task<IResult> HandleChatAsync(PublicChatRequest body, HttpContext ctx, ChatDbContext db, IPublicRateLimiter limiter,
        IOptions<PublicChatOptions> options, ConversationOrchestrator orchestrator, TimeProvider clock)
    {
        var ct = ctx.RequestAborted;
        var tenant = ctx.GetTenant()!;
        ctx.Response.Headers.CacheControl = "no-store";

        var rate = await limiter.TryAcquireAsync(ctx.Connection.RemoteIpAddress,
            OriginPolicy.AllowedOrigin(tenant, ctx.Request), !ctx.Request.Headers.ContainsKey("Origin"), ct);
        if (!rate.Allowed)
        {
            ctx.Response.Headers.RetryAfter = rate.RetryAfterSeconds.ToString();
            return Refuse(StatusCodes.Status429TooManyRequests, PublicCodes.RateLimited, "Too many messages. Please wait a moment.");
        }
        if (string.IsNullOrWhiteSpace(body.Message) || body.Message.Length > ChatEndpoints.MaxMessageLength)
            return Refuse(StatusCodes.Status400BadRequest, PublicCodes.InvalidMessage, "The message must be between 1 and 4000 characters.");

        var now = clock.GetUtcNow();
        var idle = TimeSpan.FromMinutes(Math.Max(1, options.Value.IdleMinutes));
        var newToken = NewSecret(32);
        var newHash = HashOf("cont", newToken);

        if (string.IsNullOrEmpty(body.ConversationId) && string.IsNullOrEmpty(body.ContinuationToken))
        {
            var capability = NewSecret(16);
            var id = Guid.NewGuid();
            db.Conversations.Add(new Conversation { Id = id, TenantId = tenant.Id, CreatedAt = now, LastMessageAt = now });
            db.PublicConversationStates.Add(new PublicConversationState
            {
                ConversationId = id,
                CapabilityHash = HashOf("cap", capability),
                ContinuationTokenHash = newHash,
            });
            await db.SaveChangesAsync(ct);
            InFlight.TryAdd(id, 0);
            return await StreamAsync(ctx, db, orchestrator, tenant, id, capability, newToken, body, false, 0);
        }

        if (!WellFormed(body.ConversationId))
            return Refuse(StatusCodes.Status404NotFound, PublicCodes.UnknownConversation, "Unknown conversation.");
        if (!WellFormed(body.ContinuationToken, 128))
            return Refuse(StatusCodes.Status409Conflict, PublicCodes.ContinuationRequired, "The latest continuation token is required.");
        var presented = HashOf("cont", body.ContinuationToken!);

        var state = await db.PublicConversationStates
            .FirstOrDefaultAsync(s => s.ContinuationTokenHash == presented || s.PreviousTokenHash == presented, ct);
        // Same answer for "no such token" and "wrong capability": a guessed identifier learns nothing.
        if (state is null || !CapabilityMatches(state, body.ConversationId!))
            return Refuse(StatusCodes.Status404NotFound, PublicCodes.UnknownConversation, "Unknown conversation.");

        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == state.ConversationId, ct);
        if (conv is null || conv.TenantId != tenant.Id)
            return Refuse(StatusCodes.Status404NotFound, PublicCodes.UnknownConversation, "Unknown conversation.");
        if (state.AdoptedAt is not null)
            return Refuse(StatusCodes.Status410Gone, PublicCodes.Adopted, "This conversation moved to a signed-in session.");
        if (conv.OidcSub is not null)
            return Refuse(StatusCodes.Status403Forbidden, PublicCodes.Authenticated, "This conversation is no longer anonymous.");
        if (conv.EndedAt is not null || now - conv.LastMessageAt >= idle)
        {
            if (conv.EndedAt is null)
                await db.Conversations.Where(c => c.Id == conv.Id && c.EndedAt == null).ExecuteUpdateAsync(u => u.SetProperty(c => c.EndedAt, now), ct);
            return Refuse(StatusCodes.Status410Gone, PublicCodes.IdleClosed, "This conversation was closed after a period of inactivity.");
        }

        var conversationId = state.ConversationId;
        if (!InFlight.TryAdd(conversationId, 0))
            return Refuse(StatusCodes.Status409Conflict, PublicCodes.Busy, "A previous message in this conversation is still being answered.");

        var streaming = false;
        try
        {
            bool replay;
            long replayAfterSeq = 0;
            if (state.ContinuationTokenHash == presented)
            {
                // Consume the current token atomically: exactly one concurrent request wins it.
                var userSeq = ((await db.Messages.Where(m => m.ConversationId == conversationId).MaxAsync(m => (long?)m.Seq, ct)) ?? 0) + 1;
                var n = await db.PublicConversationStates
                    .Where(s => s.ConversationId == conversationId && s.ContinuationTokenHash == presented && s.AdoptedAt == null)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(s => s.PreviousTokenHash, presented)
                        .SetProperty(s => s.ContinuationTokenHash, newHash)
                        .SetProperty(s => s.LastReplyRef, "seq:" + userSeq), ct);
                if (n != 1) return Refuse(StatusCodes.Status409Conflict, PublicCodes.ContinuationInvalid, "The continuation token was already used.");
                replay = false;
            }
            else
            {
                // The previous token: one replay of the lost reply. A fresh token is issued (the lost one is unknown to us)
                // and the previous token is spent, so a second replay is refused.
                if (state.LastReplyRef is not { } rref || !rref.StartsWith("seq:", StringComparison.Ordinal) || !long.TryParse(rref.AsSpan(4), out replayAfterSeq))
                    return Refuse(StatusCodes.Status409Conflict, PublicCodes.ContinuationInvalid, "The continuation token was already used.");
                var n = await db.PublicConversationStates
                    .Where(s => s.ConversationId == conversationId && s.PreviousTokenHash == presented && s.AdoptedAt == null)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(s => s.PreviousTokenHash, (string?)null)
                        .SetProperty(s => s.ContinuationTokenHash, newHash), ct);
                if (n != 1) return Refuse(StatusCodes.Status409Conflict, PublicCodes.ContinuationInvalid, "The continuation token was already used.");
                replay = true;
            }

            streaming = true; // StreamAsync releases the in-flight slot
            return await StreamAsync(ctx, db, orchestrator, tenant, conversationId, body.ConversationId!, newToken, body, replay, replayAfterSeq);
        }
        finally
        {
            if (!streaming) InFlight.TryRemove(conversationId, out _);
        }
    }

    private static async Task<IResult> StreamAsync(HttpContext ctx, ChatDbContext db, ConversationOrchestrator orchestrator, Core.Data.Tenant tenant,
        Guid conversationId, string capability, string newToken, PublicChatRequest body, bool replay, long replayAfterSeq)
    {
        var ct = ctx.RequestAborted;
        try
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Response.Headers["X-Biatec-Conversation"] = capability;
            ctx.Response.Headers["X-Biatec-Continuation"] = newToken;
            await ctx.Response.Body.FlushAsync(ct);

            try
            {
                if (replay)
                {
                    var texts = await db.Messages.AsNoTracking()
                        .Where(m => m.ConversationId == conversationId && m.Seq > replayAfterSeq && m.Role == MessageRole.Assistant && m.Content != "")
                        .OrderBy(m => m.Seq).Select(m => m.Content).ToListAsync(CancellationToken.None);
                    if (texts.Count == 0)
                        await ChatEndpoints.WriteEventAsync(ctx.Response, new ErrorEvent("reply_unavailable", "The earlier reply could not be recovered. Please ask again.", true), ct);
                    foreach (var t in texts) await ChatEndpoints.WriteEventAsync(ctx.Response, new TextDeltaEvent(t), ct);
                    return Results.Empty;
                }

                // Anonymous tier, no sign-in route, no vault scopes: the public transport can never be elevated.
                var turn = new ChatTurn(conversationId, "public:" + conversationId.ToString("N"), tenant, Core.Data.SessionTier.Anonymous, [], body.Message, body.Context,
                    SignInAvailable: false);
                await foreach (var ev in orchestrator.RunAsync(turn, ct))
                    await ChatEndpoints.WriteEventAsync(ctx.Response, ev, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Biatec.Chat.Api.Public")
                    .LogError("Public chat turn failed for conversation {Conversation}: {ErrorType}", conversationId, ex.GetType().Name);
                try { await ChatEndpoints.WriteEventAsync(ctx.Response, new ErrorEvent("internal", "Something went wrong. Please try again.", true), CancellationToken.None); }
                catch { /* the client is gone */ }
            }
            return Results.Empty;
        }
        finally { InFlight.TryRemove(conversationId, out _); }
    }

    /// <summary>
    /// Cookie transport: moves an anonymous conversation into the caller's cookie session. Needs the capability AND the
    /// live (unused) continuation token, so a stale identifier cannot be adopted. Adoption retires the identifier.
    /// </summary>
    private static async Task<IResult> HandleAdoptAsync(AdoptRequest body, HttpContext ctx, ChatDbContext db, ISessionService sessions,
        IOptions<PublicChatOptions> options, TimeProvider clock)
    {
        var ct = ctx.RequestAborted;
        var tenant = ctx.GetTenant()!;
        ctx.Response.Headers.CacheControl = "no-store";
        const string refused = "Cannot adopt this conversation.";
        if (!WellFormed(body.ConversationId) || !WellFormed(body.ContinuationToken, 128))
            return Refuse(StatusCodes.Status404NotFound, PublicCodes.UnknownConversation, refused);

        var presented = HashOf("cont", body.ContinuationToken!);
        var state = await db.PublicConversationStates.FirstOrDefaultAsync(s => s.ContinuationTokenHash == presented, ct);
        if (state is null || !CapabilityMatches(state, body.ConversationId!))
            return Refuse(StatusCodes.Status404NotFound, PublicCodes.UnknownConversation, refused);

        var now = clock.GetUtcNow();
        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == state.ConversationId, ct);
        if (conv is null || conv.TenantId != tenant.Id) return Refuse(StatusCodes.Status404NotFound, PublicCodes.UnknownConversation, refused);
        if (state.AdoptedAt is not null) return Refuse(StatusCodes.Status410Gone, PublicCodes.Adopted, refused);
        if (conv.OidcSub is not null) return Refuse(StatusCodes.Status403Forbidden, PublicCodes.Authenticated, refused);
        if (conv.EndedAt is not null || now - conv.LastMessageAt >= TimeSpan.FromMinutes(Math.Max(1, options.Value.IdleMinutes)))
            return Refuse(StatusCodes.Status410Gone, PublicCodes.IdleClosed, refused);
        if (InFlight.ContainsKey(conv.Id)) return Refuse(StatusCodes.Status409Conflict, PublicCodes.Busy, refused);

        var handle = await sessions.ResolveOrCreateAsync(tenant, SessionEndpoints.ReadCookie(ctx.Request), ct);
        if (handle.NewToken is not null) SessionEndpoints.WriteCookie(ctx.Response, handle.NewToken, handle.Session.AbsoluteExpiresAt, now);

        // Every cookie session starts with an empty placeholder conversation; that one is replaced (and ended). A session
        // whose conversation already has messages keeps it: adoption never silently unlinks real history.
        var current = handle.Session.ConversationId;
        if (current != conv.Id && await db.Messages.AnyAsync(m => m.ConversationId == current, ct))
            return Refuse(StatusCodes.Status409Conflict, PublicCodes.SessionHasConversation, refused);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Retire the identifier first; only one adopter can win.
        var won = await db.PublicConversationStates
            .Where(s => s.ConversationId == conv.Id && s.ContinuationTokenHash == presented && s.AdoptedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.AdoptedAt, now), ct);
        if (won != 1) return Refuse(StatusCodes.Status409Conflict, PublicCodes.ContinuationInvalid, refused);
        if (current != conv.Id)
            await db.Conversations.Where(c => c.Id == current && c.EndedAt == null).ExecuteUpdateAsync(u => u.SetProperty(c => c.EndedAt, now), ct);
        var sub = handle.Session.OidcSub;
        await db.ChatSessions.Where(x => x.Id == handle.Session.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ConversationId, conv.Id), ct);
        if (sub is not null)
            await db.Conversations.Where(c => c.Id == conv.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.OidcSub, sub), ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { conversationId = conv.Id });
    }
}
