using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Onboarding;

/// <summary>
/// The operator bearer check shared by the operator onboarding route and the admin API. The key is
/// <c>Onboarding:OperatorKey</c> (injected from a secret, never in a config file). Call it before the request body is
/// read, so an unauthenticated caller never reaches parsing.
/// </summary>
public static class OperatorAuth
{
    public const string LogCategory = "Biatec.Chat.Api.Operator";

    /// <summary>Null when the request carries the operator key; otherwise the result to return (404 while no key is configured, else 401).</summary>
    public static IResult? Check(HttpContext ctx)
    {
        var key = ctx.RequestServices.GetRequiredService<IOptions<OnboardingOptions>>().Value.OperatorKey;
        if (key is not { Length: >= OnboardingOptions.MinKeyLength }) return Results.NotFound();
        var values = ctx.Request.Headers.Authorization;
        var auth = values.Count == 1 ? values[0] ?? "" : "";
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && FixedEquals(auth["Bearer ".Length..].Trim(), key)) return null;

        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory)
            .LogWarning("Operator: rejected unauthenticated {Method} {Path} from {RemoteIp}", ctx.Request.Method, Clean(ctx.Request.Path.Value), Remote(ctx));
        return Results.Json(new { error = "Operator authorization required." }, statusCode: 401);
    }

    public static string Remote(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Caller-supplied text for a log line: control characters removed and length bounded (no log forging).</summary>
    public static string Clean(string? s)
    {
        var t = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray());
        return t.Length <= 128 ? t : t[..128] + "...";
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
