namespace Biatec.Chat.Tools.Market;

/// <summary>Biatec DEX market settings (section "Market"). The base URL is configuration, never code.</summary>
public sealed class MarketOptions
{
    public const string Section = "Market";
    /// <summary>Base URL of the public Biatec DEX (AVM Trade Reporter) API.</summary>
    public string BaseUrl { get; set; } = "https://api.algorand.scan.biatec.io";
    /// <summary>How long a snapshot is reused for the same pair. Short, so the "not live" card is never old.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(8);
}

/// <summary>
/// A 24 hour market snapshot of one pair. Every number is a decimal string, never a double. <see cref="AsOf"/> is the
/// timestamp of the newest data point, not of the request.
/// </summary>
public sealed record MarketSnapshot(
    string Pair,
    string Price,
    string Change24hPct,
    string High24h,
    string Low24h,
    string Volume24h,
    IReadOnlyList<string> Series,
    DateTimeOffset AsOf,
    string Source);

/// <summary>Public Biatec DEX market data. Mocked in tests; the HTTP implementation is <see cref="BiatecDexMarketClient"/>.</summary>
public interface IBiatecDexMarketClient
{
    /// <summary>Snapshot of asset A priced in asset B (0 is ALGO). Throws <see cref="MarketDataUnavailableException"/> on any upstream failure or empty data.</summary>
    Task<MarketSnapshot> GetSnapshotAsync(ulong assetA, ulong assetB, CancellationToken ct);
}

public sealed class MarketDataUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
