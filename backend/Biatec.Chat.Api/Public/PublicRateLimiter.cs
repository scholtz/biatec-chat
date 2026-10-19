using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Biatec.Chat.Api.Public;

public sealed class PublicChatOptions
{
    public const string Section = "PublicChat";

    /// <summary>Messages per minute per client address (salted hash) on /public/chat.</summary>
    public int IpPermitsPerMinute { get; set; } = 20;
    /// <summary>Messages per minute per request Origin on /public/chat.</summary>
    public int OriginPermitsPerMinute { get; set; } = 120;
    /// <summary>An idle public conversation ends after this many minutes.</summary>
    public int IdleMinutes { get; set; } = 30;
    /// <summary>Secret mixed into the address hash (the date rotates it daily). Unset: a random per-process value.</summary>
    public string? IpSalt { get; set; }
}

public sealed record RateDecision(bool Allowed, int RetryAfterSeconds);

public interface IPublicRateLimiter
{
    /// <summary>Counts one request for this address and origin. The raw address never leaves this method.</summary>
    Task<RateDecision> TryAcquireAsync(System.Net.IPAddress? address, string? origin, CancellationToken ct);
}

/// <summary>
/// Redis counters keyed on a salted hash of the address (the salt rotates daily) with a TTL of a few minutes, so
/// counters are shared across pods and no raw address is stored or logged. If Redis is unreachable the counters
/// fall back to this process's memory: the limiter degrades, it never fails open to unlimited and never throws.
/// </summary>
public sealed class RedisPublicRateLimiter(IConnectionMultiplexer redis, IOptions<PublicChatOptions> options, TimeProvider clock) : IPublicRateLimiter
{
    private static readonly byte[] ProcessSalt = RandomNumberGenerator.GetBytes(32);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(3);
    private readonly ConcurrentDictionary<string, (long Window, int Count)> _local = new();

    public async Task<RateDecision> TryAcquireAsync(System.Net.IPAddress? address, string? origin, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var window = now.ToUnixTimeSeconds() / 60;
        var retry = (int)(60 - now.ToUnixTimeSeconds() % 60);
        var ipKey = "ip:" + Hash(now, address?.ToString() ?? "unknown");
        var originKey = "origin:" + Hash(now, origin ?? "none");

        var ip = await IncrementAsync(ipKey, window);
        if (ip > Math.Max(1, o.IpPermitsPerMinute)) return new RateDecision(false, retry);
        var og = await IncrementAsync(originKey, window);
        return og > Math.Max(1, o.OriginPermitsPerMinute) ? new RateDecision(false, retry) : new RateDecision(true, 0);
    }

    private string Hash(DateTimeOffset now, string value)
    {
        var salt = options.Value.IpSalt is { Length: > 0 } s ? Encoding.UTF8.GetBytes(s) : ProcessSalt;
        using var h = new HMACSHA256(salt);
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(now.UtcDateTime.ToString("yyyy-MM-dd") + "|" + value)))[..32];
    }

    private async Task<long> IncrementAsync(string key, long window)
    {
        if (redis.IsConnected)
        {
            try
            {
                var db = redis.GetDatabase();
                var redisKey = $"biatec:pub:rl:{key}:{window}";
                var n = await db.StringIncrementAsync(redisKey);
                if (n == 1) await db.KeyExpireAsync(redisKey, Ttl);
                return n;
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException) { /* fall through to memory */ }
        }
        var entry = _local.AddOrUpdate(key, _ => (window, 1), (_, e) => e.Window == window ? (window, e.Count + 1) : (window, 1));
        if (_local.Count > 10_000)
            foreach (var kv in _local.Where(kv => kv.Value.Window < window)) _local.TryRemove(kv.Key, out _);
        return entry.Count;
    }
}
