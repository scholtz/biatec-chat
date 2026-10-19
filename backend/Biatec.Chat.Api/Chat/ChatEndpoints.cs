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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
            var handle = await sessions.ResolveOrCreateAsync(tenant, SessionEndpoints.ReadCookie(ctx.Request), ctx.RequestAborted);
            var session = handle.Session;
            if (handle.NewToken is not null) SessionEndpoints.WriteCookie(ctx.Response, handle.NewToken, session.AbsoluteExpiresAt, clock.GetUtcNow());

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

            var turn = new ChatTurn(session.ConversationId, session.Id.ToString("N"), tenant, session.Tier, scopes, body.Message, body.Context);

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
            return Results.Empty;
        }).WithName("PostChatMessage").Produces(StatusCodes.Status200OK, contentType: "text/event-stream");

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
        services.TryAddSingleton(_ => CapabilityRegistry.CreateDefault());
        services.TryAddSingleton<IPolicyEventSink, InMemoryPolicyEventSink>();
        services.TryAddSingleton<ToolGate>();
        services.TryAddSingleton<IMessageRedactor, DefaultMessageRedactor>();
        services.TryAddScoped<ConversationOrchestrator>();
        return services;
    }
}
