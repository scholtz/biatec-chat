using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace Biatec.Chat.Api.Budget;

/// <summary>Spend so far in the current window, in micro-USD.</summary>
public readonly record struct BudgetSpend(long TenantMicroUsd, long GlobalMicroUsd);

/// <summary>Spend counters per fixed window. Shared by every pod, so it lives in Redis.</summary>
public interface IBudgetLedger
{
    Task<BudgetSpend> GetAsync(string tenantId, DateTimeOffset now, CancellationToken ct);
    Task AddAsync(string tenantId, long microUsd, DateTimeOffset now, CancellationToken ct);
}

/// <summary>Anonymous answers served while the breaker is open. Keyed by a hash of the tenant and the question.</summary>
public interface IAnswerCache
{
    Task<string?> GetAsync(string tenantId, string question, CancellationToken ct);
    Task PutAsync(string tenantId, string question, string answer, CancellationToken ct);
}

public sealed class RedisBudgetLedger(IConnectionMultiplexer redis, Microsoft.Extensions.Options.IOptions<BudgetOptions> options) : IBudgetLedger
{
    private readonly TimeSpan _window = TimeSpan.FromMinutes(options.Value.WindowMinutes);

    public async Task<BudgetSpend> GetAsync(string tenantId, DateTimeOffset now, CancellationToken ct)
    {
        var bucket = Bucket(now);
        var values = await redis.GetDatabase().StringGetAsync([TenantKey(tenantId, bucket), GlobalKey(bucket)]);
        return new BudgetSpend(ToLong(values[0]), ToLong(values[1]));
    }

    public async Task AddAsync(string tenantId, long microUsd, DateTimeOffset now, CancellationToken ct)
    {
        if (microUsd <= 0) return;
        var bucket = Bucket(now);
        var batch = redis.GetDatabase().CreateBatch();
        // The key outlives its window by one window, so a late increment still lands and nothing piles up.
        var ttl = _window * 2;
        var tasks = new List<Task>();
        foreach (var key in new RedisKey[] { TenantKey(tenantId, bucket), GlobalKey(bucket) })
        {
            tasks.Add(batch.StringIncrementAsync(key, microUsd));
            tasks.Add(batch.KeyExpireAsync(key, ttl));
        }
        batch.Execute();
        await Task.WhenAll(tasks);
    }

    private long Bucket(DateTimeOffset now) => now.ToUnixTimeSeconds() / (long)_window.TotalSeconds;
    private static RedisKey TenantKey(string tenantId, long bucket) => $"biatec:budget:tenant:{tenantId}:{bucket}";
    private static RedisKey GlobalKey(long bucket) => $"biatec:budget:global:{bucket}";
    private static long ToLong(RedisValue v) => v.HasValue && long.TryParse(v.ToString(), out var n) ? n : 0;
}

public sealed class RedisAnswerCache(IConnectionMultiplexer redis, Microsoft.Extensions.Options.IOptions<BudgetOptions> options) : IAnswerCache
{
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(options.Value.CachedAnswerTtlMinutes);

    public async Task<string?> GetAsync(string tenantId, string question, CancellationToken ct)
    {
        var v = await redis.GetDatabase().StringGetAsync(Key(tenantId, question));
        return v.HasValue ? v.ToString() : null;
    }

    public Task PutAsync(string tenantId, string question, string answer, CancellationToken ct) =>
        redis.GetDatabase().StringSetAsync(Key(tenantId, question), answer, _ttl);

    /// <summary>The question is hashed, so Redis never holds what a visitor typed.</summary>
    private static RedisKey Key(string tenantId, string question) =>
        "biatec:budget:answer:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tenantId + "\n" + AnswerKey.Normalize(question))));
}

public static class AnswerKey
{
    /// <summary>Case, surrounding whitespace, repeated spaces and trailing punctuation don't make a different question.</summary>
    public static string Normalize(string question) =>
        string.Join(' ', question.Trim().TrimEnd('?', '!', '.', ' ').ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
