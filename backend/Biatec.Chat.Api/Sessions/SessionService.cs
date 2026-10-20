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

    private static TimeSpan _inFlightGrace = TimeSpan.FromSeconds(30);
    /// <summary>Slack past a consent or step deadline during which the session still counts as in flight (several
    /// 2-second consent poll intervals plus margin). Set from <c>Sessions:InFlightGrace</c>, clamped to 0..5 minutes.
    /// It never extends the absolute lifetime.</summary>
    public static TimeSpan InFlightGrace
    {
        get => _inFlightGrace;
        set => _inFlightGrace = value < TimeSpan.Zero ? TimeSpan.Zero : value > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : value;
    }
}

/// <summary>
/// The one "is this session live" test (T-080), shared by the request path (resolve, keepalive, rotation, the auth,
/// consent and evidence export endpoints) and the sweeper, so they can never disagree. The absolute lifetime always
/// applies; the idle timeout applies unless an action of the session's conversation is in flight (an open
/// ConsentRequest, or a confirmed action mid-signing; spec, Action Preview and Minimal lifetime). EF Core expressions,
/// translated to one SQL predicate with a correlated EXISTS.
/// </summary>
public static class SessionLiveness
{
    private static readonly ActionPreviewStatus[] InFlightPreview = [ActionPreviewStatus.Confirmed, ActionPreviewStatus.InProgress];
    // AwaitingConsent is left out on purpose: that step is in flight exactly as long as its ConsentRequest is open.
    private static readonly StepStatus[] InFlightStep = [StepStatus.Pending, StepStatus.Signed, StepStatus.Submitted];

    /// <summary>
    /// Correlated EXISTS on the session's own conversation: an open ConsentRequest, or a confirmed action with a step
    /// still to sign, signed or submitted (covers the gap between one step's approval and the next step's request). Both are bounded by their own expiry plus <see cref="SessionPolicy.InFlightGrace"/>, so the poll
    /// right after a deadline still reaches the session and can record the timeout or collect a last-second approval.
    /// Uses ActionPreviews (ConversationId, Status), ActionSteps (PreviewId, Seq) and ConsentRequests (StepId, Status, ExpiresAt).
    /// </summary>
    private static Expression<Func<ChatSession, bool>> InFlight(ChatDbContext db, DateTimeOffset now)
    {
        var cutoff = now - SessionPolicy.InFlightGrace;
        return s => db.ActionPreviews.Any(p => p.ConversationId == s.ConversationId && db.ActionSteps.Any(st => st.PreviewId == p.Id && (
            (InFlightPreview.Contains(p.Status) && InFlightStep.Contains(st.Status) && st.ExpiresAt > cutoff)
            || db.ConsentRequests.Any(c => c.StepId == st.Id && c.Status == ConsentStatus.Pending && c.ExpiresAt > cutoff))));
    }

    /// <summary>
    /// Live: inside the absolute lifetime, and inside the idle timeout or with an action in flight. The cheap idle
    /// comparison comes first in the OR, so the EXISTS only runs for a session that is already idle.
    /// </summary>
    public static Expression<Func<ChatSession, bool>> IsLive(ChatDbContext db, DateTimeOffset now)
    {
        var idleCutoff = now - SessionPolicy.IdleTimeout;
        var inFlight = InFlight(db, now);
        var s = inFlight.Parameters[0];
        Expression<Func<ChatSession, bool>> absolute = x => x.AbsoluteExpiresAt > now;
        Expression<Func<ChatSession, bool>> active = x => x.LastActivityAt > idleCutoff;
        return Expression.Lambda<Func<ChatSession, bool>>(
            Expression.AndAlso(Rebind(absolute, s), Expression.OrElse(Rebind(active, s), inFlight.Body)), s);
    }

    /// <summary>
    /// The exact complement of <see cref="IsLive"/> in De Morgan form, so it stays index friendly: past the absolute
    /// lifetime, or idle with nothing in flight. What the sweeper ends.
    /// </summary>
    public static Expression<Func<ChatSession, bool>> IsDue(ChatDbContext db, DateTimeOffset now)
    {
        var idleCutoff = now - SessionPolicy.IdleTimeout;
        var inFlight = InFlight(db, now);
        var s = inFlight.Parameters[0];
        Expression<Func<ChatSession, bool>> expired = x => x.AbsoluteExpiresAt <= now;
        Expression<Func<ChatSession, bool>> idle = x => x.LastActivityAt <= idleCutoff;
        return Expression.Lambda<Func<ChatSession, bool>>(
            Expression.OrElse(Rebind(expired, s), Expression.AndAlso(Rebind(idle, s), Expression.Not(inFlight.Body))), s);
    }

    private static Expression Rebind(Expression<Func<ChatSession, bool>> e, ParameterExpression s) =>
        new Rebinder(e.Parameters[0], s).Visit(e.Body);

    private sealed class Rebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }

    /// <summary>
    /// Records activity on a session after a consent resolves or a step signs, so the idle window restarts where the
    /// in-flight exemption ends. Only LastActivityAt moves (never backwards); the absolute expiry is untouched.
    /// </summary>
    public static Task TouchByIdAsync(ChatDbContext db, Guid sessionId, DateTimeOffset now, CancellationToken ct) =>
        db.ChatSessions.Where(x => x.Id == sessionId && x.AbsoluteExpiresAt > now)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.LastActivityAt, x => x.LastActivityAt > now ? x.LastActivityAt : now), ct);
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
