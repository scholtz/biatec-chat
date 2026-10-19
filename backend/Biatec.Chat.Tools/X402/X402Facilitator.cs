using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Biatec.Chat.Tools.X402;

/// <summary>One entry of <c>accepts</c> from a 402 response: what the seller asks for. Seller-controlled, therefore untrusted.</summary>
public sealed record X402Requirements(
    string Scheme,
    string Network,
    ulong MaxAmountRequired,
    string Resource,
    string PayTo,
    ulong Asset,
    string? Description);

/// <summary>The signed payment sent in <c>X-PAYMENT</c> and to the facilitator: the signed group, base64 msgpack in order.</summary>
public sealed record X402PaymentPayload(IReadOnlyList<string> SignedGroup);

public sealed record X402VerifyResult(bool IsValid, string? InvalidReason);
public sealed record X402SettleResult(bool Success, string? Transaction, string? ErrorReason);

public interface IX402Facilitator
{
    Task<X402VerifyResult> VerifyAsync(X402PaymentPayload payment, X402Requirements requirements, CancellationToken ct);
    Task<X402SettleResult> SettleAsync(X402PaymentPayload payment, X402Requirements requirements, CancellationToken ct);
}

/// <summary>
/// Client for an x402 facilitator (<c>POST /verify</c>, <c>POST /settle</c>, as served by <c>algo-safe-x402-facilitator</c>).
/// The base URL is operator configuration, not model input. Responses are size-bounded and only the documented fields are read.
/// </summary>
public sealed class HttpX402Facilitator(HttpClient http, X402Options options) : IX402Facilitator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<X402VerifyResult> VerifyAsync(X402PaymentPayload payment, X402Requirements requirements, CancellationToken ct)
    {
        var r = await PostAsync<VerifyBody>("verify", payment, requirements, ct);
        return new X402VerifyResult(r?.IsValid == true, r?.InvalidReason);
    }

    public async Task<X402SettleResult> SettleAsync(X402PaymentPayload payment, X402Requirements requirements, CancellationToken ct)
    {
        var r = await PostAsync<SettleBody>("settle", payment, requirements, ct);
        return new X402SettleResult(r?.Success == true, r?.Transaction, r?.ErrorReason);
    }

    private async Task<T?> PostAsync<T>(string path, X402PaymentPayload payment, X402Requirements requirements, CancellationToken ct)
    {
        if (!Uri.TryCreate(options.FacilitatorUrl?.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException("X402:FacilitatorUrl is not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, path))
        {
            Content = JsonContent.Create(new
            {
                x402Version = 1,
                paymentPayload = new { x402Version = 1, scheme = requirements.Scheme, network = requirements.Network, payload = new { paymentGroup = payment.SignedGroup, paymentIndex = 0 } },
                paymentRequirements = requirements,
            }, options: Json),
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return default;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        byte[] bytes;
        try { bytes = await X402Http.ReadBoundedAsync(stream, options.MaxResponseBytes, ct); }
        catch (X402PolicyException)
        {
            // The facilitator may already have acted: this is an unknown outcome, never "nothing was paid".
            throw new X402Exception("facilitator_response_too_large", "The facilitator response was too large to read.");
        }
        try { return JsonSerializer.Deserialize<T>(bytes, Json); }
        catch (JsonException) { return default; }
    }

    private sealed record VerifyBody(bool IsValid, string? InvalidReason);
    private sealed record SettleBody(bool Success, string? Transaction, string? ErrorReason);
}

/// <summary>
/// In-process facilitator for tests and the demo: accepts a payment that is a non-empty group and records it. No funds move,
/// nothing is submitted to a chain. It rejects when told to, so failure paths are testable.
/// </summary>
public sealed class MockX402Facilitator : IX402Facilitator
{
    private readonly List<(X402PaymentPayload Payment, X402Requirements Requirements)> _settled = [];

    public bool RejectVerify { get; set; }
    public bool RejectSettle { get; set; }
    public IReadOnlyList<(X402PaymentPayload Payment, X402Requirements Requirements)> Settled => _settled;

    public Task<X402VerifyResult> VerifyAsync(X402PaymentPayload payment, X402Requirements requirements, CancellationToken ct) =>
        Task.FromResult(RejectVerify || payment.SignedGroup.Count == 0
            ? new X402VerifyResult(false, "invalid_payment")
            : new X402VerifyResult(true, null));

    public Task<X402SettleResult> SettleAsync(X402PaymentPayload payment, X402Requirements requirements, CancellationToken ct)
    {
        if (RejectSettle) return Task.FromResult(new X402SettleResult(false, null, "settlement_failed"));
        _settled.Add((payment, requirements));
        return Task.FromResult(new X402SettleResult(true, "MOCKTX" + _settled.Count.ToString("D8"), null));
    }
}

internal static class X402Http
{
    /// <summary>Reads at most <paramref name="max"/> bytes; throws when the body is larger so a hostile server cannot exhaust memory.</summary>
    public static async Task<byte[]> ReadBoundedAsync(Stream stream, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) throw new X402PolicyException("response_too_large", "The response was too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
