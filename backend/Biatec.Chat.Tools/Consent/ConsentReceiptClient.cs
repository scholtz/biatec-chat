using System.Net.Http.Headers;
using System.Text.Json;

namespace Biatec.Chat.Tools.Consent;

public interface IConsentReceiptClient
{
    /// <summary>
    /// <c>GET /consent/receipts/{auth_req_id}</c> as the client the request belongs to. The receipt as raw JSON text, or null when it cannot be
    /// had (unknown, not yet stored, a failure, a timeout): best effort, never throws.
    /// </summary>
    Task<string?> GetAsync(Uri receiptEndpoint, string bearer, CancellationToken ct = default);
}

public sealed class ConsentReceiptClient(HttpClient http) : IConsentReceiptClient
{
    private const int MaxBytes = 64 * 1024;

    public async Task<string?> GetAsync(Uri receiptEndpoint, string bearer, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, receiptEndpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return null;
            if (res.Content.Headers.ContentLength is > MaxBytes) return null;
            var text = await res.Content.ReadAsStringAsync(ct);
            if (text.Length > MaxBytes) return null;
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.GetRawText() : null;
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidOperationException) && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
