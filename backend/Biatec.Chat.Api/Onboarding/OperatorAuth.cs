using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Biatec.Chat.Api.Admin;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Onboarding;

/// <summary>One named operator key (<c>Admin:Operators:&lt;name&gt;:KeyHash</c>): the lowercase or uppercase hex SHA-256 of the key.</summary>
public sealed class OperatorKeyOptions
{
    /// <summary>Hex SHA-256 (64 characters) of the operator's bearer key. Only the hash is configured; the key itself lives with the operator.</summary>
    public string? KeyHash { get; set; }
}

/// <summary>Who made an admin request. <see cref="Legacy"/>: the shared, deprecated <c>Onboarding:OperatorKey</c>.</summary>
public sealed record OperatorIdentity(string Name, bool Legacy);

/// <summary>
/// The operator bearer check shared by the operator onboarding route and the admin API. Each operator has a named key
/// (<c>Admin:Operators:&lt;name&gt;:KeyHash</c>, only its SHA-256 is configured), so every admin request is attributed
/// to a named operator. The single shared <c>Onboarding:OperatorKey</c> (injected from a secret, never in a config file)
/// is still accepted as the deprecated operator <see cref="LegacyName"/>. Call it before the request body is read, so
/// an unauthenticated caller never reaches parsing.
/// </summary>
public static partial class OperatorAuth
{
    public const string LogCategory = "Biatec.Chat.Api.Operator";

    /// <summary>The operator name recorded for the shared <c>Onboarding:OperatorKey</c>. Reserved: no named operator may use it.</summary>
    public const string LegacyName = "legacy";

    private const string ItemKey = "Biatec.Chat.Operator";
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ILoggerFactory, object> LegacyWarned = new();

    // The name is a configuration key (case-insensitive) and is written to logs and evidence.
    [GeneratedRegex(@"\A[A-Za-z0-9](?:[A-Za-z0-9._-]{0,62}[A-Za-z0-9])?\z")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"\A[0-9A-Fa-f]{64}\z")]
    private static partial Regex HashRegex();

    public static bool ValidName(string name) => NameRegex().IsMatch(name) && !string.Equals(name, LegacyName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Configuration errors in <c>Admin:Operators</c> (checked at start), or an empty list.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyDictionary<string, OperatorKeyOptions> operators)
    {
        var errors = new List<string>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, o) in operators)
        {
            if (!ValidName(name))
                errors.Add($"Admin:Operators:{Clean(name)}: the name must be 1-64 letters, digits, '.', '_' or '-' and not '{LegacyName}'.");
            if (o.KeyHash is not { } h || !HashRegex().IsMatch(h.Trim()))
                errors.Add($"Admin:Operators:{Clean(name)}:KeyHash must be the 64-character hex SHA-256 of the operator key.");
            else if (seen.TryGetValue(h.Trim(), out var other))
                errors.Add($"Admin:Operators:{Clean(name)} has the same key as {other}: every operator needs an own key.");
            else seen[h.Trim()] = Clean(name);
        }
        return errors;
    }

    /// <summary>
    /// Null when the request carries a valid operator key (the operator is then <see cref="Current"/>); otherwise the
    /// result to return (404 while no operator key is configured, else 401).
    /// </summary>
    public static IResult? Check(HttpContext ctx)
    {
        var legacyKey = ctx.RequestServices.GetRequiredService<IOptions<OnboardingOptions>>().Value.OperatorKey;
        var operators = ctx.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value.Operators;
        var legacyOn = legacyKey is { Length: >= OnboardingOptions.MinKeyLength };
        if (!legacyOn && operators.Count == 0) return Results.NotFound();

        var values = ctx.Request.Headers.Authorization;
        var auth = values.Count == 1 ? values[0] ?? "" : "";
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && Match(auth["Bearer ".Length..].Trim(), operators, legacyOn ? legacyKey : null) is { } who)
        {
            ctx.Items[ItemKey] = who;
            // Warned once per host (logger factory), so the deprecation is visible without a line per request.
            var loggers = ctx.RequestServices.GetRequiredService<ILoggerFactory>();
            if (who.Legacy && LegacyWarned.TryAdd(loggers, new object()))
                loggers.CreateLogger(LogCategory)
                    .LogWarning("Operator: the shared Onboarding:OperatorKey was used; it is deprecated and recorded as operator '{Operator}'. Give each operator a named key under Admin:Operators.", LegacyName);
            return null;
        }

        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory)
            .LogWarning("Operator: rejected unauthenticated {Method} {Path} from {RemoteIp}", ctx.Request.Method, Clean(ctx.Request.Path.Value), Remote(ctx));
        return Results.Json(new { error = "Operator authorization required." }, statusCode: 401);
    }

    /// <summary>The operator <see cref="Check"/> authenticated on this request.</summary>
    public static OperatorIdentity Current(HttpContext ctx) =>
        ctx.Items[ItemKey] as OperatorIdentity ?? throw new InvalidOperationException("OperatorAuth.Check must pass before the operator is read.");

    /// <summary>
    /// Compares the presented key's SHA-256 with every configured hash in constant time (no early exit, so timing does
    /// not tell which operator matched). A named key wins over the legacy one. Invalid entries never match.
    /// </summary>
    private static OperatorIdentity? Match(string presented, IReadOnlyDictionary<string, OperatorKeyOptions> operators, string? legacyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        OperatorIdentity? found = null;
        foreach (var (name, o) in operators)
        {
            if (!ValidName(name) || o.KeyHash is not { } h || !HashRegex().IsMatch(h.Trim())) continue;
            if (CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(h.Trim())))
                found ??= new OperatorIdentity(name, Legacy: false);
        }
        if (legacyKey is not null && CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(Encoding.UTF8.GetBytes(legacyKey))))
            found ??= new OperatorIdentity(LegacyName, Legacy: true);
        return found;
    }

    public static string Remote(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Caller-supplied text for a log line: control characters removed and length bounded (no log forging).</summary>
    public static string Clean(string? s)
    {
        var t = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray());
        return t.Length <= 128 ? t : t[..128] + "...";
    }
}
