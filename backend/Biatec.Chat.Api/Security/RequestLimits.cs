using System.Threading.RateLimiting;
using Biatec.Chat.Api.Health;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Security;

/// <summary>Limits that hold for every route (section <c>Limits</c>). Individual routes may be stricter (onboarding 32 KiB, a knowledge-base page 16 KiB).</summary>
public sealed class RequestLimitsOptions
{
    public const string Section = "Limits";

    /// <summary>Largest request body in bytes, declared or streamed (default 64 KiB: chat messages, previews, onboarding and KB pages are all far below it).</summary>
    public int MaxRequestBodyBytes { get; set; } = 64 * 1024;

    /// <summary>How long the answer of <c>/health</c> is reused, in seconds (default 5): the dependency checks run at most that often however often it is called.</summary>
    public int HealthCacheSeconds { get; set; } = 5;

    /// <summary><c>/health</c> requests per window and remote IP (default 120 per 60 s; kubelet probes every few seconds are far below it).</summary>
    public int HealthRateLimitPermits { get; set; } = 120;

    public int HealthRateLimitWindowSeconds { get; set; } = 60;
}

/// <summary>
/// The answer of <c>/health</c>, reused for <see cref="RequestLimitsOptions.HealthCacheSeconds"/>. The endpoint needs no authentication and is
/// reachable through the ingress, and each real check opens a PostgreSQL connection and pings Redis, so the cost of a caller is bounded here.
/// A single check at a time; waiters share its result.
/// </summary>
public sealed class CachedHealth(HealthCheckService checks, IOptions<RequestLimitsOptions> options, TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthStatus _status;
    private DateTimeOffset _validUntil = DateTimeOffset.MinValue;

    public async Task<HealthStatus> StatusAsync(CancellationToken ct)
    {
        if (clock.GetUtcNow() < _validUntil) return _status;
        await _gate.WaitAsync(ct);
        try
        {
            if (clock.GetUtcNow() < _validUntil) return _status; // another caller checked while this one waited
            var report = await checks.CheckHealthAsync(ct);
            _status = report.Status;
            _validUntil = clock.GetUtcNow().AddSeconds(Math.Max(0, options.Value.HealthCacheSeconds));
            return _status;
        }
        finally { _gate.Release(); }
    }
}

public static class RequestLimits
{
    public const string HealthRateLimitPolicy = "health";

    public static IServiceCollection AddRequestLimits(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<RequestLimitsOptions>(config.GetSection(RequestLimitsOptions.Section));
        services.AddSingleton<CachedHealth>();
        services.AddRateLimiter(o => o.AddPolicy(HealthRateLimitPolicy, ctx =>
        {
            var opts = ctx.RequestServices.GetRequiredService<IOptions<RequestLimitsOptions>>().Value;
            return RateLimitPartition.GetFixedWindowLimiter("health:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, opts.HealthRateLimitPermits),
                Window = TimeSpan.FromSeconds(Math.Max(1, opts.HealthRateLimitWindowSeconds)),
                QueueLimit = 0,
            });
        }));
        return services;
    }

    /// <summary>Refuses a request whose declared body is over the limit with 413, and caps a body that declares no length.</summary>
    public static IApplicationBuilder UseRequestBodyLimit(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var max = ctx.RequestServices.GetRequiredService<IOptions<RequestLimitsOptions>>().Value.MaxRequestBodyBytes;
        if (ctx.Request.ContentLength > max)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        // Chunked bodies have no declared length: the server's own cap does the work (a stricter route limit may lower it again later).
        var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false } && (feature.MaxRequestBodySize is null || feature.MaxRequestBodySize > max)) feature.MaxRequestBodySize = max;
        await next(ctx);
    });

    public static IEndpointRouteBuilder MapCachedHealth(this IEndpointRouteBuilder app)
    {
        // 200 Healthy when Redis and PostgreSQL answer, 503 Unhealthy otherwise. The body is the status only.
        app.MapGet("/health", async (CachedHealth health, CancellationToken ct) =>
        {
            var status = await health.StatusAsync(ct);
            return Results.Text(status.ToString(), "text/plain", statusCode: status == HealthStatus.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
        }).RequireRateLimiting(HealthRateLimitPolicy);
        return app;
    }
}
