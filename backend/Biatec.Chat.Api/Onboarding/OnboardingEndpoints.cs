using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Onboarding;

public static class OnboardingEndpoints
{
    public static IServiceCollection AddOnboarding(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<OnboardingOptions>(config.GetSection(OnboardingOptions.Section));
        services.TryAddSingleton<IDnsTxtResolver, DnsClientTxtResolver>();
        services.TryAddSingleton<IOidcClientRegistrar, NoDynamicRegistration>();
        services.TryAddSingleton<ISecretStore, UnconfiguredSecretStore>();
        services.TryAddScoped<OnboardingService>();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Per remote IP; behind a proxy this needs forwarded-headers handling so the client IP, not the proxy, is seen.
            o.AddPolicy(RateLimitPolicy, ctx =>
            {
                var opts = ctx.RequestServices.GetRequiredService<IOptions<OnboardingOptions>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, opts.RateLimitPermits),
                    Window = TimeSpan.FromSeconds(Math.Max(1, opts.RateLimitWindowSeconds)),
                    QueueLimit = 0,
                });
            });
        });
        return services;
    }

    public const string RateLimitPolicy = "onboarding";

    /// <summary>Rejects an oversized /onboarding request body (413) before it is parsed. Add before the endpoints.</summary>
    public static IApplicationBuilder UseOnboardingBodyLimit(this IApplicationBuilder app) =>
        app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/onboarding"), branch => branch.Use(async (ctx, next) =>
        {
            var max = ctx.RequestServices.GetRequiredService<IOptions<OnboardingOptions>>().Value.MaxBodyBytes;
            if (ctx.Request.ContentLength > max)
            {
                ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
            var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = max; // chunked bodies without a length
            await next(ctx);
        }));

    private static IResult ToResult(OnboardingResult r) => r.Status switch
    {
        OnboardingStatus.Ok => Results.Json(r.Body),
        OnboardingStatus.Created => Results.Json(r.Body, statusCode: 201),
        OnboardingStatus.PendingOperator => Results.Json(r.Body, statusCode: 202),
        OnboardingStatus.Invalid => Results.Json(new { error = r.Error }, statusCode: 400),
        OnboardingStatus.HostTaken => Results.Json(new { error = r.Error }, statusCode: 409),
        OnboardingStatus.Unverified => Results.Json(new { error = r.Error, details = r.Body }, statusCode: 422),
        OnboardingStatus.Disabled => Results.Json(new { error = r.Error }, statusCode: 503),
        _ => Results.Json(new { error = r.Error }, statusCode: 502),
    };

    /// <summary>
    /// Cookie-less endpoints: no ambient credential exists to forge, so they sit outside the CSRF and origin guards
    /// (see OriginPolicy.IsPublic). The operator endpoint needs a bearer key.
    /// </summary>
    public static IEndpointRouteBuilder MapOnboarding(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/onboarding").RequireRateLimiting(RateLimitPolicy);

        g.MapPost("/challenges", async (OnboardingRequest? req, OnboardingService svc, CancellationToken ct) =>
            ToResult(await svc.GetChallengesAsync(req, ct)));

        g.MapPost("/tenants", async (OnboardingRequest? req, OnboardingService svc, CancellationToken ct) =>
            ToResult(await svc.OnboardAsync(req, ct)));

        g.MapPost("/operator/tenants", async (HttpContext ctx, OperatorApprovalRequest? req, OnboardingService svc, CancellationToken ct) =>
            OperatorAuth.Check(ctx) ?? ToResult(await svc.ApproveAsync(req, ct)));

        return app;
    }
}
