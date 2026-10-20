using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Biatec.Chat.Tools.Algod;

/// <summary>
/// How an asset's base units are shown. <see cref="UnitName"/> is the asset creator's text: untrusted data, already
/// cleaned by <see cref="AlgodAssetReader.CleanUnitName"/> (null when the asset has none).
/// </summary>
public sealed record AlgodAssetParams(ulong AssetId, int Decimals, string? UnitName);

/// <summary>What an account holds of one asset: <see cref="OptedIn"/> false (and amount 0) when it has not opted in.</summary>
public sealed record AlgodAccountAsset(bool OptedIn, ulong Amount);

/// <summary>Asset reads from algod, by network name (for example <c>algorand-testnet</c>). Null means the read failed.</summary>
public interface IAlgodAssetReader
{
    /// <summary>Decimals and cleaned unit name from <c>GET /v2/assets/{id}</c>, or null when algod cannot say.</summary>
    Task<AlgodAssetParams?> GetAssetAsync(string network, ulong assetId, CancellationToken ct = default);

    /// <summary>The holding from <c>GET /v2/accounts/{address}/assets/{id}</c> (404 is "not opted in"), or null when the read failed.</summary>
    Task<AlgodAccountAsset?> GetAccountAssetAsync(string network, string address, ulong assetId, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IAlgodAssetReader"/> over algod's REST API. Asset params are cached for a short time (decimals and unit
/// name cannot change on Algorand, the cache only saves round trips); failures are never cached.
/// </summary>
public sealed class AlgodAssetReader : IAlgodAssetReader
{
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(10);
    private const int MaxCacheEntries = 2048;

    private readonly Dictionary<string, AlgodNetworkOptions> _networks;
    private readonly Func<AlgodNetworkOptions, HttpClient> _httpFor;
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Network, ulong AssetId), (AlgodAssetParams Params, DateTimeOffset Until)> _cache = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;

    public AlgodAssetReader(IReadOnlyDictionary<string, AlgodNetworkOptions> networks, Func<AlgodNetworkOptions, HttpClient>? httpClient = null,
        TimeProvider? clock = null, TimeSpan? cacheTtl = null)
    {
        _networks = new Dictionary<string, AlgodNetworkOptions>(networks, StringComparer.OrdinalIgnoreCase);
        _httpFor = httpClient ?? (_ => new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
        _clock = clock ?? TimeProvider.System;
        _ttl = cacheTtl ?? DefaultCacheTtl;
    }

    public async Task<AlgodAssetParams?> GetAssetAsync(string network, ulong assetId, CancellationToken ct = default)
    {
        if (assetId == 0) return new AlgodAssetParams(0, 6, "ALGO");
        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue((network, assetId), out var hit) && hit.Until > now) return hit.Params;

        using var res = await SendAsync(network, $"v2/assets/{assetId.ToString(CultureInfo.InvariantCulture)}", ct);
        if (res is null || !res.IsSuccessStatusCode) return null;
        var result = await ReadAsync(res, root =>
            root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("decimals", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var decimals) && decimals is >= 0 and <= 19
                ? new AlgodAssetParams(assetId, decimals,
                    p.TryGetProperty("unit-name", out var u) && u.ValueKind == JsonValueKind.String ? CleanUnitName(u.GetString()) : null)
                : null, ct);
        if (result is null) return null;
        if (_cache.Count >= MaxCacheEntries) _cache.Clear();
        _cache[(network, assetId)] = (result, now + _ttl);
        return result;
    }

    public async Task<AlgodAccountAsset?> GetAccountAssetAsync(string network, string address, ulong assetId, CancellationToken ct = default)
    {
        using var res = await SendAsync(network, $"v2/accounts/{Uri.EscapeDataString(address)}/assets/{assetId.ToString(CultureInfo.InvariantCulture)}", ct);
        if (res is null) return null;
        if (res.StatusCode == HttpStatusCode.NotFound) return new AlgodAccountAsset(false, 0); // not opted in (or no such account)
        if (!res.IsSuccessStatusCode) return null;
        return await ReadAsync(res, root =>
            root.TryGetProperty("asset-holding", out var h) && h.ValueKind == JsonValueKind.Object
            && h.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetUInt64(out var amount)
                ? new AlgodAccountAsset(true, amount)
                : null, ct);
    }

    /// <summary>
    /// A creator-chosen unit name made safe to show and to put in model-facing text: control characters, quotes,
    /// backticks and angle brackets become spaces, whitespace collapses, and it is cut to 8 characters (the
    /// Algorand limit). Null when nothing is left.
    /// </summary>
    public static string? CleanUnitName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var chars = text.Select(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '\'' or '"' or '`' or '<' or '>' or '\\' ? ' ' : c).ToArray();
        var clean = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > 8) clean = clean[..8].TrimEnd();
        return clean.Length == 0 ? null : clean;
    }

    private async Task<HttpResponseMessage?> SendAsync(string network, string path, CancellationToken ct)
    {
        if (!_networks.TryGetValue(network, out var net) || string.IsNullOrWhiteSpace(net.BaseUrl)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(net.BaseUrl.TrimEnd('/') + "/"), path));
            if (net.Token is { Length: > 0 }) req.Headers.TryAddWithoutValidation("X-Algo-API-Token", net.Token);
            var http = _clients.GetOrAdd(network, _ => _httpFor(net));
            return await http.SendAsync(req, ct);
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException) && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage res, Func<JsonElement, T?> read, CancellationToken ct) where T : class
    {
        try
        {
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return doc.RootElement.ValueKind == JsonValueKind.Object ? read(doc.RootElement) : null;
        }
        catch (Exception e) when ((e is JsonException or HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
