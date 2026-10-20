using System.Security.Cryptography;
using System.Text;
using Biatec.Chat.Api.Onboarding;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Admin;

/// <summary>
/// Operator admin API for tenant policy (docs/admin-api.md). It lives under <c>/onboarding/operator/</c> on purpose:
/// that prefix already is the operator surface, so it inherits the bearer-only posture of the operator approval route
/// (no cookie, so exempt from the CSRF guard; never a CORS wildcard; Authorization is not an allowed CORS header, so a
/// browser page cannot send the bearer cross-origin), plus the onboarding rate limit and body size limit.
/// Authentication is the same operator bearer key (<c>Onboarding:OperatorKey</c>, injected from a Kubernetes secret
/// or AWS Secrets Manager as an environment variable, never in a config file); the API is disabled (404) while unset.
/// </summary>
public static class AdminEndpoints
{
    public static IServiceCollection AddAdmin(this IServiceCollection services)
    {
        services.AddScoped<TenantPolicyService>();
        return services;
    }

    public static IEndpointRouteBuilder MapAdmin(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/onboarding/operator/tenants")
            .RequireRateLimiting(OnboardingEndpoints.RateLimitPolicy)
            .AddEndpointFilter(RequireOperator);

        g.MapGet("/", async (HttpContext ctx, TenantPolicyService svc, ILogger<TenantPolicyService> log, CancellationToken ct) =>
        {
            var list = await svc.ListAsync(ct);
            log.LogInformation("Admin: operator from {RemoteIp} listed {Count} tenant policies", Remote(ctx), list.Count);
            return Results.Json(new { tenants = list });
        });

        g.MapGet("/{tenantId}", async (string tenantId, HttpContext ctx, TenantPolicyService svc, ILogger<TenantPolicyService> log, CancellationToken ct) =>
        {
            var view = await svc.GetAsync(tenantId, ct);
            log.LogInformation("Admin: operator from {RemoteIp} read tenant policy {TenantId} (found: {Found})", Remote(ctx), Clean(tenantId), view is not null);
            return view is null ? Results.Json(new { error = "Unknown tenant." }, statusCode: 404) : Results.Json(view);
        });

        g.MapMethods("/{tenantId}/policy", [HttpMethods.Patch], async (string tenantId, TenantPolicyPatch? patch, HttpContext ctx, TenantPolicyService svc, ILogger<TenantPolicyService> log, CancellationToken ct) =>
        {
            var r = await svc.UpdateAsync(tenantId, patch, ct);
            var reason = TenantPolicyService.CleanReason(patch?.Reason);
            switch (r.Status)
            {
                case PolicyStatus.Ok:
                    // Audit record: policy fields and values only; the request never carries a secret.
                    log.LogInformation("Admin: operator from {RemoteIp} changed tenant policy {TenantId}: {Changes}. Reason: {Reason}",
                        Remote(ctx), tenantId, r.Changes is { Count: > 0 } c ? string.Join("; ", c) : "(no change)", reason);
                    return Results.Json(new { tenant = r.View, changes = r.Changes });
                case PolicyStatus.NotFound:
                    log.LogWarning("Admin: operator from {RemoteIp} tried to change unknown tenant {TenantId}", Remote(ctx), Clean(tenantId));
                    return Results.Json(new { error = r.Error }, statusCode: 404);
                default:
                    log.LogWarning("Admin: refused tenant policy change {TenantId} from {RemoteIp}: {Error}. Reason: {Reason}", Clean(tenantId), Remote(ctx), r.Error, reason);
                    return Results.Json(new { error = r.Error }, statusCode: 400);
            }
        });

        return app;
    }

    private static async ValueTask<object?> RequireOperator(EndpointFilterInvocationContext fc, EndpointFilterDelegate next)
    {
        var ctx = fc.HttpContext;
        var key = ctx.RequestServices.GetRequiredService<IOptions<OnboardingOptions>>().Value.OperatorKey;
        if (key is not { Length: >= OnboardingOptions.MinKeyLength }) return Results.NotFound();
        var values = ctx.Request.Headers.Authorization;
        var auth = values.Count == 1 ? values[0] ?? "" : "";
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || !FixedEquals(auth["Bearer ".Length..].Trim(), key))
        {
            ctx.RequestServices.GetRequiredService<ILogger<TenantPolicyService>>()
                .LogWarning("Admin: rejected unauthenticated {Method} {Path} from {RemoteIp}", ctx.Request.Method, Clean(ctx.Request.Path.Value), Remote(ctx));
            return Results.Json(new { error = "Operator authorization required." }, statusCode: 401);
        }
        return await next(fc);
    }

    private static string Remote(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Caller-supplied text for a log line: control characters removed and length bounded (no log forging).</summary>
    private static string Clean(string? s)
    {
        var t = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray());
        return t.Length <= 128 ? t : t[..128] + "...";
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
