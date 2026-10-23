using System.Security.Cryptography;
using System.Text;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Hosting;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Auth;

/// <summary>Response of POST /auth/popup. The nonce inside <see cref="LoginUrl"/> is single use.</summary>
public sealed record PopupStartResponse
{
    public required string AuthRequestId { get; init; }
    /// <summary>Absolute URL the widget opens in the popup.</summary>
    public required string LoginUrl { get; init; }
}

/// <summary>Response of GET /auth/popup/{id}: status only.</summary>
public sealed record PopupStatusResponse
{
    public required string AuthRequestId { get; init; }
    public required SignInStatus Status { get; init; }
}

public static class AuthEndpoints
{
    public const string LoginScope = "openid profile email sign";
    private static readonly TimeSpan PopupLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private const string ConsumedMarker = "consumed";

    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        // Step 1, from script (custom header and origin allow-list apply): create the popup request and its one-time nonce.
        app.MapPost("/auth/popup", async (HttpContext ctx, ISessionService sessions, ChatDbContext db, TimeProvider clock) =>
        {
            Harden(ctx);
            var tenant = ctx.GetTenant()!;
            var now = clock.GetUtcNow();
            var handle = await sessions.ResolveOrCreateAsync(tenant, SessionEndpoints.ReadCookie(ctx.Request), ctx.RequestAborted);
            if (handle.NewToken is not null) SessionEndpoints.WriteCookie(ctx.Response, handle.NewToken, handle.Session.AbsoluteExpiresAt, now);

            // One open request per conversation: starting another retires the earlier pending one.
            await db.PopupAuthRequests
                .Where(p => p.ConversationId == handle.Session.ConversationId && p.Purpose == PopupAuthPurpose.Login && p.Status == PopupAuthStatus.Pending)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PopupAuthStatus.Expired), ctx.RequestAborted);

            var nonce = Random64();
            var req = new PopupAuthRequest
            {
                Id = Guid.NewGuid(),
                ConversationId = handle.Session.ConversationId,
                Purpose = PopupAuthPurpose.Login,
                NonceHash = Hash(nonce),
                Status = PopupAuthStatus.Pending,
                ExpiresAt = now + PopupLifetime,
            };
            db.PopupAuthRequests.Add(req);
            await db.SaveChangesAsync(ctx.RequestAborted);

            // The popup targets the tenant's own backend (origin from its registered redirect URI, plus the mount prefix of the host), never a request value.
            var origin = BackendUrls.Base(tenant, ctx.Request);
            return Results.Ok(new PopupStartResponse
            {
                AuthRequestId = req.Id.ToString(),
                LoginUrl = $"{origin}/auth/login?req={req.Id}&nonce={Uri.EscapeDataString(nonce)}",
            });
        }).WithName("StartAuthPopup").Produces<PopupStartResponse>().LimitSessionCreation();

        // Step 2, a navigation in the popup. Needs the live session cookie and the one-time nonce of a request of that session.
        app.MapGet("/auth/login", async (HttpContext ctx, ChatDbContext db, TimeProvider clock, IOidcClient oidc, IAuthStateStore states, ILogger<AuthLog> log) =>
        {
            Harden(ctx);
            var tenant = ctx.GetTenant()!;
            if (!NavigationAllowed(ctx.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var now = clock.GetUtcNow();
            var session = await FindLiveAsync(db, tenant, SessionEndpoints.ReadCookie(ctx.Request), now, ctx.RequestAborted);
            var nonce = ctx.Request.Query["nonce"].ToString();
            if (session is null || !Guid.TryParse(ctx.Request.Query["req"], out var reqId) || nonce.Length is 0 or > 128)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            // Consume the nonce atomically: only a request of this very session, still pending, unexpired and never used.
            var hash = Hash(nonce);
            var used = await db.PopupAuthRequests
                .Where(p => p.Id == reqId && p.ConversationId == session.ConversationId && p.Purpose == PopupAuthPurpose.Login
                            && p.Status == PopupAuthStatus.Pending && p.ExpiresAt > now && p.NonceHash == hash)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.NonceHash, ConsumedMarker), ctx.RequestAborted);
            if (used != 1) return Results.StatusCode(StatusCodes.Status403Forbidden);

            try
            {
                oidc.EnsureConfigured(tenant);
                var state = Random64();
                var oidcNonce = Random64();
                var verifier = Random64();
                await states.PutAsync(Hash(state), new PendingAuth
                {
                    SessionId = session.Id.ToString("N"),
                    TenantId = tenant.Id,
                    PopupId = reqId,
                    Purpose = "login",
                    Nonce = oidcNonce,
                    Verifier = verifier,
                    Scope = LoginScope,
                }, StateLifetime, ctx.RequestAborted);
                var url = await oidc.BuildAuthorizeUrlAsync(tenant, LoginScope, state, oidcNonce, Challenge(verifier), ctx.RequestAborted);
                return Results.Redirect(url.AbsoluteUri);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Could not start sign-in: {ErrorType}", ex.GetType().Name);
                await SetStatusAsync(db, reqId, session.ConversationId, PopupAuthStatus.Failed, ctx.RequestAborted);
                return ClosePage(ctx, 503);
            }
        }).WithName("AuthLogin");

        // Step 3, the redirect back from Biatec OIDC. Always answers with the same minimal close-popup page.
        app.MapGet("/auth/callback", async (HttpContext ctx, ChatDbContext db, TimeProvider clock, ISessionService sessions,
            IAuthStateStore states, IOidcClient oidc, IIdTokenValidator validator, ITokenVault vault, IOptions<OidcOptions> options, ILogger<AuthLog> log) =>
        {
            Harden(ctx);
            var tenant = ctx.GetTenant()!;
            var now = clock.GetUtcNow();
            var cookie = SessionEndpoints.ReadCookie(ctx.Request);
            var state = ctx.Request.Query["state"].ToString();
            if (state.Length is 0 or > 256)
                return ctx.Request.Cookies.ContainsKey(Admin.Dashboard.AdminAuth.LoginCookieName) && !ctx.Request.Cookies.ContainsKey(Sessions.SessionPolicy.CookieName)
                    ? Admin.Dashboard.AdminLogin.Failed(ctx, ctx.Request.Query["error"].ToString() == "access_denied" ? Admin.Dashboard.AdminLogin.Errors.AccessDenied : Admin.Dashboard.AdminLogin.Errors.LoginFailed)
                    : ClosePage(ctx, 400);

            // The administrator dashboard signs in through this same registered redirect URI (docs/admin-dashboard.md). Its state
            // is bound to its own login cookie, not to a chat session, so it is recognised before the session lookup.
            var stateHash = Hash(state);
            var stored = await states.GetAsync(stateHash, ctx.RequestAborted);
            if (stored is { Purpose: Admin.Dashboard.AdminAuth.Purpose } adminPending)
            {
                // A state presented by a browser that did not start it is refused without being consumed, like a chat state of another session.
                if (!Admin.Dashboard.AdminLogin.BoundToThisBrowser(ctx, adminPending)) return Admin.Dashboard.AdminLogin.Failed(ctx, Admin.Dashboard.AdminLogin.Errors.LoginFailed);
                if (!await states.TakeAsync(stateHash, ctx.RequestAborted)) return Admin.Dashboard.AdminLogin.Failed(ctx, Admin.Dashboard.AdminLogin.Errors.LoginFailed); // replay or race
                return await Admin.Dashboard.AdminLogin.CompleteAsync(ctx, tenant, adminPending);
            }

            // Login CSRF defence: the state must belong to the session carrying this cookie. A state minted for
            // another session is rejected without being consumed or touching that session's popup request.
            var session = await FindLiveAsync(db, tenant, cookie, now, ctx.RequestAborted);
            // An administrator's sign-in whose state is gone (waited too long, back button) or that came back with an error and no state
            // ends on the dashboard's own message, not on this popup-closing page.
            if (stored is null && session is null && ctx.Request.Cookies.ContainsKey(Admin.Dashboard.AdminAuth.LoginCookieName))
                return Admin.Dashboard.AdminLogin.Failed(ctx, ctx.Request.Query["error"].ToString() == "access_denied"
                    ? Admin.Dashboard.AdminLogin.Errors.AccessDenied : Admin.Dashboard.AdminLogin.Errors.LoginFailed);
            var pending = session is null ? null : stored;
            if (session is null || pending is null || pending.TenantId != tenant.Id || pending.SessionId != session.Id.ToString("N"))
                return ClosePage(ctx, 400);
            if (!await states.TakeAsync(stateHash, ctx.RequestAborted)) return ClosePage(ctx, 400); // replay or race

            var code = ctx.Request.Query["code"].ToString();
            if (!CallbackIsUsable(ctx.Request, options.Value.Issuer))
            {
                await SetStatusAsync(db, pending.PopupId, session.ConversationId, PopupAuthStatus.Failed, ctx.RequestAborted);
                return ClosePage(ctx, 400);
            }

            if (pending.Purpose is not ("login" or Elevation.ElevationEndpoints.Purpose))
            {
                await SetStatusAsync(db, pending.PopupId, session.ConversationId, PopupAuthStatus.Failed, ctx.RequestAborted);
                return ClosePage(ctx, 400); // fail closed on an unknown purpose
            }
            if (pending.Purpose == Elevation.ElevationEndpoints.Purpose)
            {
                try
                {
                    // A superseded popup request must not still change the vault (the nonce check at /auth/elevate enforced its expiry; the state TTL bounds the rest).
                    var open = await db.PopupAuthRequests.AsNoTracking().AnyAsync(p => p.Id == pending.PopupId && p.ConversationId == session.ConversationId
                        && p.Purpose == PopupAuthPurpose.Elevate && p.Status == PopupAuthStatus.Pending, ctx.RequestAborted);
                    var ok = open && await Elevation.ElevationCompletion.CompleteAsync(tenant, session, pending, code, clock, oidc, validator, vault, ctx.RequestAborted);
                    if (ok) ctx.RequestServices.GetRequiredService<Sessions.ISessionProfile>().Invalidate(session.Id.ToString("N")); // the session's scopes changed
                    await SetStatusAsync(db, pending.PopupId, session.ConversationId, ok ? PopupAuthStatus.Completed : PopupAuthStatus.Failed, ctx.RequestAborted);
                    return ClosePage(ctx, ok ? 200 : 400);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("Scope elevation failed: {ErrorType}", ex.GetType().Name);
                    await SetStatusAsync(db, pending.PopupId, session.ConversationId, PopupAuthStatus.Failed, CancellationToken.None);
                    return ClosePage(ctx, 400);
                }
            }

            string? newSessionToken = null;
            string? newSessionId = null;
            try
            {
                var tokens = await oidc.ExchangeCodeAsync(tenant, code, pending.Verifier, ctx.RequestAborted);
                if (string.IsNullOrEmpty(tokens.IdToken) || string.IsNullOrEmpty(tokens.RefreshToken))
                    throw new OidcException("The token response lacks an ID token or refresh token.");
                var identity = await validator.ValidateAsync(tokens.IdToken, tenant.OidcClientId, pending.Nonce, ctx.RequestAborted);

                // Session fixation defence: a new id and cookie, the old id retired in the same transaction.
                var rotated = await sessions.RotateAsync(tenant, cookie, ctx.RequestAborted)
                    ?? throw new OidcException("The session ended during sign-in.");
                newSessionToken = rotated.NewToken;
                newSessionId = rotated.Session.Id.ToString("N");

                await vault.PutAsync(newSessionId, new TokenVaultEntry
                {
                    AccessToken = tokens.AccessToken,
                    AccessExpiresAt = now.AddSeconds(tokens.ExpiresIn),
                    RefreshToken = tokens.RefreshToken,
                    IdToken = tokens.IdToken,
                    Scopes = tokens.Scopes ?? pending.Scope.Split(' '),
                    AbsoluteExpiresAt = rotated.Session.AbsoluteExpiresAt,
                }, ctx.RequestAborted);
                // Re-login from an already signed-in session: the old id's entry must not outlive the old id.
                // (Revoking its refresh token at Biatec OIDC is the logout/elevation work, T-010 and T-011.)
                await vault.DeleteAsync(session.Id.ToString("N"), ctx.RequestAborted);

                await db.ChatSessions.Where(s => s.Id == rotated.Session.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.Tier, Core.Data.SessionTier.Authenticated).SetProperty(s => s.OidcSub, identity.Subject)
                        .SetProperty(s => s.AgentMode, AgentMode.Prepare).SetProperty(s => s.Paused, false), ctx.RequestAborted);
                await db.Conversations.Where(c => c.Id == rotated.Session.ConversationId)
                    .ExecuteUpdateAsync(u => u.SetProperty(c => c.OidcSub, identity.Subject), ctx.RequestAborted);
                if (ctx.RequestServices.GetService<Biatec.Chat.Core.Evidence.IEvidenceRecorder>() is { } recorder)
                    await recorder.RecordAsync(rotated.Session.ConversationId, Biatec.Chat.Core.Evidence.EvidenceKinds.TierChanged, new System.Text.Json.Nodes.JsonObject
                    {
                        ["from"] = session.Tier.ToString(), ["to"] = nameof(Core.Data.SessionTier.Authenticated), ["reason"] = "sign_in",
                        ["tenantId"] = tenant.Id, ["at"] = now.ToString("O"),
                    }, ctx.RequestAborted);

                SessionEndpoints.WriteCookie(ctx.Response, rotated.NewToken!, rotated.Session.AbsoluteExpiresAt, now);
                await SetStatusAsync(db, pending.PopupId, rotated.Session.ConversationId, PopupAuthStatus.Completed, ctx.RequestAborted);
                // After the last step that can fail the sign-in: the administrator dashboard lists only users whose sign-in completed.
                await Admin.Dashboard.KnownUsers.RecordSignInAsync(db, tenant.Id, identity, now, log, ctx.RequestAborted);
                return ClosePage(ctx, 200);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Only the exception type is logged: messages and bodies near this code can hold tokens.
                log.LogWarning("Sign-in failed: {ErrorType}", ex.GetType().Name);
                if (newSessionId is not null)
                {
                    // Never leave a half-signed-in session behind: drop the rotated session and its vault entry.
                    await vault.DeleteAsync(newSessionId, CancellationToken.None);
                    await sessions.EndAsync(tenant, newSessionToken, CancellationToken.None);
                }
                await SetStatusAsync(db, pending.PopupId, session.ConversationId, PopupAuthStatus.Failed, CancellationToken.None);
                return ClosePage(ctx, 400);
            }
        }).WithName("AuthCallback");

        // Polled by the widget with the session cookie: reports status only, never anything about the tokens.
        app.MapGet("/auth/popup/{id:guid}", async (Guid id, HttpContext ctx, ChatDbContext db, TimeProvider clock) =>
        {
            Harden(ctx);
            var tenant = ctx.GetTenant()!;
            var now = clock.GetUtcNow();
            var session = await FindLiveAsync(db, tenant, SessionEndpoints.ReadCookie(ctx.Request), now, ctx.RequestAborted);
            if (session is null) return Results.Unauthorized();
            var p = await db.PopupAuthRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ConversationId == session.ConversationId, ctx.RequestAborted);
            if (p is null) return Results.NotFound();
            var status = p.Status;
            if (status == PopupAuthStatus.Pending && p.ExpiresAt <= now)
            {
                await SetStatusAsync(db, id, session.ConversationId, PopupAuthStatus.Expired, ctx.RequestAborted);
                status = PopupAuthStatus.Expired;
            }
            return Results.Ok(new PopupStatusResponse
            {
                AuthRequestId = id.ToString(),
                Status = status switch
                {
                    PopupAuthStatus.Pending => SignInStatus.Pending,
                    PopupAuthStatus.Completed => SignInStatus.Complete,
                    _ => SignInStatus.Failed,
                },
            });
        }).WithName("GetAuthPopupStatus").Produces<PopupStatusResponse>();

        return app;
    }

    /// <summary>
    /// The redirect back from Biatec OIDC, before anything is exchanged: no <c>error</c>, a code of sane size, and (RFC 9207) no issuer
    /// other than ours, since a response that names a different issuer is a mix-up attempt. Shared by the chat and the admin sign-in.
    /// </summary>
    internal static bool CallbackIsUsable(HttpRequest req, string issuer)
    {
        var code = req.Query["code"].ToString();
        var iss = req.Query["iss"].ToString();
        var issuerMismatch = iss.Length > 0 && !string.Equals(iss.TrimEnd('/'), issuer.TrimEnd('/'), StringComparison.Ordinal);
        return !issuerMismatch && !req.Query.ContainsKey("error") && code.Length is > 0 and <= 2048;
    }

    /// <summary>
    /// Sec-Fetch checks for the login navigation: a top-level document navigation from the same site (the tenant's
    /// origins are same-site with the backend). Cross-site navigations and subresource, iframe or fetch loads are
    /// refused. Absent headers (old browsers, non-browser clients) fall through to the nonce and cookie checks.
    /// </summary>
    internal static bool NavigationAllowed(HttpRequest req)
    {
        var site = req.Headers["Sec-Fetch-Site"].ToString();
        var dest = req.Headers["Sec-Fetch-Dest"].ToString();
        var mode = req.Headers["Sec-Fetch-Mode"].ToString();
        if (site.Length > 0 && site is not ("same-origin" or "same-site" or "none")) return false;
        if (dest.Length > 0 && dest != "document") return false;
        if (mode.Length > 0 && mode != "navigate") return false;
        return true;
    }

    internal static void Harden(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Append("Vary", "Cookie");
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    /// <summary>The only HTML this flow serves: no data, no tokens, the same page for every outcome.</summary>
    internal static IResult ClosePage(HttpContext ctx, int status)
    {
        var scriptNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        ctx.Response.Headers.ContentSecurityPolicy = $"default-src 'none'; script-src 'nonce-{scriptNonce}'; base-uri 'none'; frame-ancestors 'none'";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        var html = $"<!doctype html><meta charset=\"utf-8\"><title>Biatec Chat</title><p>You can close this window.</p><script nonce=\"{scriptNonce}\">window.close()</script>";
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
    }

    /// <summary>Read-only live session lookup: unlike ResolveOrCreate it never creates a session for an unknown cookie.</summary>
    internal static async Task<ChatSession?> FindLiveAsync(ChatDbContext db, Tenant tenant, string? token, DateTimeOffset now, CancellationToken ct)
    {
        if (!SessionService.WellFormed(token)) return null;
        var id = SessionService.IdFromToken(token!);
        return await db.ChatSessions.AsNoTracking()
            .Where(SessionLiveness.IsLive(db, now))
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenant.Id, ct);
    }

    internal static Task SetStatusAsync(ChatDbContext db, Guid id, Guid conversationId, PopupAuthStatus status, CancellationToken ct) =>
        db.PopupAuthRequests.Where(p => p.Id == id && p.ConversationId == conversationId && p.Status == PopupAuthStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, status), ct);

    internal static string Random64() => Base64Url(RandomNumberGenerator.GetBytes(32));
    internal static string Hash(string value) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Logger category for the auth endpoints.</summary>
    public sealed class AuthLog;
}
