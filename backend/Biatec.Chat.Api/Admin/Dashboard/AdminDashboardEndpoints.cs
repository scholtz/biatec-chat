using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Evidence;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Admin.Dashboard;

/// <summary>What the browser learns of a running impersonation: not the dashboard's internal row id.</summary>
public sealed record ImpersonationView(string TenantId, string Sub, string Reason, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt)
{
    public static ImpersonationView? Of(AdminImpersonation? i) => i is null ? null : new(i.TenantId, i.Sub, i.Reason, i.StartedAt, i.ExpiresAt);
}

public sealed record MeResponse(string Email, string TenantId, DateTimeOffset ExpiresAt, ImpersonationView? Impersonation);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StartImpersonationRequest(string? TenantId, string? Sub, string? Reason);

/// <summary>Start and stop of an impersonation, recorded in the admin evidence chain. Running out is not an entry of its own: the start entry holds its end time.</summary>
public static class AdminImpersonations
{
    public const int MinReason = 8;
    public const int MaxReason = 500;

    public static Task RecordStopAsync(IEvidenceLog log, AdminSession session, AdminImpersonation imp, string how, CancellationToken ct) =>
        AdminEvidence.AppendDashboardAsync(log, session.Email, AdminEvidence.ImpersonationStop, Target(imp), imp.Reason,
            new JsonObject { ["startedAt"] = imp.StartedAt.ToString("O") }, new JsonObject { ["how"] = how }, ct);

    /// <summary>Records a stop; a failure is logged (type only), never thrown, because the view is already closed when this runs.</summary>
    public static async Task TryRecordStopAsync(IEvidenceLog log, ILogger logger, AdminSession session, AdminImpersonation imp, string how, CancellationToken ct)
    {
        try { await RecordStopAsync(log, session, imp, how, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("The end ({How}) of an impersonation by {Email} could not be recorded: {ErrorType}", how, Onboarding.OperatorAuth.Clean(session.Email), ex.GetType().Name);
        }
    }

    /// <summary>
    /// The user is named by tenant and the random id of their dashboard row. The chain is permanent and signed, so it holds neither
    /// the subject nor anything derived from it: erasing the user deletes the row, and the id then names no one.
    /// </summary>
    public static string Target(AdminImpersonation imp) => $"user:{imp.TenantId}:{imp.UserId:N}";

    /// <summary>A reason as the audit log keeps it: trimmed, no control characters.</summary>
    public static string? CleanReason(string? reason)
    {
        var t = new string((reason ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length is >= MinReason and <= MaxReason ? t : null;
    }
}

/// <summary>
/// The dashboard: static app at <c>/admin/</c> and JSON API at <c>/admin/api</c> (docs/admin-dashboard.md). Everything is behind
/// <see cref="AdminAuth.RequireAdmin"/> except the static files and the sign-in redirect.
/// </summary>
public static class AdminDashboardEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static IServiceCollection AddAdminDashboard(this IServiceCollection services)
    {
        services.AddSingleton<AdminAllowList>();
        services.AddSingleton<IAdminSessionStore, RedisAdminSessionStore>();
        services.AddScoped<AdminDashboardService>();
        services.AddMemoryCache();
        return services;
    }

    public static IEndpointRouteBuilder MapAdminDashboard(this IEndpointRouteBuilder app)
    {
        // Resolved at start so a malformed ADMINS value is reported in the start-up log, not on the first request.
        _ = app.ServiceProvider.GetRequiredService<AdminAllowList>();
        app.MapAdminLogin();

        var api = app.MapGroup("/admin/api").RequireRateLimiting(AdminEndpoints.DashboardRateLimitPolicy).RequireAdmin();

        api.MapGet("/me", (HttpContext ctx, TimeProvider clock) =>
        {
            var p = AdminAuth.Current(ctx);
            return Results.Json(new MeResponse(p.Email, p.Session.TenantId, p.Session.ExpiresAt, ImpersonationView.Of(p.Impersonation(clock.GetUtcNow()))), Json);
        });

        api.MapPost("/logout", async (HttpContext ctx, IAdminSessionStore store, IEvidenceLog log, TimeProvider clock, ILogger<AdminDashboardService> logger) =>
        {
            var p = AdminAuth.Current(ctx);
            // The session ends first: a failing evidence write must never leave a signed-out admin signed in.
            await store.DeleteAsync(p.TokenHash, ctx.RequestAborted);
            AdminAuth.ClearCookie(ctx.Response);
            if (p.Impersonation(clock.GetUtcNow()) is { } imp) await AdminImpersonations.TryRecordStopAsync(log, logger, p.Session, imp, "logout", ctx.RequestAborted);
            try
            {
                await AdminEvidence.AppendDashboardAsync(log, p.Email, AdminEvidence.DashboardSignOut, "dashboard", null, null, null, ctx.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("The dashboard sign-out of {Email} could not be recorded: {ErrorType}", Onboarding.OperatorAuth.Clean(p.Email), ex.GetType().Name);
            }
            logger.LogInformation("Admin {Email} signed out of the dashboard.", Onboarding.OperatorAuth.Clean(p.Email));
            return Results.NoContent();
        });

        // Just the tenants, for the filters: the overview statistics are far too heavy to load for a dropdown.
        api.MapGet("/tenants", async (AdminDashboardService svc, CancellationToken ct) => Results.Json(new { tenants = await svc.TenantsAsync(ct) }, Json));

        api.MapGet("/stats", async (HttpContext ctx, AdminDashboardService svc, CancellationToken ct) =>
        {
            if (Repeated(ctx)) return Bad("Each parameter may be given once.");
            var days = 30;
            if (ctx.Request.Query.TryGetValue("days", out var d) && (!int.TryParse(d, out days) || days is < 7 or > 365))
                return Bad("days must be between 7 and 365.");
            var tenant = Single(ctx, "tenant");
            if (tenant is { Length: > 128 }) return Bad("tenant is too long.");
            if (tenant is not null && !await svc.TenantExistsAsync(tenant, ct)) return NotFound("Unknown tenant.");
            return Results.Json(await svc.StatsAsync(days, tenant, ct), Json);
        });

        api.MapGet("/users", async (HttpContext ctx, AdminDashboardService svc, CancellationToken ct) =>
        {
            if (Repeated(ctx)) return Bad("Each parameter may be given once.");
            var sort = Single(ctx, "sort") ?? "lastActivity";
            var dir = Single(ctx, "dir") ?? "desc";
            if (!AdminDashboardService.SortFields.Contains(sort) || dir is not ("asc" or "desc")) return Bad("Unknown sort or direction.");
            int page = 1, pageSize = 25;
            if ((ctx.Request.Query.TryGetValue("page", out var pg) && (!int.TryParse(pg, out page) || page < 1 || page > 100_000)) ||
                (ctx.Request.Query.TryGetValue("pageSize", out var ps) && (!int.TryParse(ps, out pageSize) || pageSize is < 1 or > 100)))
                return Bad("page must be 1 or more and pageSize between 1 and 100.");
            var q = Single(ctx, "q");
            if (q is { Length: > 200 }) return Bad("q is too long.");
            return Results.Json(await svc.UsersAsync(q, Single(ctx, "tenant"), sort, dir == "desc", page, pageSize, ct), Json);
        });

        api.MapGet("/user", async (HttpContext ctx, AdminDashboardService svc, CancellationToken ct) =>
        {
            if (Repeated(ctx) || Single(ctx, "tenant") is not { } tenant || Single(ctx, "sub") is not { } sub) return Bad("tenant and sub are required, once each.");
            return await svc.UserAsync(tenant, sub, ct) is { } detail ? Results.Json(detail, Json) : NotFound("Unknown user.");
        });

        // ---- impersonation: a read-only view as another user ----

        api.MapPost("/impersonation", async (HttpContext ctx, AdminDashboardService svc, IAdminSessionStore store, IEvidenceLog log,
            IOptions<AdminOptions> options, TimeProvider clock, ILogger<AdminDashboardService> logger) =>
        {
            if (!ctx.Request.HasJsonContentType()) return Results.Json(new { error = "Content-Type must be application/json." }, statusCode: 415);
            StartImpersonationRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<StartImpersonationRequest>(ctx.RequestAborted); }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException) { return Bad("The body must be JSON with tenantId, sub and reason."); }
            if (body is not { TenantId.Length: > 0, Sub.Length: > 0 }) return Bad("tenantId and sub are required.");
            if (AdminImpersonations.CleanReason(body.Reason) is not { } reason)
                return Bad($"A reason of {AdminImpersonations.MinReason} to {AdminImpersonations.MaxReason} characters is required.");
            var user = await svc.FindUserAsync(body.TenantId, body.Sub, ctx.RequestAborted);
            if (user is null) return NotFound("Unknown user.");

            var p = AdminAuth.Current(ctx);
            var now = clock.GetUtcNow();
            var session = p.Session;
            var previous = p.Impersonation(now);
            var end = now.AddMinutes(Math.Max(1, options.Value.ImpersonationMinutes));
            var imp = new AdminImpersonation
            {
                TenantId = user.TenantId, Sub = user.OidcSub, UserId = user.Id, Reason = reason, StartedAt = now,
                ExpiresAt = end < session.ExpiresAt ? end : session.ExpiresAt,
            };
            // The start is in the chain before the view opens: no evidence, no impersonation (a failing append throws and nothing changed).
            await AdminEvidence.AppendDashboardAsync(log, session.Email, AdminEvidence.ImpersonationStart, AdminImpersonations.Target(imp), reason, null,
                new JsonObject { ["expiresAt"] = imp.ExpiresAt.ToString("O"), ["mode"] = "read-only" }, ctx.RequestAborted);
            session.Impersonation = imp;
            // Only a session that still exists: a sign-out that raced this request stays a sign-out. The start then never took effect, and the chain says so.
            if (!await store.ReplaceAsync(p.TokenHash, session, AdminAuth.IdleTtl(session, options.Value, now), ctx.RequestAborted))
            {
                await AdminImpersonations.TryRecordStopAsync(log, logger, session, imp, "aborted", ctx.RequestAborted);
                return Results.Json(new { error = "Sign in required." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            // The one it replaced is closed in the chain once the new one is in force; a failure here is logged, not fatal.
            if (previous is not null) await AdminImpersonations.TryRecordStopAsync(log, logger, session, previous, "replaced", ctx.RequestAborted);
            logger.LogInformation("Admin {Email} started a read-only impersonation of a user of tenant {TenantId} until {ExpiresAt:O}.", Onboarding.OperatorAuth.Clean(session.Email), Onboarding.OperatorAuth.Clean(imp.TenantId), imp.ExpiresAt);
            return Results.Json(ImpersonationView.Of(imp), Json);
        });

        api.MapGet("/impersonation", (HttpContext ctx, TimeProvider clock) =>
            AdminAuth.Current(ctx).Impersonation(clock.GetUtcNow()) is { } imp ? Results.Json(ImpersonationView.Of(imp), Json) : NotFound("No impersonation is active."));

        api.MapDelete("/impersonation", async (HttpContext ctx, IAdminSessionStore store, IEvidenceLog log, IOptions<AdminOptions> options, TimeProvider clock, ILogger<AdminDashboardService> logger) =>
        {
            var p = AdminAuth.Current(ctx);
            var now = clock.GetUtcNow();
            if (p.Session.Impersonation is not { } imp) return Results.Json(new { error = "No impersonation is active." }, statusCode: StatusCodes.Status409Conflict);
            // Ended first: a failing evidence write must never leave the view open. One that already ran out is cleared the same way.
            p.Session.Impersonation = null;
            if (!await store.ReplaceAsync(p.TokenHash, p.Session, AdminAuth.IdleTtl(p.Session, options.Value, now), ctx.RequestAborted))
                return Results.Json(new { error = "Sign in required." }, statusCode: StatusCodes.Status401Unauthorized);
            if (imp.ExpiresAt > now) await AdminImpersonations.TryRecordStopAsync(log, logger, p.Session, imp, "stopped", ctx.RequestAborted);
            return Results.NoContent();
        });

        // The view endpoints take no user: it is the one in the admin's session, so a request can never be pointed at another.
        api.MapGet("/view/conversations", async (HttpContext ctx, AdminDashboardService svc, TimeProvider clock, CancellationToken ct) =>
        {
            if (AdminAuth.Current(ctx).Impersonation(clock.GetUtcNow()) is not { } imp) return NotFound("No impersonation is active.");
            return Results.Json(new { user = new { imp.TenantId, imp.Sub }, conversations = await svc.ConversationsAsync(imp.TenantId, imp.Sub, ct) }, Json);
        });

        api.MapGet("/view/conversations/{id:guid}/messages", async (Guid id, HttpContext ctx, AdminDashboardService svc, IEvidenceLog log, TimeProvider clock, CancellationToken ct) =>
        {
            var p = AdminAuth.Current(ctx);
            if (p.Impersonation(clock.GetUtcNow()) is not { } imp) return NotFound("No impersonation is active.");
            var transcript = await svc.TranscriptAsync(imp.TenantId, imp.Sub, id, ct);
            if (transcript is null) return NotFound("Unknown conversation.");
            // Reading a user's words is what impersonation is for, and each conversation opened is in the signed chain.
            await AdminEvidence.AppendDashboardAsync(log, p.Email, AdminEvidence.ImpersonationViewConversation, AdminImpersonations.Target(imp), imp.Reason,
                null, new JsonObject { ["conversationId"] = id.ToString(), ["messages"] = transcript.Messages.Count }, ct);
            return Results.Json(transcript, Json);
        });

        MapUi(app);
        return app;
    }

    private static string? Single(HttpContext ctx, string key) =>
        ctx.Request.Query.TryGetValue(key, out var v) && v.Count == 1 && !string.IsNullOrWhiteSpace(v[0]) ? v[0] : null;

    /// <summary>A key sent twice is an error, never read as "no filter".</summary>
    private static bool Repeated(HttpContext ctx) => ctx.Request.Query.Any(kv => kv.Value.Count > 1);

    private static IResult Bad(string message) => Results.Json(new { error = message }, statusCode: StatusCodes.Status400BadRequest);
    private static IResult NotFound(string message) => Results.Json(new { error = message }, statusCode: StatusCodes.Status404NotFound);

    // ---- the static app ----

    private const string Csp = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; font-src 'self'; " +
                               "object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private static void MapUi(IEndpointRouteBuilder app)
    {
        // Routing ignores the trailing slash, so "/admin" and "/admin/" reach the same handler: only the bare path is redirected.
        app.MapMethods("/admin", [HttpMethods.Get, HttpMethods.Head], (HttpContext ctx, AdminAllowList allow) =>
            !allow.Enabled || ctx.GetTenant() is null ? Results.NotFound()
            : ctx.Request.Path.Value!.EndsWith('/') ? ServeFile(ctx, null)
            : Results.Redirect(ctx.Request.PathBase.Value + "/admin/", permanent: false));
        app.MapMethods("/admin/{**file}", [HttpMethods.Get, HttpMethods.Head], (string? file, HttpContext ctx) => ServeFile(ctx, file));
    }

    private static IResult ServeFile(HttpContext ctx, string? file)
    {
        var allow = ctx.RequestServices.GetRequiredService<AdminAllowList>();
        var options = ctx.RequestServices.GetRequiredService<IOptions<AdminOptions>>();
        // /admin/api and /admin/auth are routes of their own; an unknown path under them is not a file.
        if (!allow.Enabled || ctx.GetTenant() is null) return Results.NotFound();
        var res = ctx.Response;
        res.Headers.ContentSecurityPolicy = Csp;
        res.Headers.XContentTypeOptions = "nosniff";
        res.Headers["Referrer-Policy"] = "no-referrer";
        res.Headers["X-Frame-Options"] = "DENY";
        res.Headers["Cross-Origin-Opener-Policy"] = "same-origin";

        var root = Path.GetFullPath(options.Value.UiDirectory is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "admin-ui"));
        var path = string.IsNullOrEmpty(file) ? "index.html" : file;
        if (path.StartsWith("api/", StringComparison.Ordinal) || path.StartsWith("auth/", StringComparison.Ordinal) || path.Contains('\\') || path.Contains("..") || path.Contains('\0'))
            return Results.NotFound();

        var full = Path.GetFullPath(Path.Combine(root, path));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
            return Results.NotFound();
        if (!ContentTypes.TryGetContentType(path, out var type)) return Results.NotFound();
        // The shell is never cached; hashed assets under assets/ can be.
        res.Headers.CacheControl = path.StartsWith("assets/", StringComparison.Ordinal) ? "public, max-age=31536000, immutable" : "no-store";
        return Results.File(full, type);
    }
}
