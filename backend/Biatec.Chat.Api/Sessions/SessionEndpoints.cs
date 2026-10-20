using System.Collections.Concurrent;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Api.Wallet.Read;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Tools.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using ApiTier = Biatec.Chat.Api.Contracts.SessionTier;

namespace Biatec.Chat.Api.Sessions;

public sealed record ProbeResponse
{
    /// <summary>True when this request carried a live session cookie.</summary>
    public required bool CookieReturned { get; init; }
}

public sealed record KeepAliveResponse
{
    /// <summary>The absolute expiry. Keepalive never changes it.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

public static class SessionEndpoints
{
    /// <summary>
    /// Writes the session cookie with the attributes the spec fixes, on every transport including http://localhost:
    /// __Host- prefix, HttpOnly, Secure, SameSite=Lax, Path=/, no Domain. Browsers treat localhost as a secure context.
    /// </summary>
    public static void WriteCookie(HttpResponse res, string token, DateTimeOffset absoluteExpiresAt, DateTimeOffset now)
    {
        var remaining = absoluteExpiresAt - now;
        res.Cookies.Append(SessionPolicy.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Domain = null,
            IsEssential = true,
            MaxAge = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
        });
    }

    public static string? ReadCookie(HttpRequest req) => req.Cookies.TryGetValue(SessionPolicy.CookieName, out var v) ? v : null;

    public static IEndpointRouteBuilder MapSessions(this IEndpointRouteBuilder app)
    {
        // Session state. The first call (no live cookie) creates the session and sets the cookie.
        app.MapGet("/session", async (HttpContext ctx, ISessionService sessions, ISessionProfile profile, ChatDbContext db, TimeProvider clock) =>
        {
            NoStore(ctx);
            var handle = await sessions.ResolveOrCreateAsync(ctx.GetTenant()!, ReadCookie(ctx.Request), ctx.RequestAborted);
            if (handle.NewToken is not null) WriteCookie(ctx.Response, handle.NewToken, handle.Session.AbsoluteExpiresAt, clock.GetUtcNow());
            var (email, primaryAddress, scopes) = await profile.GetProfileAsync(handle.Session, ctx.RequestAborted);
            return Results.Ok(await BuildStateAsync(db, handle.Session, email, primaryAddress, scopes, ctx.RequestAborted));
        }).WithName("GetSession").Produces<SessionState>().RegisteredOriginsOnly();

        // Cookie probe. Without a live cookie: creates the session, sets the cookie, reports false. With it: true.
        app.MapGet("/session/probe", async (HttpContext ctx, ISessionService sessions, TimeProvider clock) =>
        {
            NoStore(ctx);
            var handle = await sessions.ResolveOrCreateAsync(ctx.GetTenant()!, ReadCookie(ctx.Request), ctx.RequestAborted);
            if (handle.NewToken is not null) WriteCookie(ctx.Response, handle.NewToken, handle.Session.AbsoluteExpiresAt, clock.GetUtcNow());
            return Results.Ok(new ProbeResponse { CookieReturned = !handle.Created });
        }).WithName("ProbeSession").Produces<ProbeResponse>().RegisteredOriginsOnly();

        // Keepalive: extends the idle timeout only. An expired or missing session gets 401 and no new session.
        app.MapPost("/session/keepalive", async (HttpContext ctx, ISessionService sessions, ChatDbContext db) =>
        {
            NoStore(ctx);
            var token = ReadCookie(ctx.Request);
            if (!await sessions.KeepAliveAsync(ctx.GetTenant()!, token, ctx.RequestAborted)) return Results.Unauthorized();
            var s = await db.ChatSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == SessionService.IdFromToken(token!), ctx.RequestAborted);
            return s is null ? Results.Unauthorized() : Results.Ok(new KeepAliveResponse { ExpiresAt = s.AbsoluteExpiresAt });
        }).WithName("KeepAliveSession").Produces<KeepAliveResponse>().RegisteredOriginsOnly();

        return app;
    }

    /// <summary>A browser Origin outside the tenant allow-list gets no session and no Set-Cookie (a missing Origin is a same-origin or non-browser call).</summary>
    private static RouteHandlerBuilder RegisteredOriginsOnly(this RouteHandlerBuilder b) => b.AddEndpointFilter(async (c, next) =>
    {
        var req = c.HttpContext.Request;
        if (req.Headers.ContainsKey("Origin") && Security.OriginPolicy.AllowedOrigin(c.HttpContext.GetTenant()!, req) is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        return await next(c);
    });

    private static void NoStore(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Append("Vary", "Cookie");
    }

    /// <param name="email">From <see cref="ISessionProfile"/>, which already returns null for guests.</param>
    /// <param name="primaryAddress">From <see cref="ISessionProfile"/>, which already returns null for guests.</param>
    /// <param name="scopes">Scope names from <see cref="ISessionProfile"/>, empty for guests. Names only.</param>
    internal static async Task<SessionState> BuildStateAsync(ChatDbContext db, ChatSession s, string? email, string? primaryAddress, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        var popup = await db.PopupAuthRequests.AsNoTracking()
            .Where(p => p.ConversationId == s.ConversationId)
            .OrderByDescending(p => p.ExpiresAt)
            .FirstOrDefaultAsync(ct);
        return new SessionState
        {
            SessionId = s.Id.ToString("N"),
            Tier = s.Tier == Core.Data.SessionTier.Anonymous ? ApiTier.Anonymous : ApiTier.Authenticated,
            // Derived strings only (T-083), never token material.
            Email = email,
            PrimaryAddress = primaryAddress,
            Scopes = scopes,
            AuthRequestId = popup?.Id.ToString(),
            SignInStatus = popup?.Status switch
            {
                null => SignInStatus.None,
                PopupAuthStatus.Pending => SignInStatus.Pending,
                PopupAuthStatus.Completed => SignInStatus.Complete,
                _ => SignInStatus.Failed,
            },
            ExpiresAt = s.AbsoluteExpiresAt,
            AgentMode = Agent.AgentModePolicy.ToWire(Agent.AgentModePolicy.Effective(s)),
            Paused = s.Paused && s.Tier != Core.Data.SessionTier.Anonymous,
        };
    }
}

public static class SessionRegistration
{
    public static IServiceCollection AddSessions(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ISessionService, SessionService>();
        services.AddMemoryCache();
        services.AddOptions<SessionProfileOptions>().BindConfiguration(SessionProfileOptions.Section);
        services.AddSingleton<ISessionProfile, SessionProfile>();
        return services;
    }
}

/// <summary>What the header shows for a signed-in session: the email and the active primary address.</summary>
public interface ISessionProfile
{
    /// <summary>
    /// The only guest check: both null for guests. Never waits for Biatec MCP and never throws for a vault or MCP
    /// outage: a value that is not known yet is null, and a later poll picks it up.
    /// </summary>
    Task<(string? Email, string? PrimaryAddress)> GetAsync(ChatSession session, CancellationToken ct) =>
        GetProfileAsync(session, ct).ContinueWith(t => (t.Result.Email, t.Result.PrimaryAddress), TaskScheduler.Default);

    /// <summary>
    /// The same lookup plus the OIDC scope names the session holds (names only). Guests get an empty list. Served from
    /// the same per-session cache, so a poll never decrypts the vault entry again.
    /// </summary>
    Task<(string? Email, string? PrimaryAddress, IReadOnlyList<string> Scopes)> GetProfileAsync(ChatSession session, CancellationToken ct);

    /// <summary>Drops the cached profile after the session's tokens or scopes changed (an elevation), so the next poll reads them again.</summary>
    void Invalidate(string sessionId);
}

public sealed class SessionProfileOptions
{
    public const string Section = "Sessions:Profile";

    /// <summary>How long the background fill waits for Biatec MCP. The call itself is never cancelled (see <see cref="SessionProfile"/>).</summary>
    public TimeSpan McpTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Lifetime of a cached profile with a known address.</summary>
    public TimeSpan AddressTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Lifetime of a cached profile whose address is not known (fill pending, failed, or skipped).</summary>
    public TimeSpan FailureTtl { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Reads the profile without persisting it (spec, personal data is minimized: PostgreSQL keeps only the OIDC sub).
/// The email is the <c>email</c> claim of the ID token held encrypted in the vault (validated at sign-in, decoded
/// here, never sent to the browser). The primary address is the first entry of Biatec MCP <c>listAlgorandAddresses</c>.
/// Both are cached together in this process's memory per session id, so a poll neither decrypts the vault entry nor
/// calls Biatec MCP. A cache miss reads the vault once, answers with the email and a null address, and fills the
/// address in the background, single-flighted per session.
/// <para>
/// The header lookup must never trigger or cancel a refresh-token rotation (the bearer is read through the vault,
/// whose refresh would otherwise be cut off with a spent refresh token): the fill runs only while the stored access
/// token is valid well past the vault's refresh skew, and the MCP call is given no cancellation token at all; the
/// timeout only stops waiting for it. The single-flight slot is held until the call itself ends, so a hung Biatec MCP
/// sees one call per session, not one per poll.
/// </para>
/// </summary>
public sealed class SessionProfile(
    ITokenVault vault,
    IBiatecMcpClient mcp,
    IMemoryCache cache,
    IOptions<SessionProfileOptions> options,
    IOptions<VaultOptions> vaultOptions,
    TimeProvider clock,
    ILogger<SessionProfile> log) : ISessionProfile
{
    /// <summary>Extra validity the stored access token needs beyond the refresh skew before the fill may use it.</summary>
    public static readonly TimeSpan TokenMargin = TimeSpan.FromMinutes(1);

    private readonly SessionProfileOptions _o = options.Value;
    private readonly ConcurrentDictionary<string, Task> _fills = new();

    private sealed record Cached(string? Email, string? Address, IReadOnlyList<string> Scopes);

    private readonly ConcurrentDictionary<string, int> _generations = new();

    public void Invalidate(string sessionId)
    {
        var key = CacheKey(sessionId);
        _generations.AddOrUpdate(key, 1, (_, g) => g + 1);
        cache.Remove(key);
    }

    private static string CacheKey(string sessionId) => "biatec:session:profile:" + sessionId;

    public async Task<(string? Email, string? PrimaryAddress)> GetAsync(ChatSession session, CancellationToken ct)
    {
        var (email, address, _) = await GetProfileAsync(session, ct);
        return (email, address);
    }

    public async Task<(string? Email, string? PrimaryAddress, IReadOnlyList<string> Scopes)> GetProfileAsync(ChatSession session, CancellationToken ct)
    {
        if (session.Tier == Core.Data.SessionTier.Anonymous) return (null, null, []);
        var sessionId = session.Id.ToString("N");
        var key = CacheKey(sessionId);
        if (cache.TryGetValue<Cached>(key, out var hit) && hit is not null) return (hit.Email, hit.Address, hit.Scopes);

        TokenVaultEntry? entry;
        try { entry = await vault.GetAsync(sessionId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning("Session profile: vault read failed: {ErrorType}", ex.GetType().Name);
            return (null, null, []);
        }
        if (entry is null) return (null, null, []);

        var email = EmailOf(entry.IdToken);
        var scopes = (IReadOnlyList<string>)[.. entry.Scopes];
        var generation = _generations.GetValueOrDefault(key);
        cache.Set(key, new Cached(email, null, scopes), _o.FailureTtl);
        StartFill(key, new McpCallContext(session.TenantId, sessionId, entry.Scopes), entry.AccessExpiresAt, email, scopes, generation);
        return (email, null, scopes);
    }

    /// <summary>The <c>email</c> claim, or null. The token was validated when it entered the vault; only the claim leaves this method.</summary>
    public static string? EmailOf(string? idToken)
    {
        if (string.IsNullOrEmpty(idToken)) return null;
        try
        {
            return new JsonWebToken(idToken).TryGetPayloadValue<string>("email", out var email) && !string.IsNullOrWhiteSpace(email) ? email : null;
        }
        catch (Exception) { return null; } // malformed: never echo it
    }

    private void StartFill(string key, McpCallContext ctx, DateTimeOffset accessExpiresAt, string? email, IReadOnlyList<string> scopes, int generation)
    {
        // A token that would need a refresh is left alone: the chat path refreshes it, and a later poll fills the address.
        if (accessExpiresAt - clock.GetUtcNow() <= vaultOptions.Value.RefreshSkew + TokenMargin) return;
        var slot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_fills.TryAdd(key, slot.Task)) return;
        _ = Task.Run(async () =>
        {
            try { await FillAsync(key, ctx, email, scopes, generation); }
            catch (Exception ex) { log.LogWarning("Session profile: address fill failed: {ErrorType}", ex.GetType().Name); }
            finally
            {
                _fills.TryRemove(new KeyValuePair<string, Task>(key, slot.Task));
                slot.TrySetResult();
            }
        });
    }

    private async Task FillAsync(string key, McpCallContext ctx, string? email, IReadOnlyList<string> scopes, int generation)
    {
        Task<McpToolResult> call;
        try { call = mcp.ListAlgorandAddressesAsync(ctx, CancellationToken.None); }
        catch (Exception ex)
        {
            log.LogWarning("Session profile: Biatec MCP address read failed: {ErrorType}", ex.GetType().Name);
            return;
        }
        try
        {
            var r = await call.WaitAsync(_o.McpTimeout, clock);
            var address = r.Failed ? null : WalletReadExecutor.ParseAddresses(r.Json).FirstOrDefault();
            // An entry invalidated meanwhile (an elevation) is not resurrected with the scopes read before it.
            if (address is not null && _generations.GetValueOrDefault(key) == generation) cache.Set(key, new Cached(email, address, scopes), _o.AddressTtl);
            return;
        }
        catch (Exception ex)
        {
            log.LogWarning("Session profile: Biatec MCP address read failed: {ErrorType}", ex.GetType().Name);
        }
        // Timed out or failed: the cached null stays for FailureTtl; hold the slot until the call really ends.
        try { await call; } catch (Exception) { /* already logged */ }
    }
}
