using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Tools.Market;

/// <summary>
/// Reads the public Biatec DEX OHLC history (<c>GET /api/OHLC/history?assetA&amp;assetB&amp;resolution&amp;from&amp;to</c>, TradingView
/// UDF shape <c>{s, t[], o[], h[], l[], c[], v[]}</c>) and the asset names (<c>GET /api/asset-stat/{id}</c>, best effort). No
/// credentials are sent. Numbers are parsed straight from the JSON text into <see cref="decimal"/>, never through double.
/// </summary>
public sealed class BiatecDexMarketClient(HttpClient http, TimeProvider clock) : IBiatecDexMarketClient
{
    public const string SourceName = "Biatec DEX";
    private const string Resolution = "60";
    private const int MaxBars = 48;

    public async Task<MarketSnapshot> GetSnapshotAsync(ulong assetA, ulong assetB, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var to = now.ToUnixTimeSeconds();
        var from = now.AddHours(-24).ToUnixTimeSeconds();
        try
        {
            var url = string.Create(CultureInfo.InvariantCulture, $"api/OHLC/history?assetA={assetA}&assetB={assetB}&resolution={Resolution}&from={from}&to={to}");
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) throw new MarketDataUnavailableException($"Biatec DEX answered {(int)response.StatusCode}.");
            var body = await response.Content.ReadAsStringAsync(ct);
            var a = await NameAsync(assetA, ct);
            var b = await NameAsync(assetB, ct);
            return Parse(body, $"{a}/{b}");
        }
        catch (MarketDataUnavailableException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or OverflowException or InvalidOperationException or KeyNotFoundException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new MarketDataUnavailableException("Biatec DEX market data could not be read.", ex);
        }
    }

    /// <summary>Parses a UDF history document into a snapshot. Public for tests.</summary>
    public static MarketSnapshot Parse(string json, string pair)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("s", out var s) || s.ValueKind != JsonValueKind.String || s.GetString() != "ok")
            throw new MarketDataUnavailableException("Biatec DEX has no data for this pair.");
        var t = Arr(root, "t"); var o = Arr(root, "o"); var h = Arr(root, "h"); var l = Arr(root, "l"); var c = Arr(root, "c");
        var v = Arr(root, "v");
        var n = t.Count;
        if (n == 0 || o.Count != n || h.Count != n || l.Count != n || c.Count != n)
            throw new MarketDataUnavailableException("Biatec DEX returned no usable data for this pair.");

        var closes = c.Select(Dec).ToList();
        var first = Dec(o[0]);
        var last = closes[^1];
        if (last <= 0 || first <= 0) throw new MarketDataUnavailableException("Biatec DEX returned no usable price for this pair.");
        var change = Math.Round((last - first) / first * 100m, 2, MidpointRounding.AwayFromZero);
        var volume = v.Sum(x => x.ValueKind == JsonValueKind.Number ? Dec(x) : 0m);
        var asOf = DateTimeOffset.FromUnixTimeSeconds(t[^1].GetInt64());

        // Down-sample to at most MaxBars points, always keeping the newest one.
        var step = Math.Max(1, (int)Math.Ceiling(n / (double)MaxBars));
        var series = new List<string>();
        for (var i = n - 1; i >= 0; i -= step) series.Add(Fmt(closes[i]));
        series.Reverse();

        return new MarketSnapshot(pair, Fmt(last), Fmt(change), Fmt(h.Select(Dec).Max()), Fmt(l.Select(Dec).Min()), Fmt(volume), series, asOf, SourceName);
    }

    private static List<JsonElement> Arr(JsonElement root, string name) =>
        root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : [];

    private static decimal Dec(JsonElement e) => e.ValueKind == JsonValueKind.Number
        ? decimal.Parse(e.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture)
        : throw new FormatException("Expected a number.");

    /// <summary>Plain decimal text without exponent or trailing zeros.</summary>
    internal static string Fmt(decimal d) => d.ToString("0.############################", CultureInfo.InvariantCulture);

    private async Task<string> NameAsync(ulong id, CancellationToken ct)
    {
        if (id == 0) return "ALGO";
        try
        {
            using var r = await http.GetAsync(string.Create(CultureInfo.InvariantCulture, $"api/asset-stat/{id}"), ct);
            if (r.IsSuccessStatusCode)
            {
                using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("unitName", out var u)
                    && u.ValueKind == JsonValueKind.String && Clean(u.GetString()) is { } name)
                    return name;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested)) { }
        return "#" + id.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Unit names come from the chain: short, printable, no markup characters.</summary>
    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Length is > 0 and <= 16 && t.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_') ? t : null;
    }
}

/// <summary>Short per-pair cache in front of any <see cref="IBiatecDexMarketClient"/>. Failures are never cached.</summary>
public sealed class CachingMarketClient(IBiatecDexMarketClient inner, IOptions<MarketOptions> options, TimeProvider clock) : IBiatecDexMarketClient
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong, ulong), (DateTimeOffset At, MarketSnapshot Snapshot)> _cache = new();

    public async Task<MarketSnapshot> GetSnapshotAsync(ulong assetA, ulong assetB, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_cache.TryGetValue((assetA, assetB), out var hit) && now - hit.At < options.Value.CacheDuration) return hit.Snapshot;
        var snap = await inner.GetSnapshotAsync(assetA, assetB, ct);
        if (_cache.Count > 256) _cache.Clear();
        _cache[(assetA, assetB)] = (now, snap);
        return snap;
    }
}

public static class MarketRegistration
{
    /// <summary>Binds "Market" and registers the typed HTTP client behind <see cref="IBiatecDexMarketClient"/> with a short cache.</summary>
    public static IServiceCollection AddBiatecDexMarket(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MarketOptions>().Bind(configuration.GetSection(MarketOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient<BiatecDexMarketClient>((sp, c) =>
        {
            var o = sp.GetRequiredService<IOptions<MarketOptions>>().Value;
            c.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = o.RequestTimeout;
        });
        services.AddSingleton<IBiatecDexMarketClient>(sp => new CachingMarketClient(
            sp.GetRequiredService<BiatecDexMarketClient>(), sp.GetRequiredService<IOptions<MarketOptions>>(), sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
