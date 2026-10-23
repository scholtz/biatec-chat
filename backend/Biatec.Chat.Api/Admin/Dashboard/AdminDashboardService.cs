using System.Text.Json;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Biatec.Chat.Api.Admin.Dashboard;

public sealed record StatTotals(int Users, int ActiveUsers7d, int Conversations, long Messages, int LiveSessions, int Previews, int Tenants);
public sealed record TierCount(string Tier, int Count);
public sealed record StatusCount(string Status, int Count);
public sealed record ModelTokens(string ModelId, long Messages, long Input, long Output, long CacheRead, long CacheWrite);
public sealed record TokenStats(long Input, long Output, long CacheRead, long CacheWrite, IReadOnlyList<ModelTokens> ByModel);
public sealed record DailyPoint(string Date, int NewUsers, int Conversations, long Messages, long InputTokens, long OutputTokens);
public sealed record TenantStat(string Id, string Host, int Users, int Conversations);
public sealed record StatsResponse(DateTimeOffset GeneratedAt, int Days, string? Tenant, StatTotals Totals, IReadOnlyList<TierCount> SessionsByTier,
    IReadOnlyList<StatusCount> PreviewsByStatus, TokenStats Tokens, IReadOnlyList<DailyPoint> Daily, IReadOnlyList<TenantStat> Tenants);

public sealed record UserRow(string TenantId, string Sub, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, int SignInCount,
    int Conversations, DateTimeOffset LastActivityAt);
public sealed record UserPage(IReadOnlyList<UserRow> Items, int Total, int Page, int PageSize);
public sealed record UserSummary(string TenantId, string Sub, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, int SignInCount,
    int Conversations, DateTimeOffset LastActivityAt, long Messages, int Previews, int ActiveSessions);
public sealed record ConversationRow(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset LastMessageAt, DateTimeOffset? EndedAt, int Messages, int Previews);
public sealed record UserDetail(UserSummary User, IReadOnlyList<ConversationRow> Conversations);

public sealed record ViewMessage(long Seq, string Role, string Content, DateTimeOffset CreatedAt, string? ModelId, IReadOnlyList<string> ToolNames, int ToolResultCount);
public sealed record ViewStep(int Seq, string Kind, string Status, string? Network, IReadOnlyList<string> TxIds);
public sealed record ViewPreview(Guid Id, string Capability, string Status, decimal? PredictedValueEur, bool NeedsConsent, DateTimeOffset ExpiresAt, IReadOnlyList<ViewStep> Steps);
public sealed record ViewConversation(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset LastMessageAt, DateTimeOffset? EndedAt);
public sealed record ViewTranscript(ViewConversation Conversation, IReadOnlyList<ViewMessage> Messages, bool Truncated, IReadOnlyList<ViewPreview> Previews);

/// <summary>
/// The queries behind the administrator dashboard (docs/admin-dashboard.md). Statistics and the user list are metadata only; message text is
/// read only through <see cref="TranscriptAsync"/>, which the impersonation endpoints call for the impersonated user alone.
/// </summary>
public sealed class AdminDashboardService(ChatDbContext db, TimeProvider clock, IMemoryCache cache)
{
    public const int MaxMessages = 500;
    public const int MaxConversations = 200;

    // ---- statistics -------------------------------------------------------------------------------------------------

    /// <summary>How long a statistics answer is reused: the overview runs aggregate queries over the whole message table.</summary>
    public static readonly TimeSpan StatsCacheTime = TimeSpan.FromSeconds(30);

    public async Task<StatsResponse> StatsAsync(int days, string? tenant, CancellationToken ct)
    {
        tenant = string.IsNullOrWhiteSpace(tenant) ? null : tenant.Trim();
        // Only a real tenant (or none) is cached, so arbitrary query values cannot fill the cache.
        if (tenant is not null && !await db.Tenants.AnyAsync(t => t.Id == tenant, ct)) return await ComputeStatsAsync(days, tenant, ct);
        return (await cache.GetOrCreateAsync($"admin-stats|{days}|{tenant}", e =>
        {
            e.AbsoluteExpirationRelativeToNow = StatsCacheTime;
            return ComputeStatsAsync(days, tenant, ct);
        }))!;
    }

    private async Task<StatsResponse> ComputeStatsAsync(int days, string? tenant, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var from = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-(days - 1));
        var weekAgo = now.AddDays(-7);
        tenant = string.IsNullOrWhiteSpace(tenant) ? null : tenant;

        var users = db.KnownUsers.AsNoTracking().Where(u => tenant == null || u.TenantId == tenant);
        var convs = db.Conversations.AsNoTracking().Where(c => tenant == null || c.TenantId == tenant);
        var previews = db.ActionPreviews.AsNoTracking().Where(p => tenant == null || db.Conversations.Any(c => c.Id == p.ConversationId && c.TenantId == tenant));

        var userCount = await users.CountAsync(ct);
        var active = await users.CountAsync(u => u.LastSeenAt >= weekAgo || db.Conversations.Any(c => c.TenantId == u.TenantId && c.OidcSub == u.OidcSub && c.LastMessageAt >= weekAgo), ct);
        var convCount = await convs.CountAsync(ct);
        var msgCount = await db.Messages.AsNoTracking().LongCountAsync(m => tenant == null || db.Conversations.Any(c => c.Id == m.ConversationId && c.TenantId == tenant), ct);
        var previewCount = await previews.CountAsync(ct);

        var live = db.ChatSessions.AsNoTracking().Where(SessionLiveness.IsLive(db, now)).Where(s => tenant == null || s.TenantId == tenant);
        var tiers = await live.GroupBy(s => s.Tier).Select(g => new { Tier = g.Key, Count = g.Count() }).ToListAsync(ct);
        var liveCount = tiers.Sum(t => t.Count);
        var byStatus = await previews.GroupBy(p => p.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);

        var perModel = await db.Database.SqlQuery<ModelTokenRow>($"""
            SELECT COALESCE(m."ModelId", 'unknown') AS "ModelId", COUNT(*)::bigint AS "Messages",
                   COALESCE(SUM((m."Usage"->>'inputTokens')::bigint), 0)::bigint AS "Input",
                   COALESCE(SUM((m."Usage"->>'outputTokens')::bigint), 0)::bigint AS "Output",
                   COALESCE(SUM((m."Usage"->>'cacheReadInputTokens')::bigint), 0)::bigint AS "CacheRead",
                   COALESCE(SUM((m."Usage"->>'cacheWriteInputTokens')::bigint), 0)::bigint AS "CacheWrite"
            FROM "Messages" m JOIN "Conversations" c ON c."Id" = m."ConversationId"
            WHERE m."Usage" IS NOT NULL AND m."CreatedAt" >= {from} AND ({tenant}::text IS NULL OR c."TenantId" = {tenant}::text)
            GROUP BY 1 ORDER BY "Output" DESC
            """).ToListAsync(ct);

        var perDay = await db.Database.SqlQuery<DayRow>($"""
            SELECT (m."CreatedAt" AT TIME ZONE 'UTC')::date AS "Day", COUNT(*)::bigint AS "Messages",
                   COALESCE(SUM((m."Usage"->>'inputTokens')::bigint), 0)::bigint AS "Input",
                   COALESCE(SUM((m."Usage"->>'outputTokens')::bigint), 0)::bigint AS "Output"
            FROM "Messages" m JOIN "Conversations" c ON c."Id" = m."ConversationId"
            WHERE m."CreatedAt" >= {from} AND ({tenant}::text IS NULL OR c."TenantId" = {tenant}::text)
            GROUP BY 1
            """).ToListAsync(ct);
        var convPerDay = await db.Database.SqlQuery<CountRow>($"""
            SELECT ("CreatedAt" AT TIME ZONE 'UTC')::date AS "Day", COUNT(*)::bigint AS "Count"
            FROM "Conversations" WHERE "CreatedAt" >= {from} AND ({tenant}::text IS NULL OR "TenantId" = {tenant}::text) GROUP BY 1
            """).ToListAsync(ct);
        var usersPerDay = await db.Database.SqlQuery<CountRow>($"""
            SELECT ("FirstSeenAt" AT TIME ZONE 'UTC')::date AS "Day", COUNT(*)::bigint AS "Count"
            FROM "KnownUsers" WHERE "FirstSeenAt" >= {from} AND ({tenant}::text IS NULL OR "TenantId" = {tenant}::text) GROUP BY 1
            """).ToListAsync(ct);

        var daily = new List<DailyPoint>(days);
        for (var i = 0; i < days; i++)
        {
            var day = DateOnly.FromDateTime(from.UtcDateTime.AddDays(i));
            var m = perDay.FirstOrDefault(r => r.Day == day);
            daily.Add(new DailyPoint(day.ToString("yyyy-MM-dd"),
                (int)(usersPerDay.FirstOrDefault(r => r.Day == day)?.Count ?? 0), (int)(convPerDay.FirstOrDefault(r => r.Day == day)?.Count ?? 0),
                m?.Messages ?? 0, m?.Input ?? 0, m?.Output ?? 0));
        }

        var tenants = await db.Tenants.AsNoTracking().OrderBy(t => t.Id).Select(t => new { t.Id, t.Host }).ToListAsync(ct);
        var userByTenant = await db.KnownUsers.AsNoTracking().GroupBy(u => u.TenantId).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync(ct);
        var convByTenant = await db.Conversations.AsNoTracking().GroupBy(c => c.TenantId).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync(ct);

        return new StatsResponse(now, days, tenant,
            new StatTotals(userCount, active, convCount, msgCount, liveCount, previewCount, tenants.Count),
            tiers.OrderBy(t => t.Tier).Select(t => new TierCount(t.Tier.ToString(), t.Count)).ToList(),
            byStatus.OrderByDescending(s => s.Count).Select(s => new StatusCount(s.Status.ToString(), s.Count)).ToList(),
            new TokenStats(perModel.Sum(r => r.Input), perModel.Sum(r => r.Output), perModel.Sum(r => r.CacheRead), perModel.Sum(r => r.CacheWrite),
                perModel.Select(r => new ModelTokens(r.ModelId, r.Messages, r.Input, r.Output, r.CacheRead, r.CacheWrite)).ToList()),
            daily,
            tenants.Select(t => new TenantStat(t.Id, t.Host, userByTenant.FirstOrDefault(u => u.Id == t.Id)?.Count ?? 0, convByTenant.FirstOrDefault(c => c.Id == t.Id)?.Count ?? 0)).ToList());
    }

    private sealed record ModelTokenRow(string ModelId, long Messages, long Input, long Output, long CacheRead, long CacheWrite);
    private sealed record DayRow(DateOnly Day, long Messages, long Input, long Output);
    private sealed record CountRow(DateOnly Day, long Count);

    // ---- users ------------------------------------------------------------------------------------------------------

    public static readonly string[] SortFields = ["lastActivity", "firstSeen", "sub", "conversations"];

    public async Task<UserPage> UsersAsync(string? q, string? tenant, string sort, bool descending, int page, int pageSize, CancellationToken ct)
    {
        var users = db.KnownUsers.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(tenant)) users = users.Where(u => u.TenantId == tenant);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + EscapeLike(q.Trim()) + "%";
            users = users.Where(u => EF.Functions.ILike(u.OidcSub, like));
        }

        var rows = users.Select(u => new
        {
            u.TenantId, u.OidcSub, u.FirstSeenAt, u.LastSeenAt, u.SignInCount,
            Conversations = db.Conversations.Count(c => c.TenantId == u.TenantId && c.OidcSub == u.OidcSub),
            LastMessage = db.Conversations.Where(c => c.TenantId == u.TenantId && c.OidcSub == u.OidcSub).Max(c => (DateTimeOffset?)c.LastMessageAt),
        }).Select(r => new
        {
            r.TenantId, r.OidcSub, r.FirstSeenAt, r.LastSeenAt, r.SignInCount, r.Conversations,
            LastActivity = r.LastMessage != null && r.LastMessage > r.LastSeenAt ? r.LastMessage.Value : r.LastSeenAt,
        });

        rows = (sort, descending) switch
        {
            ("firstSeen", false) => rows.OrderBy(r => r.FirstSeenAt).ThenBy(r => r.OidcSub).ThenBy(r => r.TenantId),
            ("firstSeen", true) => rows.OrderByDescending(r => r.FirstSeenAt).ThenBy(r => r.OidcSub).ThenBy(r => r.TenantId),
            ("sub", false) => rows.OrderBy(r => r.OidcSub).ThenBy(r => r.TenantId),
            ("sub", true) => rows.OrderByDescending(r => r.OidcSub).ThenBy(r => r.TenantId),
            ("conversations", false) => rows.OrderBy(r => r.Conversations).ThenBy(r => r.OidcSub).ThenBy(r => r.TenantId),
            ("conversations", true) => rows.OrderByDescending(r => r.Conversations).ThenBy(r => r.OidcSub).ThenBy(r => r.TenantId),
            (_, false) => rows.OrderBy(r => r.LastActivity).ThenBy(r => r.OidcSub).ThenBy(r => r.TenantId),
            _ => rows.OrderByDescending(r => r.LastActivity).ThenBy(r => r.OidcSub).ThenBy(r => r.TenantId),
        };

        var total = await users.CountAsync(ct);
        var items = await rows.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new UserPage(items.Select(r => new UserRow(r.TenantId, r.OidcSub, r.FirstSeenAt, r.LastSeenAt, r.SignInCount, r.Conversations, r.LastActivity)).ToList(),
            total, page, pageSize);
    }

    /// <summary>Escapes the LIKE wildcards of a search term (the escape character is backslash).</summary>
    internal static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public async Task<KnownUser?> FindUserAsync(string tenantId, string sub, CancellationToken ct) =>
        await db.KnownUsers.AsNoTracking().FirstOrDefaultAsync(u => u.TenantId == tenantId && u.OidcSub == sub, ct);

    public async Task<UserDetail?> UserAsync(string tenantId, string sub, CancellationToken ct)
    {
        var u = await FindUserAsync(tenantId, sub, ct);
        if (u is null) return null;
        var now = clock.GetUtcNow();
        var convs = await db.Conversations.AsNoTracking().Where(c => c.TenantId == tenantId && c.OidcSub == sub)
            .OrderByDescending(c => c.LastMessageAt).Take(MaxConversations)
            .Select(c => new ConversationRow(c.Id, c.CreatedAt, c.LastMessageAt, c.EndedAt,
                db.Messages.Count(m => m.ConversationId == c.Id), db.ActionPreviews.Count(p => p.ConversationId == c.Id)))
            .ToListAsync(ct);
        var conversationCount = await db.Conversations.CountAsync(c => c.TenantId == tenantId && c.OidcSub == sub, ct);
        var messages = await db.Messages.LongCountAsync(m => db.Conversations.Any(c => c.Id == m.ConversationId && c.TenantId == tenantId && c.OidcSub == sub), ct);
        var previews = await db.ActionPreviews.CountAsync(p => db.Conversations.Any(c => c.Id == p.ConversationId && c.TenantId == tenantId && c.OidcSub == sub), ct);
        var sessions = await db.ChatSessions.Where(SessionLiveness.IsLive(db, now)).CountAsync(s => s.TenantId == tenantId && s.OidcSub == sub, ct);
        var lastMessage = convs.Count == 0 ? (DateTimeOffset?)null : convs.Max(c => c.LastMessageAt);
        var last = lastMessage is { } lm && lm > u.LastSeenAt ? lm : u.LastSeenAt;
        return new UserDetail(new UserSummary(u.TenantId, u.OidcSub, u.FirstSeenAt, u.LastSeenAt, u.SignInCount, conversationCount, last, messages, previews, sessions), convs);
    }

    // ---- what the impersonated user sees ----------------------------------------------------------------------------

    public async Task<IReadOnlyList<ConversationRow>> ConversationsAsync(string tenantId, string sub, CancellationToken ct) =>
        await db.Conversations.AsNoTracking().Where(c => c.TenantId == tenantId && c.OidcSub == sub)
            .OrderByDescending(c => c.LastMessageAt).Take(MaxConversations)
            .Select(c => new ConversationRow(c.Id, c.CreatedAt, c.LastMessageAt, c.EndedAt,
                db.Messages.Count(m => m.ConversationId == c.Id), db.ActionPreviews.Count(p => p.ConversationId == c.Id)))
            .ToListAsync(ct);

    /// <summary>The transcript and previews of one conversation of this user; null when the conversation is not theirs.</summary>
    public async Task<ViewTranscript?> TranscriptAsync(string tenantId, string sub, Guid conversationId, CancellationToken ct)
    {
        var c = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == conversationId && x.TenantId == tenantId && x.OidcSub == sub, ct);
        if (c is null) return null;
        // The newest MaxMessages: when a conversation is longer, it is the recent part that matters, and Truncated says older ones are left out.
        var rows = await db.Messages.AsNoTracking().Where(m => m.ConversationId == c.Id).OrderByDescending(m => m.Seq).Take(MaxMessages + 1).ToListAsync(ct);
        var truncated = rows.Count > MaxMessages;
        var messages = rows.Take(MaxMessages).OrderBy(m => m.Seq).Select(m => new ViewMessage(m.Seq, m.Role.ToString(), m.Content, m.CreatedAt, m.ModelId, ToolNames(m.ToolCalls), CountItems(m.ToolResults))).ToList();

        var previews = await db.ActionPreviews.AsNoTracking().Where(p => p.ConversationId == c.Id)
            .OrderBy(p => p.ExpiresAt).Select(p => new { p.Id, p.Capability, p.Status, p.PredictedValueEur, p.PredictedNeedsConsent, p.ExpiresAt }).ToListAsync(ct);
        var ids = previews.Select(p => p.Id).ToList();
        var steps = await db.ActionSteps.AsNoTracking().Where(s => ids.Contains(s.PreviewId)).OrderBy(s => s.Seq)
            .Select(s => new { s.PreviewId, s.Seq, s.Kind, s.Status, s.Network, s.TxIds }).ToListAsync(ct);
        return new ViewTranscript(new ViewConversation(c.Id, c.CreatedAt, c.LastMessageAt, c.EndedAt), messages, truncated,
            previews.Select(p => new ViewPreview(p.Id, p.Capability, p.Status.ToString(), p.PredictedValueEur, p.PredictedNeedsConsent, p.ExpiresAt,
                steps.Where(s => s.PreviewId == p.Id).Select(s => new ViewStep(s.Seq, s.Kind.ToString(), s.Status.ToString(), s.Network, s.TxIds)).ToList())).ToList());
    }

    /// <summary>The tool names of a stored tool-call array; the raw arguments and results are never exposed.</summary>
    internal static IReadOnlyList<string> ToolNames(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null)
                .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!.Length > 64 ? n[..64] : n).Take(20).ToList();
        }
        catch (JsonException) { return []; }
    }

    internal static int CountItems(string? json)
    {
        if (string.IsNullOrEmpty(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch (JsonException) { return 0; }
    }
}
