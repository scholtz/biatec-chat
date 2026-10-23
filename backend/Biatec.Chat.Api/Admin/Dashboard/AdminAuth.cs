using System.Text.Json.Nodes;
using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Hosting;
using Biatec.Chat.Api.Security;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Evidence;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Admin.Dashboard;

/// <summary>The admin a request belongs to, set by <see cref="AdminAuth.RequireAdmin"/>.</summary>
public sealed record AdminPrincipal(AdminSession Session, string TokenHash)
{
    public string Email => Session.Email;

    /// <summary>The running impersonation, or null (none, or it ended).</summary>
    public AdminImpersonation? Impersonation(DateTimeOffset now) => Session.Impersonation is { } i && i.ExpiresAt > now ? i : null;
}

/// <summary>
/// Guards every <c>/admin/api</c> call. An admin session is an opaque, server-side record behind a
/// <c>__Host-</c> cookie (HttpOnly, Secure, SameSite=Strict). On each request: the dashboard must be enabled, the call
/// must come from the dashboard's own origin (no CORS exists on <c>/admin</c>: <c>Sec-Fetch-Site</c>, the custom header
/// and an exact <c>Origin</c> on writes), the session must exist and be unexpired, belong to the host's tenant, and its
/// email must still be on <c>Admin:Admins</c>. A non-admin always gets the same 401 (the allow-list is never revealed).
/// </summary>
public static class AdminAuth
{
    public const string CookieName = "__Host-biatec_admin";
    public const string LoginCookieName = "__Host-biatec_admin_login";
    public const string CsrfHeader = "X-Biatec-Admin";
    /// <summary>The <see cref="PendingAuth.Purpose"/> of an admin sign-in.</summary>
    public const string Purpose = "admin";

    private const string ItemKey = "Biatec.Chat.Admin";

    public static RouteGroupBuilder RequireAdmin(this RouteGroupBuilder group) => group.AddEndpointFilter(async (c, next) =>
    {
        var ctx = c.HttpContext;
        ctx.Response.Headers.CacheControl = "no-store";
        var allow = ctx.RequestServices.GetRequiredService<AdminAllowList>();
        var tenant = ctx.GetTenant();
        if (!allow.Enabled || tenant is null) return Results.NotFound();

        var safe = HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method);
        if (!SameOrigin(ctx.Request, tenant, safe)) return Results.Json(new { error = "Forbidden." }, statusCode: StatusCodes.Status403Forbidden);

        var clock = ctx.RequestServices.GetRequiredService<TimeProvider>();
        var options = ctx.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value;
        var store = ctx.RequestServices.GetRequiredService<IAdminSessionStore>();
        var now = clock.GetUtcNow();

        var token = ctx.Request.Cookies.TryGetValue(CookieName, out var t) ? t : null;
        if (token is not { Length: > 0 and <= 128 }) return Unauthorized();
        var hash = AuthEndpoints.Hash(token);
        var session = await store.GetAsync(hash, ctx.RequestAborted);
        if (session is null) return Unauthorized();
        if (session.ExpiresAt <= now || session.TenantId != tenant.Id || !allow.Contains(session.Email))
        {
            await store.DeleteAsync(hash, ctx.RequestAborted);
            ClearCookie(ctx.Response);
            return Unauthorized();
        }

        var principal = new AdminPrincipal(session, hash);
        if (session.Impersonation is { } imp && imp.ExpiresAt <= now)
        {
            // Seen ended: say so in the chain once, then forget it.
            session.Impersonation = null;
            await AdminImpersonations.RecordStopAsync(ctx.RequestServices.GetRequiredService<IEvidenceLog>(), session, imp, "expired", ctx.RequestAborted);
            await store.ReplaceAsync(hash, session, IdleTtl(session, options, now), ctx.RequestAborted);
        }
        else
        {
            await store.TouchAsync(hash, IdleTtl(session, options, now), ctx.RequestAborted);
        }

        ctx.Items[ItemKey] = principal;
        return await next(c);
    });

    public static AdminPrincipal Current(HttpContext ctx) =>
        ctx.Items[ItemKey] as AdminPrincipal ?? throw new InvalidOperationException("RequireAdmin must pass before the admin is read.");

    private static IResult Unauthorized() => Results.Json(new { error = "Sign in required." }, statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// The dashboard's own origin only. Browsers send <c>Sec-Fetch-Site</c> on every request: anything but
    /// <c>same-origin</c> is refused (<c>none</c>, a typed URL, only for reads). A write also needs the custom header and an
    /// <c>Origin</c> equal to the backend's origin (the origin of the tenant's registered redirect URI).
    /// </summary>
    internal static bool SameOrigin(HttpRequest req, Biatec.Chat.Core.Data.Tenant tenant, bool safe)
    {
        var site = req.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site != "same-origin" && !(safe && site == "none")) return false;
        if (safe) return true;
        var header = req.Headers[CsrfHeader];
        if (header.Count != 1 || header[0] != "1") return false;
        var origin = req.Headers["Origin"];
        var own = OriginPolicy.Canonicalize(BackendUrls.TryBase(tenant, ""));
        return origin.Count == 1 && own is not null && OriginPolicy.Canonicalize(origin[0]) == own;
    }

    public static TimeSpan IdleTtl(AdminSession s, AdminOptions o, DateTimeOffset now)
    {
        var idle = TimeSpan.FromMinutes(Math.Max(1, o.SessionIdleMinutes));
        var left = s.ExpiresAt - now;
        return left < idle ? (left > TimeSpan.Zero ? left : TimeSpan.FromSeconds(1)) : idle;
    }

    public static void WriteCookie(HttpResponse res, string name, string token, TimeSpan maxAge, SameSiteMode sameSite) =>
        res.Cookies.Append(name, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = sameSite,
            Path = "/",
            Domain = null,
            IsEssential = true,
            MaxAge = maxAge > TimeSpan.Zero ? maxAge : TimeSpan.Zero,
        });

    public static void ClearCookie(HttpResponse res) =>
        res.Cookies.Append(CookieName, "", new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.Zero });
}
