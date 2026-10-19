using System.Security.Cryptography;
using System.Text;
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
        return services;
    }

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
        var g = app.MapGroup("/onboarding");

        g.MapPost("/challenges", async (OnboardingRequest? req, OnboardingService svc, CancellationToken ct) =>
            ToResult(await svc.GetChallengesAsync(req, ct)));

        g.MapPost("/tenants", async (OnboardingRequest? req, OnboardingService svc, CancellationToken ct) =>
            ToResult(await svc.OnboardAsync(req, ct)));

        g.MapPost("/operator/tenants", async (HttpContext ctx, OperatorApprovalRequest? req, OnboardingService svc, IOptions<OnboardingOptions> opts, CancellationToken ct) =>
        {
            var key = opts.Value.OperatorKey;
            if (key is not { Length: >= OnboardingOptions.MinKeyLength }) return Results.NotFound();
            var authValues = ctx.Request.Headers.Authorization;
            var auth = authValues.Count == 1 ? authValues[0] ?? "" : "";
            if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || !FixedEquals(auth["Bearer ".Length..].Trim(), key))
                return Results.Json(new { error = "Operator authorization required." }, statusCode: 401);
            return ToResult(await svc.ApproveAsync(req, ct));
        });

        return app;
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
