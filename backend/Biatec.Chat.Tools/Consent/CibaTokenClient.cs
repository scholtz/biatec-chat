using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Biatec.Chat.Tools.Consent;

/// <summary>Result of one poll of the CIBA token endpoint.</summary>
public abstract record CibaPollOutcome
{
    /// <summary>The user has not decided yet (<c>authorization_pending</c>), or the server asked to slow down.</summary>
    public sealed record Pending(bool SlowDown = false) : CibaPollOutcome;

    /// <summary>Approved. <paramref name="Receipt"/> is the <c>consent_receipt</c> exactly as received (raw JSON text).</summary>
    public sealed record Approved(string AccessToken, int ExpiresInSeconds, string? Receipt) : CibaPollOutcome
    {
        public override string ToString() => "Approved { [redacted] }";
    }

    /// <summary>The user denied, or entered the binding code wrongly three times (<paramref name="Reason"/> = <c>code_failed</c>).</summary>
    public sealed record Denied(string? Reason) : CibaPollOutcome;

    /// <summary>The request expired (<c>expired_token</c>).</summary>
    public sealed record Expired : CibaPollOutcome;

    /// <summary>The request is unknown or was already redeemed (<c>invalid_grant</c>); another poller may have taken it.</summary>
    public sealed record Gone : CibaPollOutcome;

    /// <summary>Biatec OIDC could not be reached or answered with a server failure. Try again on the next tick.</summary>
    public sealed record Unavailable(string Reason) : CibaPollOutcome;
}

public interface ICibaTokenClient
{
    /// <summary>One poll of the token endpoint with the CIBA grant, as the confidential client that owns the request.</summary>
    Task<CibaPollOutcome> PollAsync(Uri tokenEndpoint, string clientId, string clientSecret, string resource, string authReqId, CancellationToken ct = default);
}

/// <summary>
/// <c>POST /token</c> with <c>grant_type=urn:openid:params:grant-type:ciba</c>. The body is parsed, never logged: on
/// success it holds an access token and the signed receipt.
/// </summary>
public sealed class CibaTokenClient(HttpClient http) : ICibaTokenClient
{
    public const string GrantType = "urn:openid:params:grant-type:ciba";

    public async Task<CibaPollOutcome> PollAsync(Uri tokenEndpoint, string clientId, string clientSecret, string resource, string authReqId, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = GrantType, ["auth_req_id"] = authReqId, ["resource"] = resource,
            ["client_id"] = clientId, ["client_secret"] = clientSecret,
        };
        string text;
        int code;
        try
        {
            using var res = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), ct);
            code = (int)res.StatusCode;
            text = await res.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or IOException) && !ct.IsCancellationRequested)
        {
            return new CibaPollOutcome.Unavailable(ex.GetType().Name);
        }

        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(text); } catch (JsonException) { }
        using (doc)
        {
            var root = doc?.RootElement;
            if (code is >= 200 and < 300)
            {
                if (root is not { ValueKind: JsonValueKind.Object } r
                    || !r.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(at.GetString()))
                    return new CibaPollOutcome.Unavailable("invalid_response");
                var expires = r.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var n) && n > 0 ? n : 300;
                var receipt = r.TryGetProperty("consent_receipt", out var cr) && cr.ValueKind == JsonValueKind.Object ? cr.GetRawText() : null;
                return new CibaPollOutcome.Approved(at.GetString()!, expires, receipt);
            }

            string? error = null, reason = null;
            if (root is { ValueKind: JsonValueKind.Object } e)
            {
                error = e.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString() : null;
                reason = e.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString() : null;
            }
            return error switch
            {
                "authorization_pending" => new CibaPollOutcome.Pending(),
                "slow_down" => new CibaPollOutcome.Pending(SlowDown: true),
                "access_denied" => new CibaPollOutcome.Denied(reason),
                "expired_token" => new CibaPollOutcome.Expired(),
                "invalid_grant" => new CibaPollOutcome.Gone(),
                _ when code >= 500 || code is 408 or 429 => new CibaPollOutcome.Unavailable("http_" + code),
                _ => new CibaPollOutcome.Unavailable("rejected_" + (error ?? code.ToString())),
            };
        }
    }
}

/// <summary>What the backend knows about whether the phone will be asked, from <c>GET /consent/settings</c>.</summary>
public sealed record ConsentSettings(decimal? ThresholdEur, bool DeviceEnrolled, bool DeviceActive, bool AlwaysAsk, bool CoolingOff, bool FirstUse);

/// <summary>T-061: the settings, or null, and whether Biatec OIDC answered that the endpoint does not exist (404 / 501).</summary>
public sealed record ConsentSettingsFetch(ConsentSettings? Settings, bool EndpointMissing);

public interface IConsentSettingsClient
{
    /// <summary>Null when the endpoint is missing, fails or answers something unparseable: the caller uses the fallback text.</summary>
    Task<ConsentSettings?> GetAsync(Uri settingsEndpoint, string bearer, CancellationToken ct = default);

    /// <summary>T-061: as <see cref="GetAsync"/>, also telling a missing endpoint from a failing one.</summary>
    async Task<ConsentSettingsFetch> FetchAsync(Uri settingsEndpoint, string bearer, CancellationToken ct = default) =>
        new(await GetAsync(settingsEndpoint, bearer, ct), false);
}

public sealed class ConsentSettingsClient(HttpClient http) : IConsentSettingsClient
{
    public async Task<ConsentSettings?> GetAsync(Uri settingsEndpoint, string bearer, CancellationToken ct = default) =>
        (await FetchAsync(settingsEndpoint, bearer, ct)).Settings;

    public async Task<ConsentSettingsFetch> FetchAsync(Uri settingsEndpoint, string bearer, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, settingsEndpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var res = await http.SendAsync(req, ct);
            if (res.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NotImplemented) return new(null, true);
            if (!res.IsSuccessStatusCode) return new(null, false);
            return new(await ParseAsync(res, ct), false);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or IOException or JsonException) && !ct.IsCancellationRequested)
        {
            return new(null, false);
        }
    }

    private static async Task<ConsentSettings?> ParseAsync(HttpResponseMessage res, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        decimal? threshold = null;
        if (r.TryGetProperty("threshold_eur", out var t) && t.ValueKind == JsonValueKind.String
            && decimal.TryParse(t.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) threshold = d;
        if (!r.TryGetProperty("device_enrolled", out var enrolled) || enrolled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
        r.TryGetProperty("client", out var client);
        return new ConsentSettings(threshold, enrolled.GetBoolean(), Flag(r, "device_active"),
            Flag(client, "always_ask"), IsSet(client, "cooling_off_until"), Flag(client, "first_use"));
    }

    private static bool Flag(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static bool IsSet(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString());
}

/// <summary>
/// The line the preview card shows about consent. It never predicts less consent than Biatec OIDC would ask for:
/// without usable settings it says approval may be needed.
/// </summary>
public static class ConsentPrediction
{
    public const string FallbackText = "This may need approval on your phone.";
    public const string AskedText = "You will be asked to approve this on your phone.";
    public const string NoDeviceText = "Enroll the Biatec authenticator on your phone to approve this action.";
    public const string NotExpectedText = "No phone approval expected for this amount, though it may still be asked.";

    public static string Describe(ConsentSettings? s, decimal? valueEur)
    {
        if (s is null) return FallbackText;
        if (s.AlwaysAsk || s.CoolingOff || s.FirstUse) return s.DeviceActive ? AskedText : NoDeviceText;
        if (s.ThresholdEur is { } threshold && valueEur is { } v)
            return v > threshold ? (s.DeviceActive ? AskedText : NoDeviceText) : NotExpectedText;
        return FallbackText;
    }
}
