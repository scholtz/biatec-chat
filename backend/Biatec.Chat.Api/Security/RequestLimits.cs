using System.Threading.RateLimiting;
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
/// The latest result of each dependency check, reused for <see cref="RequestLimitsOptions.HealthCacheSeconds"/>. <c>/health</c> needs no
/// authentication and is reachable through the ingress, and every real check opens a PostgreSQL connection or pings Redis, so the cost
/// of a caller is bounded here: one check at a time per dependency, whoever asks and however often.
/// </summary>
public sealed class HealthResultCache(IOptions<RequestLimitsOptions> options, TimeProvider clock)
{
    // One immutable reference per check, so a reader never sees a result from one run with the expiry of another.
    private sealed record Entry(HealthCheckResult Result, DateTimeOffset Until);
    private sealed class Slot { public readonly SemaphoreSlim Gate = new(1, 1); public volatile Entry? Latest; }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    /// <summary>The cached result of <paramref name="name"/>, or the result of one fresh <paramref name="run"/> (shared by everyone waiting).</summary>
    public async Task<HealthCheckResult> GetAsync(string name, Func<Task<HealthCheckResult>> run, CancellationToken ct)
    {
        var slot = _slots.GetOrAdd(name, _ => new Slot());
        if (slot.Latest is { } fresh && clock.GetUtcNow() < fresh.Until) return fresh.Result;
        await slot.Gate.WaitAsync(ct);
        try
        {
            if (slot.Latest is { } again && clock.GetUtcNow() < again.Until) return again.Result; // another caller checked while this one waited
            HealthCheckResult result;
            try { result = await run().WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
            catch (Exception) { result = HealthCheckResult.Unhealthy("The check failed."); } // a failing dependency is cached too: that is when the cost bound matters
            slot.Latest = new Entry(result, clock.GetUtcNow().AddSeconds(Math.Max(0, options.Value.HealthCacheSeconds)));
            return result;
        }
        finally { slot.Gate.Release(); }
    }
}

/// <summary>Runs <paramref name="inner"/> through the <see cref="HealthResultCache"/>. The check itself is created per run by the health service, so the state lives in the cache.</summary>
public sealed class CachedHealthCheck(string name, IHealthCheck inner, HealthResultCache cache) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        cache.GetAsync(name, () => inner.CheckHealthAsync(context, CancellationToken.None), cancellationToken);
}

public static class RequestLimits
{
    public const string HealthRateLimitPolicy = "health";

    public static IServiceCollection AddRequestLimits(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<RequestLimitsOptions>(config.GetSection(RequestLimitsOptions.Section));
        services.AddSingleton<HealthResultCache>();
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

    /// <summary>A registration of <paramref name="inner"/> behind the result cache.</summary>
    public static HealthCheckRegistration Cached(string name, Func<IServiceProvider, IHealthCheck> inner) =>
        new(name, sp => new CachedHealthCheck(name, inner(sp), sp.GetRequiredService<HealthResultCache>()), failureStatus: null, tags: null);
}
