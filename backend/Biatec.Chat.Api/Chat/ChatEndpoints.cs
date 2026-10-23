using System.Text.Json;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Biatec.Chat.Api.Chat;

public static class ChatEndpoints
{
    public const int MaxMessageLength = 4000;
    /// <summary>Problem <c>code</c> when the request's session cookie no longer names a live session (T-113).</summary>
    public const string SessionExpiredCode = "session_expired";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // One turn at a time per conversation (per process): concurrent turns would race on Seq and interleave history.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> TurnsInFlight = new();

    /// <summary>
    /// POST /chat/messages streams the reply as server-sent events: one <c>data:</c> line per <see cref="ChatEvent"/>,
    /// with the event name equal to its <c>type</c>. The CSRF guard (custom header, registered Origin, client key) is
    /// the global middleware, because the route is outside /public.
    /// </summary>
    public static IEndpointRouteBuilder MapChat(this IEndpointRouteBuilder app)
    {
        app.MapPost("/chat/messages", async (ChatMessageRequest body, HttpContext ctx, ISessionService sessions, ITokenVault vault,
            ConversationOrchestrator orchestrator, TimeProvider clock) =>
        {
            if (string.IsNullOrWhiteSpace(body.Message) || body.Message.Length > MaxMessageLength)
                return Results.Problem("The message must be between 1 and 4000 characters.", statusCode: StatusCodes.Status400BadRequest);

            var tenant = ctx.GetTenant()!;
            var cookie = SessionEndpoints.ReadCookie(ctx.Request);
            var handle = await sessions.ResolveOrCreateAsync(tenant, cookie, ctx.RequestAborted);
            var session = handle.Session;
            if (handle.NewToken is not null) SessionEndpoints.WriteCookie(ctx.Response, handle.NewToken, session.AbsoluteExpiresAt, clock.GetUtcNow());

            // The cookie named a session that has ended (idle or absolute lifetime). Running the turn on the fresh
            // anonymous session would answer a signed-in user as a guest; the widget refreshes its state and decides.
            if (handle.Created && SessionService.WellFormed(cookie))
                return Results.Problem("The session has ended. Start again.", statusCode: StatusCodes.Status401Unauthorized,
                    extensions: new Dictionary<string, object?> { ["code"] = SessionExpiredCode });

            // A client-supplied conversation id only ever selects the session's own conversation; any other value is refused.
            if (body.ConversationId is not null && !string.Equals(body.ConversationId, session.ConversationId.ToString(), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(body.ConversationId, session.ConversationId.ToString("N"), StringComparison.OrdinalIgnoreCase))
                return Results.Problem("Unknown conversation.", statusCode: StatusCodes.Status404NotFound);

            // Scopes come from the server-side vault, never from the browser.
            IReadOnlyCollection<string> scopes = [];
            if (session.Tier != Core.Data.SessionTier.Anonymous)
            {
                var entry = await vault.GetAsync(session.Id.ToString("N"), ctx.RequestAborted);
                scopes = entry?.Scopes ?? [];
            }

            var turn = new ChatTurn(session.ConversationId, session.Id.ToString("N"), tenant, session.Tier, scopes, body.Message, body.Context,
                SignInAvailable: true, session.AgentMode, session.Paused, ctx.Request.PathBase.Value ?? ""); // cookie transport; /public/chat must pass false

            if (!TurnsInFlight.TryAdd(session.ConversationId, 0))
                return Results.Problem("A previous message in this conversation is still being answered.", statusCode: StatusCodes.Status409Conflict);

            try
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers.CacheControl = "no-store";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";
                ctx.Response.Headers["X-Biatec-Conversation"] = session.ConversationId.ToString();
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

                try
                {
                    await foreach (var ev in orchestrator.RunAsync(turn, ctx.RequestAborted))
                        await WriteEventAsync(ctx.Response, ev, ctx.RequestAborted);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    // Headers are already sent, so tell the widget in-band. Log the type only: the text may hold user data.
                    ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Biatec.Chat.Api.Chat.ChatEndpoints")
                        .LogError("Chat turn failed for conversation {Conversation}: {ErrorType}", session.ConversationId, ex.GetType().Name);
                    try { await WriteEventAsync(ctx.Response, new ErrorEvent("internal", "Something went wrong. Please try again.", true), CancellationToken.None); }
                    catch { /* the client is gone */ }
                }
                return Results.Empty;
            }
            finally { TurnsInFlight.TryRemove(session.ConversationId, out _); }
        }).WithName("PostChatMessage").Produces(StatusCodes.Status200OK, contentType: "text/event-stream").LimitSessionCreation();

        return app;
    }

    internal static async Task WriteEventAsync(HttpResponse res, ChatEvent ev, CancellationToken ct)
    {
        var data = JsonSerializer.Serialize(ev, Json);
        var type = JsonDocument.Parse(data).RootElement.GetProperty("type").GetString();
        await res.WriteAsync($"event: {type}\ndata: {data}\n\n", ct);
        await res.Body.FlushAsync(ct);
    }
}

public static class ChatRegistration
{
    public static IServiceCollection AddChat(this IServiceCollection services)
    {
        // Wallet:BitcoinTransfers (default false) offers network "bitcoin" on wallet.transfer. Leave it off until a PSBT signing path exists (BCHAT-037).
        services.TryAddSingleton(sp => CapabilityRegistry.CreateDefault(sp.GetService<IConfiguration>()?.GetValue<bool>("Wallet:BitcoinTransfers") ?? false));
        services.TryAddSingleton<IPolicyEventSink, InMemoryPolicyEventSink>();
        services.TryAddSingleton<ToolGate>();
        services.TryAddSingleton<IMessageRedactor, DefaultMessageRedactor>();
        services.TryAddScoped<ConversationOrchestrator>();
        return services;
    }
}
