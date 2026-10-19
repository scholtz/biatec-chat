using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Biatec.Chat.Tools.Signing;

/// <summary>One signing call: a single sender, the exact previewed group, and the group ID the preview was bound to.</summary>
/// <param name="Network">Network name as stored on the step (for example <c>algorand-testnet</c>).</param>
/// <param name="Address">The one sender address of the whole group.</param>
/// <param name="Transactions">Unsigned transactions, base64 msgpack, in group order: the previewed bytes, unchanged.</param>
/// <param name="GroupId">The protocol group ID (32 bytes) the preview carries.</param>
/// <param name="RequireConsent">More consent only; the backend can never skip a consent Biatec OIDC asks for.</param>
/// <param name="MaxConsentSeconds">Upper bound for the consent window (the group's remaining validity).</param>
/// <param name="Origin">The tenant origin the request is made on behalf of.</param>
public sealed record WalletSignRequest(string Network, string Address, IReadOnlyList<string> Transactions, byte[] GroupId,
    bool RequireConsent = false, int? MaxConsentSeconds = null, string? Origin = null);

/// <summary>Result of <c>POST /wallet/{network}/{address}/sign</c>.</summary>
public abstract record WalletSignOutcome
{
    /// <summary>Signed transactions (base64 msgpack, group order) and the value Biatec OIDC decoded, when it reports one.</summary>
    public sealed record Signed(IReadOnlyList<string> SignedTransactions, string? ValuedEur) : WalletSignOutcome;

    /// <summary>401 insufficient_user_authentication: consent is needed. Completion is the consent task's (T-049).</summary>
    public sealed record StepUp(string AuthReqId, string? BindingCode, int ExpiresInSeconds, int IntervalSeconds) : WalletSignOutcome;

    /// <summary>Biatec OIDC answered and refused (limits, scope, bad request). Not an outage.</summary>
    public sealed record Rejected(int Status, string Error) : WalletSignOutcome;

    /// <summary>Biatec OIDC could not be reached or answered with a server failure: an outage, not a refusal.</summary>
    public sealed record Unavailable(string Reason) : WalletSignOutcome;
}

/// <summary>
/// The only way the backend gets signatures: straight to Biatec OIDC. Biatec MCP's <c>signTransaction</c> is never
/// used, so the step-up challenge always reaches the backend.
/// </summary>
public interface IWalletSigningClient
{
    Task<WalletSignOutcome> SignAsync(string bearer, WalletSignRequest request, CancellationToken ct = default);
}

public sealed class WalletSigningClient(HttpClient http) : IWalletSigningClient
{
    public const string StepUpError = "insufficient_user_authentication";

    public async Task<WalletSignOutcome> SignAsync(string bearer, WalletSignRequest request, CancellationToken ct = default)
    {
        var path = $"wallet/{Uri.EscapeDataString(request.Network)}/{Uri.EscapeDataString(request.Address)}/sign";
        using var msg = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new Body
            {
                Transactions = request.Transactions,
                Grp = Convert.ToBase64String(request.GroupId),
                RequireConsent = request.RequireConsent,
                MaxConsentSeconds = request.MaxConsentSeconds,
                Origin = request.Origin,
            }),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        HttpResponseMessage res;
        string text;
        try
        {
            res = await http.SendAsync(msg, ct);
            text = await res.Content.ReadAsStringAsync(ct);
        }
        // Timeouts surface as TaskCanceledException without the caller's token being cancelled.
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or IOException) && !ct.IsCancellationRequested)
        {
            return new WalletSignOutcome.Unavailable(ex.GetType().Name);
        }

        using (res)
        {
            var code = (int)res.StatusCode;
            // Never log or surface the body: on success it holds signed transactions.
            if (res.IsSuccessStatusCode) return ParseSigned(text);
            if (res.StatusCode == HttpStatusCode.Unauthorized && ReadString(text, "error") == StepUpError)
            {
                var id = ReadString(text, "auth_req_id");
                return string.IsNullOrEmpty(id)
                    ? new WalletSignOutcome.Rejected(code, "invalid_challenge")
                    : new WalletSignOutcome.StepUp(id, ReadString(text, "binding_code"), ReadInt(text, "expires_in") ?? 300, ReadInt(text, "interval") ?? 2);
            }
            if (code >= 500 || res.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                return new WalletSignOutcome.Unavailable("http_" + code);
            return new WalletSignOutcome.Rejected(code, ReadString(text, "error") ?? "rejected");
        }
    }

    private static WalletSignOutcome ParseSigned(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("signedTransactions", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new WalletSignOutcome.Rejected(200, "invalid_response");
            var list = new List<string>();
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(e.GetString())) return new WalletSignOutcome.Rejected(200, "invalid_response");
                list.Add(e.GetString()!);
            }
            string? valued = r.TryGetProperty("valuedEur", out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
            return list.Count == 0 ? new WalletSignOutcome.Rejected(200, "invalid_response") : new WalletSignOutcome.Signed(list, valued);
        }
        catch (JsonException) { return new WalletSignOutcome.Rejected(200, "invalid_response"); }
    }

    private static string? ReadString(string text, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static int? ReadInt(string text, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
        }
        catch (JsonException) { return null; }
    }

    private sealed class Body
    {
        [JsonPropertyName("transactions")] public required IReadOnlyList<string> Transactions { get; init; }
        [JsonPropertyName("grp")] public required string Grp { get; init; }
        [JsonPropertyName("requireConsent")] public bool RequireConsent { get; init; }
        [JsonPropertyName("maxConsentSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MaxConsentSeconds { get; init; }
        [JsonPropertyName("origin"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Origin { get; init; }
    }
}
