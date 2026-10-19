using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using ApiTier = Biatec.Chat.Api.Contracts.SessionTier;

namespace Biatec.Chat.Api.Sessions;

public sealed record ProbeResponse
{
    /// <summary>True when this request carried a live session cookie.</summary>
    public required bool CookieReturned { get; init; }
}

public sealed record KeepAliveResponse
{
    /// <summary>The absolute expiry. Keepalive never changes it.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

public static class SessionEndpoints
{
    /// <summary>
    /// Writes the session cookie with the attributes the spec fixes, on every transport including http://localhost:
    /// __Host- prefix, HttpOnly, Secure, SameSite=Lax, Path=/, no Domain. Browsers treat localhost as a secure context.
    /// </summary>
    public static void WriteCookie(HttpResponse res, string token, DateTimeOffset absoluteExpiresAt, DateTimeOffset now)
    {
        var remaining = absoluteExpiresAt - now;
        res.Cookies.Append(SessionPolicy.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Domain = null,
            IsEssential = true,
            MaxAge = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
        });
    }

    public static string? ReadCookie(HttpRequest req) => req.Cookies.TryGetValue(SessionPolicy.CookieName, out var v) ? v : null;

    public static IEndpointRouteBuilder MapSessions(this IEndpointRouteBuilder app)
    {
        // Session state. The first call (no live cookie) creates the session and sets the cookie.
        app.MapGet("/session", async (HttpContext ctx, ISessionService sessions, ChatDbContext db, TimeProvider clock) =>
        {
            NoStore(ctx);
            var handle = await sessions.ResolveOrCreateAsync(ctx.GetTenant()!, ReadCookie(ctx.Request), ctx.RequestAborted);
            if (handle.NewToken is not null) WriteCookie(ctx.Response, handle.NewToken, handle.Session.AbsoluteExpiresAt, clock.GetUtcNow());
            return Results.Ok(await BuildStateAsync(db, handle.Session, ctx.RequestAborted));
        }).WithName("GetSession").Produces<SessionState>().RegisteredOriginsOnly();

        // Cookie probe. Without a live cookie: creates the session, sets the cookie, reports false. With it: true.
        app.MapGet("/session/probe", async (HttpContext ctx, ISessionService sessions, TimeProvider clock) =>
        {
            NoStore(ctx);
            var handle = await sessions.ResolveOrCreateAsync(ctx.GetTenant()!, ReadCookie(ctx.Request), ctx.RequestAborted);
            if (handle.NewToken is not null) WriteCookie(ctx.Response, handle.NewToken, handle.Session.AbsoluteExpiresAt, clock.GetUtcNow());
            return Results.Ok(new ProbeResponse { CookieReturned = !handle.Created });
        }).WithName("ProbeSession").Produces<ProbeResponse>().RegisteredOriginsOnly();

        // Keepalive: extends the idle timeout only. An expired or missing session gets 401 and no new session.
        app.MapPost("/session/keepalive", async (HttpContext ctx, ISessionService sessions, ChatDbContext db) =>
        {
            NoStore(ctx);
            var token = ReadCookie(ctx.Request);
            if (!await sessions.KeepAliveAsync(ctx.GetTenant()!, token, ctx.RequestAborted)) return Results.Unauthorized();
            var s = await db.ChatSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == SessionService.IdFromToken(token!), ctx.RequestAborted);
            return s is null ? Results.Unauthorized() : Results.Ok(new KeepAliveResponse { ExpiresAt = s.AbsoluteExpiresAt });
        }).WithName("KeepAliveSession").Produces<KeepAliveResponse>().RegisteredOriginsOnly();

        return app;
    }

    /// <summary>A browser Origin outside the tenant allow-list gets no session and no Set-Cookie (a missing Origin is a same-origin or non-browser call).</summary>
    private static RouteHandlerBuilder RegisteredOriginsOnly(this RouteHandlerBuilder b) => b.AddEndpointFilter(async (c, next) =>
    {
        var req = c.HttpContext.Request;
        if (req.Headers.ContainsKey("Origin") && Security.OriginPolicy.AllowedOrigin(c.HttpContext.GetTenant()!, req) is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        return await next(c);
    });

    private static void NoStore(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Append("Vary", "Cookie");
    }

    internal static async Task<SessionState> BuildStateAsync(ChatDbContext db, ChatSession s, CancellationToken ct)
    {
        var popup = await db.PopupAuthRequests.AsNoTracking()
            .Where(p => p.ConversationId == s.ConversationId)
            .OrderByDescending(p => p.ExpiresAt)
            .FirstOrDefaultAsync(ct);
        return new SessionState
        {
            SessionId = s.Id.ToString("N"),
            Tier = s.Tier == Core.Data.SessionTier.Anonymous ? ApiTier.Anonymous : ApiTier.Authenticated,
            // Email, primary address and scopes come from the vault/OIDC profile once T-009 signs a user in.
            Email = null,
            PrimaryAddress = null,
            Scopes = [],
            AuthRequestId = popup?.Id.ToString(),
            SignInStatus = popup?.Status switch
            {
                null => SignInStatus.None,
                PopupAuthStatus.Pending => SignInStatus.Pending,
                PopupAuthStatus.Completed => SignInStatus.Complete,
                _ => SignInStatus.Failed,
            },
            ExpiresAt = s.AbsoluteExpiresAt,
            AgentMode = Agent.AgentModePolicy.ToWire(Agent.AgentModePolicy.Effective(s)),
            Paused = s.Paused && s.Tier != Core.Data.SessionTier.Anonymous,
        };
    }
}

public static class SessionRegistration
{
    public static IServiceCollection AddSessions(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ISessionService, SessionService>();
        return services;
    }
}
