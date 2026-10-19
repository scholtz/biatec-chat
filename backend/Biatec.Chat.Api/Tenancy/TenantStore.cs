using System.Text.Json;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Biatec.Chat.Api.Tenancy;

public interface ITenantStore
{
    /// <summary>Finds the tenant whose registered host equals <paramref name="host"/> (already normalized).</summary>
    Task<Tenant?> FindByHostAsync(string host, CancellationToken ct);
}

/// <summary>Tenant lookup backed by PostgreSQL with a short cache (misses included).</summary>
public sealed class DbTenantStore(IServiceScopeFactory scopes, IMemoryCache cache) : ITenantStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public async Task<Tenant?> FindByHostAsync(string host, CancellationToken ct)
    {
        if (cache.TryGetValue(host, out Tenant? hit)) return hit;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Host == host, ct);
        cache.Set(host, tenant, Ttl);
        return tenant;
    }
}

public static class TenantHost
{
    /// <summary>Lower-cases, strips a trailing dot and the port. Returns null for anything that is not a plain host name.</summary>
    public static string? Normalize(HostString host)
    {
        if (!host.HasValue) return null;
        var h = host.Host.Trim().TrimEnd('.').ToLowerInvariant();
        return h.Length is 0 or > 253 ? null : h;
    }
}

public static class ClientKeys
{
    public static bool Contains(Tenant tenant, string key)
    {
        try
        {
            var keys = JsonSerializer.Deserialize<string[]>(tenant.ClientKeys) ?? [];
            return keys.Contains(key, StringComparer.Ordinal);
        }
        catch (JsonException) { return false; }
    }
}
