using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Api.Sessions;

public static class SessionPolicy
{
    public const string CookieName = "__Host-biatec_session";
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(8);
    /// <summary>Bytes of CSPRNG output in the cookie secret (256 bits).</summary>
    internal const int TokenBytes = 32;
}

/// <summary>
/// The one "is this session live" test (T-080), shared by the request path (resolve, keepalive, rotation, the auth
/// and consent endpoints) and the sweeper, so they can never disagree. The absolute lifetime always applies; the idle
/// timeout applies unless the session's conversation has an open, unexpired ConsentRequest (spec, Action Preview and
/// Minimal lifetime). Both are EF Core expressions, translated to one SQL predicate with a subquery.
/// </summary>
public static class SessionLiveness
{
    /// <summary>Conversations with a consent request still waiting for the user's phone.</summary>
    public static IQueryable<Guid> AwaitingConsent(ChatDbContext db, DateTimeOffset now) =>
        from c in db.ConsentRequests
        where c.Status == ConsentStatus.Pending && c.ExpiresAt > now
        join st in db.ActionSteps on c.StepId equals st.Id
        join p in db.ActionPreviews on st.PreviewId equals p.Id
        select p.ConversationId;

    public static Expression<Func<ChatSession, bool>> IsLive(ChatDbContext db, DateTimeOffset now)
    {
        var idleCutoff = now - SessionPolicy.IdleTimeout;
        var awaiting = AwaitingConsent(db, now);
        return s => s.AbsoluteExpiresAt > now && (s.LastActivityAt > idleCutoff || awaiting.Contains(s.ConversationId));
    }

    /// <summary>The exact complement of <see cref="IsLive"/>: what the sweeper ends.</summary>
    public static Expression<Func<ChatSession, bool>> IsDue(ChatDbContext db, DateTimeOffset now)
    {
        var live = IsLive(db, now);
        return Expression.Lambda<Func<ChatSession, bool>>(Expression.Not(live.Body), live.Parameters);
    }
}

/// <summary>A resolved, live session. <see cref="NewToken"/> is set only when the caller must (re)issue the cookie.</summary>
public sealed record SessionHandle(ChatSession Session, string? NewToken, bool Created);

/// <summary>
/// Server-side sessions. The cookie carries a random 256-bit secret; the database stores only a SHA-256 prefix of it
/// as the session id, so a database read cannot be replayed as a cookie. The id is always server generated: a client
/// supplied value that matches no live session just creates a fresh one (no session fixation).
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// Finds the live session for the cookie token and records activity (idle only), or creates a new anonymous
    /// session when the token is missing, malformed, unknown, from another tenant, or expired.
    /// </summary>
    Task<SessionHandle> ResolveOrCreateAsync(Tenant tenant, string? cookieToken, CancellationToken ct);

    /// <summary>Extends the idle timeout only. False when the session is missing or already expired. Never moves the absolute expiry.</summary>
    Task<bool> KeepAliveAsync(Tenant tenant, string? cookieToken, CancellationToken ct);

    /// <summary>
    /// Issues a new id and cookie token for the same conversation, retiring the old id in one transaction (T-009
    /// calls this on login and elevation). Absolute expiry is kept. Null when the old token is not live. The caller
    /// must write the new cookie and move any vault entry keyed by the old id to the new id.
    /// </summary>
    Task<SessionHandle?> RotateAsync(Tenant tenant, string? cookieToken, CancellationToken ct);

    /// <summary>Deletes the session (logout, T-009). Idempotent.</summary>
    Task EndAsync(Tenant tenant, string? cookieToken, CancellationToken ct);
}

public sealed class SessionService(ChatDbContext db, TimeProvider clock, IEvidenceRecorder? evidence = null) : ISessionService
{
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(SessionPolicy.TokenBytes));

    public static Guid IdFromToken(string token) => new(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token)).AsSpan(0, 16));

    internal static bool WellFormed(string? token) =>
        token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Touches LastActivityAt of a live session (<see cref="SessionLiveness.IsLive"/>). Never writes AbsoluteExpiresAt, never revives an expired row.</summary>
    private async Task<bool> TouchAsync(Tenant tenant, string? token, DateTimeOffset now, CancellationToken ct)
    {
        if (!WellFormed(token)) return false;
        var id = IdFromToken(token!);
        var n = await db.ChatSessions
            .Where(x => x.Id == id && x.TenantId == tenant.Id)
            .Where(SessionLiveness.IsLive(db, now))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.LastActivityAt, x => x.LastActivityAt > now ? x.LastActivityAt : now), ct);
        return n == 1;
    }

    public async Task<SessionHandle> ResolveOrCreateAsync(Tenant tenant, string? cookieToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (await TouchAsync(tenant, cookieToken, now, ct))
        {
            var id = IdFromToken(cookieToken!);
            var s = await db.ChatSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is not null) return new SessionHandle(s, null, false);
        }
        return await CreateAsync(tenant, now, ct);
    }

    private async Task<SessionHandle> CreateAsync(Tenant tenant, DateTimeOffset now, CancellationToken ct)
    {
        var token = NewToken();
        var conversation = new Conversation { Id = Guid.NewGuid(), TenantId = tenant.Id, CreatedAt = now, LastMessageAt = now };
        var session = new ChatSession
        {
            Id = IdFromToken(token),
            ConversationId = conversation.Id,
            Tier = Core.Data.SessionTier.Anonymous,
            TenantId = tenant.Id,
            CreatedAt = now,
            LastActivityAt = now,
            AbsoluteExpiresAt = now + SessionPolicy.AbsoluteLifetime,
        };
        db.Conversations.Add(conversation);
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(ct);
        // Session metadata only: tier, tenant and times. No IP address, user agent or geolocation is recorded.
        if (evidence is not null)
            await evidence.RecordAsync(conversation.Id, EvidenceKinds.SessionStarted, new JsonObject
            {
                ["tenantId"] = tenant.Id, ["tier"] = session.Tier.ToString(), ["startedAt"] = now.ToString("O"),
                ["absoluteExpiresAt"] = session.AbsoluteExpiresAt.ToString("O"),
            }, ct);
        return new SessionHandle(session, token, true);
    }

    public Task<bool> KeepAliveAsync(Tenant tenant, string? cookieToken, CancellationToken ct) =>
        TouchAsync(tenant, cookieToken, clock.GetUtcNow(), ct);

    public async Task<SessionHandle?> RotateAsync(Tenant tenant, string? cookieToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!WellFormed(cookieToken)) return null;
        var oldId = IdFromToken(cookieToken!);
        var token = NewToken();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var old = await db.ChatSessions.AsNoTracking()
            .Where(SessionLiveness.IsLive(db, now))
            .FirstOrDefaultAsync(x => x.Id == oldId && x.TenantId == tenant.Id, ct);
        if (old is null) return null;
        var next = new ChatSession
        {
            Id = IdFromToken(token),
            ConversationId = old.ConversationId,
            Tier = old.Tier,
            TenantId = old.TenantId,
            OidcSub = old.OidcSub,
            AgentMode = old.AgentMode,
            Paused = old.Paused,
            CreatedAt = old.CreatedAt,
            LastActivityAt = now,
            AbsoluteExpiresAt = old.AbsoluteExpiresAt,
        };
        var removed = await db.ChatSessions.Where(x => x.Id == oldId).ExecuteDeleteAsync(ct);
        if (removed != 1) return null; // lost a race with a concurrent rotation or logout
        db.ChatSessions.Add(next);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new SessionHandle(next, token, false);
    }

    public async Task EndAsync(Tenant tenant, string? cookieToken, CancellationToken ct)
    {
        if (!WellFormed(cookieToken)) return;
        var id = IdFromToken(cookieToken!);
        await db.ChatSessions.Where(x => x.Id == id && x.TenantId == tenant.Id).ExecuteDeleteAsync(ct);
    }
}
