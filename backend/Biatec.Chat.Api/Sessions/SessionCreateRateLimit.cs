using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Sessions;

/// <summary>Bounds how fast one address can make the backend create sessions and conversations (BCHAT-004).</summary>
public sealed class SessionCreateRateLimitOptions
{
    public const string Section = "Sessions:CreateRateLimit";

    /// <summary>Requests per window per client address on the routes that can create a session.</summary>
    public int Permits { get; set; } = 120;

    public int WindowSeconds { get; set; } = 60;
}

public static class SessionCreateRateLimit
{
    public const string Policy = "session-create";

    public static IServiceCollection AddSessionCreateRateLimit(this IServiceCollection services)
    {
        services.AddOptions<SessionCreateRateLimitOptions>().BindConfiguration(SessionCreateRateLimitOptions.Section);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // The client address is the forwarded one only when the peer is a configured proxy (ClientAddress, BCHAT-005).
            o.AddPolicy(Policy, ctx =>
            {
                var opts = ctx.RequestServices.GetRequiredService<IOptions<SessionCreateRateLimitOptions>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, opts.Permits),
                    Window = TimeSpan.FromSeconds(Math.Max(1, opts.WindowSeconds)),
                    QueueLimit = 0,
                });
            });
        });
        return services;
    }

    /// <summary>The route can create a session when the request carries no live cookie.</summary>
    public static RouteHandlerBuilder LimitSessionCreation(this RouteHandlerBuilder b) => b.RequireRateLimiting(Policy);
}
