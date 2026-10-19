using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Biatec.Chat.Tools.Builders;

/// <summary>Connection settings for the Node builder sidecar (<c>builders/sidecar</c>).</summary>
public sealed record BuildersOptions
{
    public const string SecretHeader = "X-Internal-Secret";

    /// <summary>Base URL of the sidecar, for example <c>http://builders:3100</c>.</summary>
    public required Uri BaseAddress { get; init; }

    /// <summary>Shared secret; must equal the sidecar's <c>BUILDERS_INTERNAL_SECRET</c>. Never logged.</summary>
    public required string InternalSecret { get; init; }
}

/// <summary>Unsigned transactions as base64 msgpack, in group order, exactly as the builder returned them.</summary>
public sealed record BuildResult(IReadOnlyList<string> Transactions);

/// <summary>
/// A builder call failed. <see cref="Exception.Message"/> carries a truncated, control-character-free excerpt of the
/// sidecar's error text, which comes from third-party libraries and may echo untrusted request content: treat it as
/// untrusted data and never forward it to the model or browser. Tools should surface <see cref="PublicMessage"/>.
/// </summary>
public sealed class BuilderException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;

    /// <summary>Generic, safe text for tool results; contains no sidecar text.</summary>
    public string PublicMessage => "The transaction builder could not build this request.";
}

public interface IBuildersClient
{
    /// <summary>True when the sidecar answers <c>/health</c> with status ok.</summary>
    Task<bool> IsHealthyAsync(CancellationToken ct = default);

    /// <summary>Asks the named builder (for example <c>clamm</c> or <c>safe</c>) for an unsigned group.</summary>
    Task<BuildResult> BuildAsync(string builder, string network, object? parameters, CancellationToken ct = default);
}

/// <summary>Typed client for the builder sidecar. The caller supplies an <see cref="HttpClient"/> (typically from IHttpClientFactory).</summary>
public sealed class BuildersClient(HttpClient http, BuildersOptions options) : IBuildersClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await http.SendAsync(Request(HttpMethod.Get, "health"), ct);
            if (!response.IsSuccessStatusCode) return false;
            var body = await response.Content.ReadFromJsonAsync<HealthBody>(Json, ct);
            return body?.Status == "ok";
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<BuildResult> BuildAsync(string builder, string network, object? parameters, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(builder)) throw new ArgumentException("Builder name is required.", nameof(builder));
        using var request = Request(HttpMethod.Post, $"v1/build/{Uri.EscapeDataString(builder)}");
        request.Content = JsonContent.Create(new { network, @params = parameters }, options: Json);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await TryReadError(response, ct);
            throw new BuilderException($"Builder '{builder}' failed ({(int)response.StatusCode}): {error}", response.StatusCode);
        }
        var result = await response.Content.ReadFromJsonAsync<BuildBody>(Json, ct);
        if (result?.Transactions is not { Count: > 0 })
            throw new BuilderException($"Builder '{builder}' returned no transactions.", response.StatusCode);
        return new BuildResult(result.Transactions);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(options.BaseAddress, path));
        request.Headers.TryAddWithoutValidation(BuildersOptions.SecretHeader, options.InternalSecret);
        return request;
    }

    private static async Task<string> TryReadError(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var e = await response.Content.ReadFromJsonAsync<ErrorBody>(Json, ct);
            return Sanitize(e?.Message ?? e?.Error);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return "unknown error";
        }
    }

    internal const int MaxErrorLength = 200;

    internal static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "unknown error";
        var sb = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (sb.Length >= MaxErrorLength) { sb.Append('…'); break; }
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString();
    }

    private sealed record HealthBody([property: JsonPropertyName("status")] string? Status);
    private sealed record BuildBody(List<string>? Transactions);
    private sealed record ErrorBody(string? Error, string? Message);
}
