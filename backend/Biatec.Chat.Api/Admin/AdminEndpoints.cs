using System.Text.Json;
using System.Threading.RateLimiting;
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
/// Operator admin API for tenant policy (docs/admin-api.md). It lives under <c>/onboarding/operator/</c> on purpose:
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

        return app;
    }
}
