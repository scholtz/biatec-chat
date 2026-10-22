using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Sessions;

/// <summary>Bounds how fast one client address can make the backend create sessions and conversations (BCHAT-004).</summary>
public sealed class SessionCreateRateLimitOptions
{
    public const string Section = "Sessions:CreateRateLimit";

    /// <summary>Sessions one client address may create per window.</summary>
    public int Permits { get; set; } = 60;

    public int WindowSeconds { get; set; } = 60;
}

/// <summary>A session would have been created beyond the per-address limit. Nothing was written.</summary>
public sealed class SessionCreationLimitedException(int retryAfterSeconds) : Exception("Too many sessions were created from this address.")
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// Counts session creations (not requests: a request that carries a live session never reaches it) per client address.
/// The address is the forwarded one only from configured proxies (<c>ClientAddress</c>, BCHAT-005); IPv4-mapped IPv6 is
/// folded to IPv4 and an IPv6 address to its /64, so a client cannot dodge the limit by rotating addresses of one allocation.
/// </summary>
public sealed class SessionCreationLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly int _retryAfter;

    public SessionCreationLimiter(IOptions<SessionCreateRateLimitOptions> options)
    {
        var o = options.Value;
        _retryAfter = Math.Max(1, o.WindowSeconds);
        _limiter = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, o.Permits),
            Window = TimeSpan.FromSeconds(_retryAfter),
            QueueLimit = 0,
        }));
    }

    /// <exception cref="SessionCreationLimitedException">The address is over the limit.</exception>
    public void Acquire(IPAddress? address)
    {
        using var lease = _limiter.AttemptAcquire(Key(address));
        if (!lease.IsAcquired) throw new SessionCreationLimitedException(lease.TryGetMetadata(MetadataName.RetryAfter, out var r) ? (int)Math.Ceiling(r.TotalSeconds) : _retryAfter);
    }

    internal static string Key(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }

    public void Dispose() => _limiter.Dispose();
}

public static class SessionCreateRateLimit
{
    public static IServiceCollection AddSessionCreateRateLimit(this IServiceCollection services)
    {
        services.AddOptions<SessionCreateRateLimitOptions>().BindConfiguration(SessionCreateRateLimitOptions.Section);
        services.AddHttpContextAccessor();
        services.AddSingleton<SessionCreationLimiter>();
        return services;
    }

    /// <summary>Answers 429 with Retry-After when the handler tried to create a session beyond the per-address limit.</summary>
    public static RouteHandlerBuilder LimitSessionCreation(this RouteHandlerBuilder b) => b.AddEndpointFilter(async (c, next) =>
    {
        try { return await next(c); }
        catch (SessionCreationLimitedException e)
        {
            c.HttpContext.Response.Headers.RetryAfter = e.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }
    });
}
