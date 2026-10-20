using System.Text.Json;
using System.Threading.RateLimiting;
using Biatec.Chat.Api.Chat.SignIn;
using Biatec.Chat.Api.Onboarding;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Admin;

/// <summary>Admin API settings (section <c>Admin</c>). The operator key itself is <c>Onboarding:OperatorKey</c>.</summary>
public sealed class AdminOptions
{
    public const string Section = "Admin";

    /// <summary>Admin requests per window and remote IP (default 60), counted apart from public onboarding.</summary>
    public int RateLimitPermits { get; set; } = 60;

    /// <summary>Length of the rate-limit window in seconds (default 60).</summary>
    public int RateLimitWindowSeconds { get; set; } = 60;
}

/// <summary>
/// Operator admin API for tenant policy and knowledge-base pages (docs/admin-api.md). It lives under <c>/onboarding/operator/</c> on purpose:
/// that prefix already is the operator surface, so it inherits the bearer-only posture of the operator approval route
/// (no cookie, so exempt from the CSRF guard; never a CORS wildcard; Authorization is not an allowed CORS header, so a
/// browser page cannot send the bearer cross-origin), plus the onboarding body size limit. Every handler authenticates
/// (<see cref="OperatorAuth"/>) before it reads the body; the API is disabled (404) while no operator key is set.
/// </summary>
public static class AdminEndpoints
{
    public const string RateLimitPolicy = "operator";

    public static IServiceCollection AddAdmin(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AdminOptions>(config.GetSection(AdminOptions.Section));
        services.AddScoped<TenantPolicyService>();
        services.AddScoped<KbAdminService>();
        services.AddRateLimiter(o => o.AddPolicy(RateLimitPolicy, ctx =>
        {
            var opts = ctx.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value;
            return RateLimitPartition.GetFixedWindowLimiter("operator:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, opts.RateLimitPermits),
                Window = TimeSpan.FromSeconds(Math.Max(1, opts.RateLimitWindowSeconds)),
                QueueLimit = 0,
            });
        }));
        return services;
    }

    public static IEndpointRouteBuilder MapAdmin(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/onboarding/operator/tenants").RequireRateLimiting(RateLimitPolicy);

        g.MapGet("/", async (HttpContext ctx, TenantPolicyService svc, ILogger<TenantPolicyService> log, CancellationToken ct) =>
        {
            if (OperatorAuth.Check(ctx) is { } denied) return denied;
            var list = await svc.ListAsync(ct);
            log.LogInformation("Admin: operator from {RemoteIp} listed {Count} tenant policies", OperatorAuth.Remote(ctx), list.Count);
            return Results.Json(new { tenants = list });
        });

        g.MapGet("/{tenantId}", async (string tenantId, HttpContext ctx, TenantPolicyService svc, ILogger<TenantPolicyService> log, CancellationToken ct) =>
        {
            if (OperatorAuth.Check(ctx) is { } denied) return denied;
            var view = await svc.GetAsync(tenantId, ct);
            log.LogInformation("Admin: operator from {RemoteIp} read tenant policy {TenantId} (found: {Found})", OperatorAuth.Remote(ctx), OperatorAuth.Clean(tenantId), view is not null);
            return view is null ? Results.Json(new { error = "Unknown tenant." }, statusCode: 404) : Results.Json(view);
        });

        // The body is bound by hand after authentication, so an unauthenticated request never reaches the JSON parser.
        g.MapMethods("/{tenantId}/policy", [HttpMethods.Patch], async (string tenantId, HttpContext ctx, TenantPolicyService svc, ILogger<TenantPolicyService> log, CancellationToken ct) =>
        {
            if (OperatorAuth.Check(ctx) is { } denied) return denied;
            var id = OperatorAuth.Clean(tenantId);
            var remote = OperatorAuth.Remote(ctx);
            if (!ctx.Request.HasJsonContentType())
                return Results.Json(new { error = "Content-Type must be application/json." }, statusCode: 415);
            TenantPolicyPatch? patch;
            try { patch = await ctx.Request.ReadFromJsonAsync<TenantPolicyPatch>(ct); }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
            {
                log.LogWarning("Admin: refused tenant policy change {TenantId} from {RemoteIp}: unreadable body", id, remote);
                return Results.Json(new { error = "The body must be a JSON policy patch with known fields only." }, statusCode: 400);
            }

            var r = await svc.UpdateAsync(tenantId, patch, ct);
            var reason = TenantPolicyService.CleanReason(patch?.Reason);
            switch (r.Status)
            {
                case PolicyStatus.Ok:
                    // Audit record: policy fields and values only; the request never carries a secret.
                    log.LogInformation("Admin: operator from {RemoteIp} changed tenant policy {TenantId}: {Changes}. Reason: {Reason}",
                        remote, id, r.Changes is { Count: > 0 } c ? string.Join("; ", c) : "(no change)", reason);
                    return Results.Json(new { tenant = r.View, changes = r.Changes });
                case PolicyStatus.NotFound:
                    log.LogWarning("Admin: operator from {RemoteIp} tried to change unknown tenant {TenantId}", remote, id);
                    return Results.Json(new { error = r.Error }, statusCode: 404);
                default:
                    log.LogWarning("Admin: refused tenant policy change {TenantId} from {RemoteIp}: {Error}. Reason: {Reason}", id, remote, r.Error, reason);
                    return Results.Json(new { error = r.Error }, statusCode: 400);
            }
        });

        MapKb(app);
        return app;
    }

    /// <summary>The header that carries the audit reason for a knowledge-base change (the body is the page itself).</summary>
    public const string ReasonHeader = "X-Admin-Reason";

    private static void MapKb(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/onboarding/operator/kb").RequireRateLimiting(RateLimitPolicy);

        g.MapGet("/pages", async (HttpContext ctx, KbAdminService svc, ILogger<KbAdminService> log, CancellationToken ct) =>
        {
            if (OperatorAuth.Check(ctx) is { } denied) return denied;
            var (uploaded, served) = await svc.ListAsync(ct);
            log.LogInformation("Admin: operator from {RemoteIp} listed kb pages ({Uploaded} uploaded, {Served} served)", OperatorAuth.Remote(ctx), uploaded.Count, served.Count);
            return Results.Json(new { uploaded, served });
        });

        // The raw body is the page. It is read by hand after authentication, bounded by Kb:MaxPageBytes.
        g.MapPut("/pages/{pageId}", async (string pageId, HttpContext ctx, KbAdminService svc, ILogger<KbAdminService> log, CancellationToken ct) =>
        {
            if (OperatorAuth.Check(ctx) is { } denied) return denied;
            var id = OperatorAuth.Clean(pageId);
            var remote = OperatorAuth.Remote(ctx);
            var reason = TenantPolicyService.CleanReason(ctx.Request.Headers[ReasonHeader].ToString());
            if (reason is null)
                return Refuse(log, remote, id, null, 400, $"{ReasonHeader} is required: 1-200 characters, no control characters.");
            if (KbAdminService.AcceptedType(ctx.Request.ContentType) is null)
                return Refuse(log, remote, id, reason, 415, "Content-Type must be text/markdown or text/plain (UTF-8).");
            if (ctx.Request.ContentLength > svc.MaxPageBytes)
                return Refuse(log, remote, id, reason, 413, $"A page is at most {svc.MaxPageBytes} bytes.");

            byte[] body;
            try { body = await ReadBoundedAsync(ctx.Request.Body, svc.MaxPageBytes + 1, ct); }
            catch (BadHttpRequestException) { return Refuse(log, remote, id, reason, 413, $"A page is at most {svc.MaxPageBytes} bytes."); }

            var r = await svc.UploadAsync(pageId, ctx.Request.ContentType, body, ct);
            switch (r.Change)
            {
                case KbChange.Created or KbChange.Replaced or KbChange.Unchanged:
                    // Audit record, written once the change is committed: id, size and hash only, never the content.
                    log.LogInformation("Admin: operator from {RemoteIp} uploaded kb page {PageId} ({Change}, {Bytes} bytes, sha256 {Sha256}); {Reload}. Reason: {Reason}",
                        remote, id, r.Change.ToString().ToLowerInvariant(), r.Page!.SizeBytes, r.Page.Sha256, ReloadNote(r), reason);
                    return Results.Json(new { page = r.Page, change = r.Change.ToString().ToLowerInvariant(), served = r.Served, reloaded = r.ReloadError is null, note = Note(r) },
                        statusCode: r.Change == KbChange.Created ? 201 : 200);
                case KbChange.UnsupportedType: return Refuse(log, remote, id, reason, 415, r.Error!);
                case KbChange.TooLarge: return Refuse(log, remote, id, reason, 413, r.Error!);
                default: return Refuse(log, remote, id, reason, 400, r.Error!);
            }
        });

        g.MapDelete("/pages/{pageId}", async (string pageId, HttpContext ctx, KbAdminService svc, ILogger<KbAdminService> log, CancellationToken ct) =>
        {
            if (OperatorAuth.Check(ctx) is { } denied) return denied;
            var id = OperatorAuth.Clean(pageId);
            var remote = OperatorAuth.Remote(ctx);
            var reason = TenantPolicyService.CleanReason(ctx.Request.Headers[ReasonHeader].ToString());
            if (reason is null)
                return Refuse(log, remote, id, null, 400, $"{ReasonHeader} is required: 1-200 characters, no control characters.");
            var r = await svc.DeleteAsync(pageId, ct);
            if (r.Change == KbChange.NotFound)
            {
                log.LogWarning("Admin: operator from {RemoteIp} tried to delete unknown kb page {PageId}", remote, id);
                return Results.Json(new { error = r.Error }, statusCode: 404);
            }
            log.LogInformation("Admin: operator from {RemoteIp} deleted kb page {PageId}; {Reload}. Reason: {Reason}", remote, id, ReloadNote(r), reason);
            return Results.Json(new { deleted = pageId, served = r.Served, reloaded = r.ReloadError is null, note = Note(r) });
        });

        // Re-reads the seed directory (Kb:Path) and the uploaded pages on this replica.
        g.MapPost("/reload", async (HttpContext ctx, KbLibrary library, ILogger<KbAdminService> log, CancellationToken ct) =>
        {
            if (OperatorAuth.Check(ctx) is { } denied) return denied;
            var r = await library.ReloadAsync(reseed: true, ct);
            if (!r.Ok)
            {
                // Partial: the new seed is served with the last uploaded pages read; the database could not be read.
                log.LogWarning("Admin: operator from {RemoteIp} reloaded the knowledge base partially: seed reloaded, uploaded pages unavailable ({Error}); serving {Served} pages",
                    OperatorAuth.Remote(ctx), r.Error, r.Served);
                return Results.Json(new { served = r.Served, partial = true, error = "Seed pages reloaded; the uploaded pages could not be read, so the last ones read are served. " + r.Error }, statusCode: 503);
            }
            log.LogInformation("Admin: operator from {RemoteIp} reloaded the knowledge base; serving {Served} pages", OperatorAuth.Remote(ctx), r.Served);
            return Results.Json(new { served = r.Served, partial = false });
        });
    }

    private static string ReloadNote(KbWriteResult r) => r.Change == KbChange.Unchanged
        ? $"no reload needed, serving {r.Served} pages"
        : r.ReloadError is null ? $"serving {r.Served} pages" : $"stored, this replica could not reload ({r.ReloadError}) and picks it up on its next refresh";

    private static string? Note(KbWriteResult r) =>
        r.ReloadError is null ? null : "Stored; this replica will pick it up on the next refresh.";

    private static IResult Refuse(ILogger log, string remote, string id, string? reason, int status, string error)
    {
        log.LogWarning("Admin: refused kb page change {PageId} from {RemoteIp}: {Error}. Reason: {Reason}", id, remote, error, reason);
        return Results.Json(new { error }, statusCode: status);
    }

    /// <summary>Reads at most <paramref name="limit"/> bytes; a longer body is cut there, so the caller sees it as too large.</summary>
    private static async Task<byte[]> ReadBoundedAsync(Stream body, int limit, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while (ms.Length < limit && (n = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - ms.Length)), ct)) > 0)
            ms.Write(buffer, 0, n);
        return ms.ToArray();
    }
}
