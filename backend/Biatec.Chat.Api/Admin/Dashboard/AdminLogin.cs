using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Hosting;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Admin.Dashboard;

/// <summary>
/// Admin sign-in through Biatec OIDC (docs/admin-dashboard.md). <c>/admin/auth/login</c> starts an authorization code flow
/// with PKCE for the host's tenant (its registered client and redirect URI); the shared <c>/auth/callback</c> hands a state of
/// purpose <see cref="AdminAuth.Purpose"/> to <see cref="CompleteAsync"/>. The scope is <c>openid profile email</c>: no
/// <c>sign</c>, no <c>offline_access</c>. The tokens are used once to learn the verified email and then dropped, so
/// no token is stored for an admin and none ever reaches the browser.
/// </summary>
public static class AdminLogin
{
    public const string Scope = "openid profile email";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The fixed set of error codes the dashboard shows after a failed sign-in.</summary>
    public static class Errors
    {
        public const string NotAdmin = "not_admin";
        public const string EmailNotVerified = "email_not_verified";
        public const string LoginFailed = "login_failed";
        public const string AccessDenied = "access_denied";
    }

    public static IEndpointRouteBuilder MapAdminLogin(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/auth/login", async (HttpContext ctx, AdminAllowList allow, IOidcClient oidc, IAuthStateStore states, ILogger<AdminLoginLog> log) =>
        {
            Harden(ctx);
            var tenant = ctx.GetTenant();
            if (!allow.Enabled || tenant is null) return Results.NotFound();
            // A top-level navigation from the same site, or typed in: never a subresource, frame or fetch.
            if (!AuthEndpoints.NavigationAllowed(ctx.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                oidc.EnsureConfigured(tenant);
                // Two tabs (or a double click) share one binding: a valid cookie that is already there is kept, so neither attempt invalidates the other.
                var binding = ctx.Request.Cookies.TryGetValue(AdminAuth.LoginCookieName, out var existing) && existing is { Length: >= 40 and <= 64 }
                    ? existing : AuthEndpoints.Random64();
                var state = AuthEndpoints.Random64();
                var nonce = AuthEndpoints.Random64();
                var verifier = AuthEndpoints.Random64();
                await states.PutAsync(AuthEndpoints.Hash(state), new PendingAuth
                {
                    SessionId = AuthEndpoints.Hash(binding), // login CSRF: the state only completes in the browser that started it
                    TenantId = tenant.Id,
                    PopupId = Guid.Empty,
                    Purpose = AdminAuth.Purpose,
                    Nonce = nonce,
                    Verifier = verifier,
                    Scope = Scope,
                }, StateLifetime, ctx.RequestAborted);
                // Lax: the callback is a cross-site top-level GET from Biatec OIDC, which a Strict cookie would not accompany.
                AdminAuth.WriteCookie(ctx.Response, AdminAuth.LoginCookieName, binding, StateLifetime, SameSiteMode.Lax);
                var url = await oidc.BuildAuthorizeUrlAsync(tenant, Scope, state, nonce, AuthEndpoints.Challenge(verifier), ctx.RequestAborted);
                return Results.Redirect(url.AbsoluteUri);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Could not start admin sign-in: {ErrorType}", ex.GetType().Name);
                return Failed(ctx, Errors.LoginFailed);
            }
        }).WithName("AdminLogin").RequireRateLimiting(AdminEndpoints.DashboardRateLimitPolicy);
        return app;
    }

    /// <summary>
    /// Finishes a sign-in whose state was minted by <c>/admin/auth/login</c> (the caller has taken the state, so it works
    /// once). Always answers with a redirect to the dashboard: with a session cookie on success, with <c>?error=&lt;code&gt;</c>
    /// otherwise.
    /// </summary>
    internal static async Task<IResult> CompleteAsync(HttpContext ctx, Tenant tenant, PendingAuth pending)
    {
        Harden(ctx);
        var sp = ctx.RequestServices;
        var log = sp.GetRequiredService<ILogger<AdminLoginLog>>();
        var allow = sp.GetRequiredService<AdminAllowList>();
        var options = sp.GetRequiredService<IOptions<AdminOptions>>().Value;
        var clock = sp.GetRequiredService<TimeProvider>();
        var ct = ctx.RequestAborted;

        // Same tenant, same browser: the state was started on this host by the browser that holds the login cookie.
        var bound = ctx.Request.Cookies.TryGetValue(AdminAuth.LoginCookieName, out var binding) && binding is { Length: > 0 and <= 128 } &&
                    CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(AuthEndpoints.Hash(binding)), System.Text.Encoding.UTF8.GetBytes(pending.SessionId));
        if (!allow.Enabled || !bound || pending.TenantId != tenant.Id) return Failed(ctx, Errors.LoginFailed);

        if (ctx.Request.Query.ContainsKey("error"))
            return Failed(ctx, ctx.Request.Query["error"].ToString() == "access_denied" ? Errors.AccessDenied : Errors.LoginFailed);
        if (!AuthEndpoints.CallbackIsUsable(ctx.Request, sp.GetRequiredService<IOptions<Config.OidcOptions>>().Value.Issuer))
            return Failed(ctx, Errors.LoginFailed);
        var code = ctx.Request.Query["code"].ToString();

        IdTokenIdentity identity;
        try
        {
            var tokens = await sp.GetRequiredService<IOidcClient>().ExchangeCodeAsync(tenant, code, pending.Verifier, ct);
            if (string.IsNullOrEmpty(tokens.IdToken)) return Failed(ctx, Errors.LoginFailed);
            identity = await sp.GetRequiredService<IIdTokenValidator>().ValidateAsync(tokens.IdToken, tenant.OidcClientId, pending.Nonce, ct);
            // `tokens` goes out of scope here: nothing of it is stored.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the type: messages near token handling can hold secrets.
            log.LogWarning("Admin sign-in failed: {ErrorType}", ex.GetType().Name);
            return Failed(ctx, Errors.LoginFailed);
        }

        if (!allow.Contains(identity.Email))
        {
            log.LogWarning("Admin sign-in refused: the signed-in user is not on Admin:Admins (tenant {TenantId}, from {RemoteIp}).", tenant.Id, Onboarding.OperatorAuth.Remote(ctx));
            return Failed(ctx, Errors.NotAdmin);
        }
        if (options.RequireVerifiedEmail && identity.EmailVerified != true)
        {
            log.LogWarning("Admin sign-in refused: the ID token does not carry email_verified=true (tenant {TenantId}).", tenant.Id);
            return Failed(ctx, Errors.EmailNotVerified);
        }

        var now = clock.GetUtcNow();
        var session = new AdminSession
        {
            Email = AdminAllowList.Normalize(identity.Email!),
            Subject = identity.Subject,
            TenantId = tenant.Id,
            CreatedAt = now,
            ExpiresAt = now.AddHours(Math.Max(1, options.SessionHours)),
        };
        var token = AuthEndpoints.Random64();
        try
        {
            // The sign-in is recorded first: no evidence, no session.
            await AdminEvidence.AppendDashboardAsync(sp.GetRequiredService<IEvidenceLog>(), session.Email, AdminEvidence.DashboardSignIn, "dashboard", null, null,
                new JsonObject { ["tenantId"] = tenant.Id }, ct);
            await sp.GetRequiredService<IAdminSessionStore>().PutAsync(AuthEndpoints.Hash(token), session, AdminAuth.IdleTtl(session, options, now), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("Admin sign-in could not be recorded: {ErrorType}", ex.GetType().Name);
            return Failed(ctx, Errors.LoginFailed);
        }

        AdminAuth.WriteCookie(ctx.Response, AdminAuth.CookieName, token, session.ExpiresAt - now, SameSiteMode.Strict);
        log.LogInformation("Admin {Email} signed in to the dashboard from {RemoteIp}.", Onboarding.OperatorAuth.Clean(session.Email), Onboarding.OperatorAuth.Remote(ctx));
        return Results.Redirect(Home(tenant, ctx.Request), permanent: false);
    }

    private static string Home(Tenant tenant, HttpRequest req) => BackendUrls.Base(tenant, req) + "/admin/";

    /// <summary>The dashboard's address; a tenant without a valid redirect URI (which is why a sign-in can fail) gets the relative one.</summary>
    private static string SafeHome(HttpContext ctx) =>
        ctx.GetTenant() is { } t && BackendUrls.TryBase(t, ctx.Request.PathBase.Value ?? "") is { } b ? b + "/admin/" : (ctx.Request.PathBase.Value ?? "") + "/admin/";

    internal static IResult Failed(HttpContext ctx, string code) => Results.Redirect(SafeHome(ctx) + "?error=" + code, permanent: false);

    private static void Harden(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Append("Vary", "Cookie");
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    /// <summary>Logger category.</summary>
    public sealed class AdminLoginLog;
}
