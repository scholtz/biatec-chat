using System.Collections.Concurrent;

namespace Biatec.Chat.Tools.Mcp;

/// <summary>Tool lists keyed by tenant and scope set, so a list obtained with an openid-only token is never served to a sign session.</summary>
public sealed class McpToolCache(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(string Tenant, string Scopes), (DateTimeOffset At, IReadOnlyList<McpToolInfo> Tools)> _items = new();

    public void Put(McpCallContext c, IReadOnlyList<McpToolInfo> tools) => _items[(c.TenantId, c.ScopeKey)] = (_clock.GetUtcNow(), tools);

    /// <summary>The cached list, or null when absent or older than <paramref name="ttl"/>.</summary>
    public IReadOnlyList<McpToolInfo>? TryGet(McpCallContext c, TimeSpan ttl) =>
        _items.TryGetValue((c.TenantId, c.ScopeKey), out var e) && _clock.GetUtcNow() - e.At < ttl ? e.Tools : null;
}
