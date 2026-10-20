using System.Globalization;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Tools.Market;

namespace Biatec.Chat.Api.Market;

/// <summary>
/// <c>market.snapshot</c> (Anonymous, no scopes, moves no funds): a 24 hour market card for one pair from public Biatec DEX
/// data. The card is built here from the upstream data, never from model text; the model gets the same numbers as text so it
/// can describe the card but has no reason to invent any. An upstream failure gives a plain "market data unavailable"
/// answer and no card. Facts only: no forecast, signal or recommendation.
/// </summary>
public sealed class MarketSnapshotExecutor(IBiatecDexMarketClient market) : IToolExecutor
{
    public const string Kind = "market";
    public const string Unavailable = "Market data unavailable. Tell the user that market data is unavailable right now and to try again later. Do not state any price or number.";

    public string CapabilityName => CapabilityRegistry.MarketSnapshot;

    public string? StatusLabel(JsonNode arguments) => "Reading Biatec DEX market data";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, JsonNode arguments, CancellationToken ct)
    {
        if (!TryId(arguments["assetAId"], out var a) || !TryId(arguments["assetBId"], out var b) || a == b)
            return new ToolExecutionResult("Two different asset ids are needed (0 is ALGO). Ask the user which assets they mean.", true);

        MarketSnapshot s;
        try { s = await market.GetSnapshotAsync(a, b, ct); }
        catch (MarketDataUnavailableException) { return new ToolExecutionResult(Unavailable, true); }

        var card = new DataCardEvent
        {
            Kind = Kind,
            Network = "mainnet",
            Addresses = [],
            Assets = [],
            AsOf = s.AsOf,
            Pair = s.Pair,
            Price = s.Price,
            Change24hPct = s.Change24hPct,
            High24h = s.High24h,
            Low24h = s.Low24h,
            Volume24h = s.Volume24h,
            Series = s.Series,
            Source = s.Source,
        };
        var content = $"A market card is now shown to the user. Source {s.Source}, data as of {s.AsOf:yyyy-MM-dd HH:mm} UTC, not live. " +
                      $"Pair {s.Pair}: price {s.Price}, 24 h change {s.Change24hPct}%, high {s.High24h}, low {s.Low24h}, volume {s.Volume24h}. " +
                      "Describe the card briefly using only these numbers. No forecast, signal or advice.";
        return new ToolExecutionResult(content, false, [card]);
    }

    private static bool TryId(JsonNode? n, out ulong id)
    {
        id = 0;
        return n is JsonValue v && ulong.TryParse(v.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }
}

public static class MarketRegistration
{
    /// <summary>Registers the Biatec DEX client (configuration section "Market") and the <c>market.snapshot</c> executor.</summary>
    public static IServiceCollection AddMarketSnapshotTool(this IServiceCollection services, IConfiguration configuration)
    {
        Biatec.Chat.Tools.Market.MarketRegistration.AddBiatecDexMarket(services, configuration);
        services.AddSingleton<IToolExecutor, MarketSnapshotExecutor>();
        return services;
    }
}
