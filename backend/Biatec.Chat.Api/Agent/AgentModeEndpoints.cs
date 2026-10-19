using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Api.Agent;

/// <summary>Wire form of the agent mode: <c>analyze</c> or <c>prepare</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentModeValue>))]
public enum AgentModeValue
{
    [JsonStringEnumMemberName("analyze")] Analyze,
    [JsonStringEnumMemberName("prepare")] Prepare,
}

public sealed record AgentSettingsRequest
{
    public AgentModeValue? Mode { get; init; }
    public bool? Paused { get; init; }
}

public sealed record AgentSettings
{
    public required AgentModeValue AgentMode { get; init; }
    public required bool Paused { get; init; }
}

/// <summary>Typed refusal for fund-moving paths while the agent is restricted.</summary>
public sealed record AgentRefusal(string Code, string Message);

public static class AgentModePolicy
{
    public const string Paused = "agent_paused";
    public const string AnalyzeOnly = "agent_analyze_only";

    /// <summary>The mode the session effectively runs in: anonymous is always analyze.</summary>
    public static Core.Data.AgentMode Effective(ChatSession s) =>
        s.Tier == Core.Data.SessionTier.Anonymous ? Core.Data.AgentMode.Analyze : s.AgentMode;

    public static AgentModeValue ToWire(Core.Data.AgentMode m) => m == Core.Data.AgentMode.Analyze ? AgentModeValue.Analyze : AgentModeValue.Prepare;

    /// <summary>Non-null when confirming or elevating must be refused for this session.</summary>
    public static AgentRefusal? ConfirmRefusal(ChatSession s)
    {
        if (s.Paused) return new AgentRefusal(Paused, "Biatec AI is paused. Resume it in the permissions dialog to continue.");
        if (Effective(s) == Core.Data.AgentMode.Analyze)
            return new AgentRefusal(AnalyzeOnly, "Biatec AI is set to analyze only. Allow it to prepare actions in the permissions dialog to continue.");
        return null;
    }
}

public static class AgentModeEndpoints
{
    public const string EvidenceKind = "agent_mode_changed";

    /// <summary>
    /// PUT /session/agent. Set only by the user (never a model tool). The global CSRF guard requires the custom header,
    /// a registered Origin and a client key. Anonymous sessions get 403.
    /// </summary>
    public static IEndpointRouteBuilder MapAgentMode(this IEndpointRouteBuilder app)
    {
        app.MapPut("/session/agent", async (AgentSettingsRequest? body, HttpContext ctx, ChatDbContext db, IEvidenceLog evidence, TimeProvider clock) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var tenant = ctx.GetTenant()!;
            var session = await AuthEndpoints.FindLiveAsync(db, tenant, SessionEndpoints.ReadCookie(ctx.Request), clock.GetUtcNow(), ctx.RequestAborted);
            if (session is null) return Results.Unauthorized();
            if (session.Tier == Core.Data.SessionTier.Anonymous)
                return Results.Json(new { error = "anonymous_always_analyze", message = "Sign in to change what Biatec AI may do." }, statusCode: StatusCodes.Status403Forbidden);
            if (body?.Mode is null || body.Paused is null)
                return Results.Problem("mode and paused are required.", statusCode: StatusCodes.Status400BadRequest);

            var newMode = body.Mode == AgentModeValue.Analyze ? Core.Data.AgentMode.Analyze : Core.Data.AgentMode.Prepare;
            var newPaused = body.Paused.Value;
            if (newMode != session.AgentMode || newPaused != session.Paused)
            {
                await using var tx = await db.Database.BeginTransactionAsync(ctx.RequestAborted);
                var n = await db.ChatSessions.Where(s => s.Id == session.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.AgentMode, newMode).SetProperty(s => s.Paused, newPaused), ctx.RequestAborted);
                if (n != 1) return Results.Unauthorized();
                await evidence.AppendAsync(session.ConversationId, EvidenceKind, new JsonObject
                {
                    ["old"] = new JsonObject { ["mode"] = Name(session.AgentMode), ["paused"] = session.Paused },
                    ["new"] = new JsonObject { ["mode"] = Name(newMode), ["paused"] = newPaused },
                }, ct: ctx.RequestAborted);
                await tx.CommitAsync(ctx.RequestAborted);
            }
            return Results.Ok(new AgentSettings { AgentMode = AgentModePolicy.ToWire(newMode), Paused = newPaused });
        }).WithName("SetAgentMode").Produces<AgentSettings>();
        return app;
    }

    private static string Name(Core.Data.AgentMode m) => m == Core.Data.AgentMode.Analyze ? "analyze" : "prepare";
}
