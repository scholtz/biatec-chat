using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Biatec.Chat.Core.Canonical;

namespace Biatec.Chat.Tools.Limits;

/// <summary>One spending limit as Biatec OIDC reports it. <see cref="Key"/> is OIDC's own name (for example <c>per_day_eur</c>).</summary>
/// <param name="Amount">Decimal string exactly as reported.</param>
public sealed record SpendingLimit(string Key, string Period, string Amount, string Currency)
{
    public string Display => $"{Amount} {Currency}";
}

public enum LimitsFailure { Unavailable, Unauthorized, Forbidden, StepUp, Rejected }

public sealed record LimitsReadResult(IReadOnlyList<SpendingLimit>? Limits, LimitsFailure? Failure = null, string? Error = null)
{
    public bool Ok => Limits is not null;
}

public sealed record LimitsWriteResult(bool Applied, LimitsFailure? Failure = null, string? Error = null);

/// <summary>Biatec OIDC <c>GET /wallet/limits</c> and <c>PUT /wallet/limits</c>, straight from the backend with the user's vaulted bearer.</summary>
public interface IWalletLimitsClient
{
    Task<LimitsReadResult> GetAsync(string bearer, CancellationToken ct = default);

    /// <summary>Sends the full limits body. The caller has already checked that it lowers and never raises.</summary>
    Task<LimitsWriteResult> PutAsync(string bearer, IReadOnlyDictionary<string, string> body, CancellationToken ct = default);
}

public sealed class WalletLimitsClient(HttpClient http) : IWalletLimitsClient
{
    public const string StepUpError = "insufficient_user_authentication";

    public async Task<LimitsReadResult> GetAsync(string bearer, CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, "wallet/limits");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var (res, text) = await SendAsync(msg, ct);
        if (res is null) return new LimitsReadResult(null, LimitsFailure.Unavailable, "unreachable");
        using (res)
        {
            if (res.IsSuccessStatusCode)
                return LimitsPolicy.ParseLimits(text) is { } l ? new LimitsReadResult(l) : new LimitsReadResult(null, LimitsFailure.Rejected, "invalid_response");
            var f = Classify(res.StatusCode, text, out var error);
            return new LimitsReadResult(null, f, error);
        }
    }

    public async Task<LimitsWriteResult> PutAsync(string bearer, IReadOnlyDictionary<string, string> body, CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Put, "wallet/limits") { Content = JsonContent.Create(body) };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var (res, text) = await SendAsync(msg, ct);
        if (res is null) return new LimitsWriteResult(false, LimitsFailure.Unavailable, "unreachable");
        using (res)
        {
            if (res.IsSuccessStatusCode) return new LimitsWriteResult(true);
            var f = Classify(res.StatusCode, text, out var error);
            return new LimitsWriteResult(false, f, error);
        }
    }

    private async Task<(HttpResponseMessage? Res, string Text)> SendAsync(HttpRequestMessage msg, CancellationToken ct)
    {
        try
        {
            var res = await http.SendAsync(msg, ct);
            return (res, await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or IOException) && !ct.IsCancellationRequested)
        {
            return (null, "");
        }
    }

    private static LimitsFailure Classify(HttpStatusCode status, string text, out string error)
    {
        error = ReadError(text) ?? "rejected";
        var code = (int)status;
        if (code >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests) { error = "http_" + code; return LimitsFailure.Unavailable; }
        if (status == HttpStatusCode.Unauthorized) return error == StepUpError ? LimitsFailure.StepUp : LimitsFailure.Unauthorized;
        if (status == HttpStatusCode.Forbidden) return LimitsFailure.Forbidden;
        return LimitsFailure.Rejected;
    }

    private static string? ReadError(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>A lowering-only change: the full body to PUT, and what changes in it.</summary>
public sealed record LimitsPlan(IReadOnlyDictionary<string, string> Body, IReadOnlyList<LimitChange> Changes, string RequestHash, string CanonicalBody);

public sealed record LimitChange(string Period, string Key, string From, string To, string Currency);

public sealed record LimitsRefusal(string Code, string Message);

/// <summary>
/// The lowering-only rule. Biatec OIDC does not offer the consent-gated raise yet, so the chat backend refuses every raise itself,
/// on the server, whatever the model asked for (docs/required-changes-biatec-oidc.md, fallback of item 19).
/// </summary>
public static class LimitsPolicy
{
    public static readonly string[] Periods = ["daily", "weekly", "monthly"];

    /// <summary>The period a limit key covers, or the key itself when it is not a time-based limit (for example <c>per_transaction_eur</c>).</summary>
    public static string PeriodOf(string key)
    {
        var k = key.ToLowerInvariant();
        if (k.Contains("day") || k.Contains("daily")) return "daily";
        if (k.Contains("week")) return "weekly";
        if (k.Contains("month")) return "monthly";
        return key;
    }

    private static string CurrencyOf(string key, string? fallback)
    {
        var i = key.LastIndexOf('_');
        if (i >= 0 && key.Length - i - 1 is 3) return key[(i + 1)..].ToUpperInvariant();
        return fallback ?? "EUR";
    }

    /// <summary>
    /// Reads <c>{"limits":{"per_day_eur":"500"}}</c> or the same map at the top level. Null limit values (no cap) are left out.
    /// A limit without a currency in its name or in a top-level <c>currency</c> is EUR, the unit Biatec OIDC prices limits in.
    /// </summary>
    public static IReadOnlyList<SpendingLimit>? ParseLimits(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? currency = root.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()?.ToUpperInvariant() : null;
            var map = root.TryGetProperty("limits", out var l) && l.ValueKind == JsonValueKind.Object ? l : root;
            var list = new List<SpendingLimit>();
            foreach (var p in map.EnumerateObject())
            {
                if (p.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) continue;
                var amount = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
                if (!TryAmount(amount, out _)) continue;
                list.Add(new SpendingLimit(p.Name, PeriodOf(p.Name), amount, CurrencyOf(p.Name, currency)));
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    public static bool TryAmount(string? s, out decimal value) =>
        decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && value >= 0;

    /// <summary>
    /// Builds the body for the requested new values (period to amount). Refuses unknown limits, anything not a plain
    /// non-negative amount, a request that changes nothing, and any value above the current one. The body carries every
    /// current limit, the lowered ones replaced.
    /// </summary>
    public static (LimitsPlan? Plan, LimitsRefusal? Refusal) Plan(IReadOnlyList<SpendingLimit> current, IReadOnlyDictionary<string, string> requested)
    {
        if (requested.Count == 0) return (null, new LimitsRefusal("no_change", "No new limit was given."));
        var changes = new List<LimitChange>();
        foreach (var (period, to) in requested.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var cur = current.FirstOrDefault(c => c.Period == period);
            if (cur is null)
                return (null, new LimitsRefusal("unknown_limit", $"There is no {period} spending limit on this account to lower."));
            if (!TryAmount(to, out var newValue) || !TryAmount(cur.Amount, out var curValue))
                return (null, new LimitsRefusal("invalid_amount", $"'{to}' is not a valid amount for the {period} limit."));
            if (newValue > curValue)
                return (null, new LimitsRefusal("raise_refused",
                    $"Raising the {period} limit from {cur.Display} to {to} {cur.Currency} is not possible from chat. Chat can only lower limits; raising one must be done on your enrolled device."));
            if (newValue == curValue) continue; // already at this value
            changes.Add(new LimitChange(period, cur.Key, cur.Amount, to, cur.Currency));
        }
        if (changes.Count == 0) return (null, new LimitsRefusal("no_change", "Those limits are already at the requested values. Nothing to change."));

        var body = current.ToDictionary(c => c.Key, c => c.Amount, StringComparer.Ordinal);
        foreach (var ch in changes) body[ch.Key] = ch.To;
        var (canonical, hash) = Canonicalize(body);
        return (new LimitsPlan(body, changes, hash, canonical), null);
    }

    /// <summary>Re-checks a stored body against the limits as they are now: it must name only existing limits and raise none of them.</summary>
    public static LimitsRefusal? CheckLowering(IReadOnlyList<SpendingLimit> current, IReadOnlyDictionary<string, string> body)
    {
        if (body.Count == 0) return new LimitsRefusal("invalid_request", "The stored limits change is empty.");
        foreach (var (key, to) in body)
        {
            var cur = current.FirstOrDefault(c => c.Key == key);
            if (cur is null) return new LimitsRefusal("unknown_limit", $"The limit '{key}' no longer exists.");
            if (!TryAmount(to, out var n) || !TryAmount(cur.Amount, out var c)) return new LimitsRefusal("invalid_amount", "A limit amount is not valid.");
            if (n > c) return new LimitsRefusal("raise_refused", $"Your {cur.Period} limit is now {cur.Display}, below the amount in this preview. Chat never raises a limit; ask again to get a new preview.");
        }
        return null;
    }

    /// <summary>RFC 8785 canonical body and its lowercase hex SHA-256: the <c>request_hash</c> of the <c>biatec_limits_change</c> authorization detail.</summary>
    public static (string Canonical, string Hash) Canonicalize(IReadOnlyDictionary<string, string> body)
    {
        var node = new JsonObject();
        foreach (var (k, v) in body) node[k] = v;
        return (JsonCanonicalizer.Canonicalize(node), Hashing.CanonicalSha256Hex(node));
    }
}
