using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using Biatec.Chat.Core.Canonical;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Algod;
using Biatec.Chat.Tools.Builders;
using Biatec.Chat.Tools.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Tools.Dex.Swap;

/// <summary>
/// The <c>dex.swap</c> executor. Biatec MCP <c>createSwapTransaction</c> is asked first: it returns an unsigned
/// transaction only when Biatec Router wins its quote. Otherwise, or when the user named a pool, the swap is built
/// against a registered Biatec DEX pool through the sidecar (<c>clamm.swap.pool</c>, i.e. <c>clammSwapSender</c>); with no
/// pool named the user is asked to choose one or cancel. Non-Biatec routers are out of scope. Nothing is signed here:
/// the result is a preview spec whose step carries the grouped unsigned transactions.
/// </summary>
public sealed class SwapExecutor(
    IBiatecMcpClient mcp,
    IBuildersClient builders,
    ISwapPoolDirectory pools,
    IOptions<SwapOptions> options,
    TimeProvider clock,
    ILogger<SwapExecutor> log)
{
    public const string Capability = "dex.swap";
    public const string PoolBuilder = "clamm.swap.pool";
    private const int Bps = 10_000;
    private static readonly BigInteger Scale = 1_000_000_000;

    private readonly SwapOptions _o = options.Value;

    public async Task<SwapOutcome> ExecuteAsync(SwapRequest req, CancellationToken ct = default)
    {
        if (!BigInteger.TryParse(req.Amount, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount.IsZero || amount > ulong.MaxValue)
            return new SwapRefused("The swap amount must be a positive whole number of base units.");
        if (req.FromAssetId == req.ToAssetId) return new SwapRefused("The swap needs two different assets.");
        var slippage = Math.Clamp(_o.SlippageBps, 1, _o.MaxSlippageBps);

        // A user who already chose a pool has declined the router; honour that and go straight to the pool.
        if (req.PoolAppId is { } chosen)
            return await BuildOnPoolAsync(req, amount, chosen, slippage, ct);

        var router = await TryRouterAsync(req, slippage, ct);
        if (router.Outcome is not null) return router.Outcome;

        SwapPoolListing listing;
        try { listing = await pools.ListAsync(req.Network, req.FromAssetId, req.ToAssetId, req.From, ct); }
        catch (SwapPoolDirectoryException e)
        {
            log.LogWarning(e, "Swap pool listing failed for {Network}", req.Network);
            return new SwapRefused("Biatec Router has no swap for this pair and the Biatec DEX pools could not be read. Try again shortly.");
        }
        if (listing.Pools.Count == 0)
        {
            var why = listing.Excluded.Count > 0
                ? "Pools exist for this pair, but your account's verification class is below their minimum. Getting the account verified in Biatec would unlock them."
                : "There is no Biatec DEX pool for this pair.";
            return new SwapRefused($"{router.Reason} {why}");
        }
        return new SwapPoolChoiceRequired(
            "Biatec Router cannot fill this swap. Which Biatec DEX pool should it use? Pick one, or cancel.",
            listing.Pools, listing.Excluded, router.Reason);
    }

    private sealed record RouterAttempt(SwapOutcome? Outcome, string Reason);

    /// <summary>Outcome is set when the router path produced a preview or a hard refusal; otherwise Reason says why it did not win.</summary>
    private async Task<RouterAttempt> TryRouterAsync(SwapRequest req, int slippageBps, CancellationToken ct)
    {
        McpToolResult result;
        try
        {
            result = await mcp.CreateSwapTransactionAsync(req.Context, req.Network, req.From, req.FromAssetId, req.ToAssetId, req.Amount,
                FormatPercent(slippageBps), ct);
        }
        catch (McpToolUnavailableException)
        {
            return new(null, "The Biatec Router swap tool is unavailable.");
        }
        if (result.IsError || result.Json is not { ValueKind: JsonValueKind.Object } json)
            return new(null, "Biatec Router could not quote this swap.");

        var txs = StringArray(json, "transactions");
        if (txs.Count == 0 && Str(json, "transaction") is { } single) txs = [single];
        var routerName = Str(json, "router") ?? Str(json, "winner");
        if (txs.Count == 0 || (routerName is not null && !routerName.Contains("biatec", StringComparison.OrdinalIgnoreCase)))
            return new(null, "Biatec Router does not offer the best price for this swap.");

        var quote = json.TryGetProperty("quote", out var q) && q.ValueKind == JsonValueKind.Object ? q : json;
        if (!TryBig(Str(quote, "expectedOutput") ?? Str(quote, "expectedReceived"), out var expected) || expected.IsZero)
            return new(null, "Biatec Router returned an incomplete quote.");
        var min = TryBig(Str(quote, "minimumReceived") ?? Str(quote, "minimumOutput"), out var m) ? m : ApplySlippage(expected, slippageBps);
        // Never accept a router's own looser protection than the configured ceiling.
        if (min * Bps < expected * (Bps - _o.MaxSlippageBps) || min > expected || min.IsZero)
            return new(null, "Biatec Router returned a quote with unsafe slippage protection.");
        var effectiveBps = (int)((expected - min) * Bps / expected);

        GroupedTransactions group;
        try { group = Group(txs, req.From); }
        catch (Exception e) when (e is GroupException or FormatException or ArgumentException or InvalidOperationException)
        {
            log.LogWarning(e, "Router swap group rejected");
            return new(new SwapRefused("The swap the router built did not pass the safety checks, so it was discarded."), "");
        }

        var card = Card(SwapRoute.BiatecRouter, req, expected, min, effectiveBps, Str(quote, "priceImpact"), null, group,
            ["Quoted by Biatec Router through Biatec MCP."]);
        return new(Ready(req, group, card), "");
    }

    private async Task<SwapOutcome> BuildOnPoolAsync(SwapRequest req, BigInteger amount, ulong poolAppId, int slippageBps, CancellationToken ct)
    {
        SwapPoolListing listing;
        try { listing = await pools.ListAsync(req.Network, req.FromAssetId, req.ToAssetId, req.From, ct); }
        catch (SwapPoolDirectoryException e)
        {
            log.LogWarning(e, "Swap pool listing failed for {Network}", req.Network);
            return new SwapRefused("The Biatec DEX pools could not be read. Try again shortly.");
        }
        // The pool id comes from the model: only a pool the account may use, for this pair, is ever built against.
        var pool = listing.Pools.FirstOrDefault(p => p.AppId == poolAppId);
        if (pool is null)
            return listing.Excluded.Any(e => e.AppId == poolAppId)
                ? new SwapRefused("That pool needs a higher verification class than your account has. Getting the account verified in Biatec would unlock it.")
                : new SwapRefused("That is not a Biatec DEX pool for this pair.");

        if (!TryEstimate(pool, req.FromAssetId, amount, out var expected))
            return new SwapRefused("The pool has no usable price right now.");
        var min = ApplySlippage(expected, slippageBps);
        if (min.IsZero) return new SwapRefused("The amount is too small to swap with slippage protection.");

        BuildResult built;
        try
        {
            built = await builders.BuildAsync("clamm.swap.pool", req.Network, new
            {
                poolAppId = pool.AppId.ToString(),
                sender = req.From,
                fromAsset = req.FromAssetId.ToString(),
                fromAmount = req.Amount,
                minimumToReceive = min.ToString(CultureInfo.InvariantCulture),
            }, ct);
        }
        catch (BuilderException e)
        {
            log.LogWarning("Pool swap build failed: {Message}", e.Message);
            return new SwapRefused(e.PublicMessage);
        }

        GroupedTransactions group;
        try { group = Group(built.Transactions, req.From); }
        catch (Exception e) when (e is GroupException or FormatException or ArgumentException or InvalidOperationException)
        {
            log.LogWarning(e, "Pool swap group rejected");
            return new SwapRefused("The swap the builder produced did not pass the safety checks, so it was discarded.");
        }

        var card = Card(SwapRoute.BiatecPool, req, expected, min, slippageBps, null, pool, group,
        [
            "Biatec Router was not used; this swap runs on the pool you chose.",
            "The expected amount is an estimate from the pool's current price and fee. The minimum received is enforced on chain: the swap fails rather than pay less.",
        ]);
        return Ready(req, group, card);
    }

    private SwapPreviewReady Ready(SwapRequest req, GroupedTransactions group, SwapPreviewCard card)
    {
        var (first, last) = group.ValidityWindow();
        var expiresAt = PreviewPolicy.ExpiryFromLastValid(last, first, clock.GetUtcNow());
        var canonical = JsonCanonicalizer.Canonicalize(card, CardJson);
        var spec = new NewPreviewSpec(req.ConversationId, Capability, canonical,
        [
            new NewStepSpec(ActionStepKind.Sign, group.GroupHash, expiresAt,
                UnsignedGroup: JsonSerializer.Serialize(group.ToBase64()),
                SenderAddress: req.From, Network: req.Network, FirstValid: (long)first, LastValid: (long)last),
        ]);
        return new SwapPreviewReady(spec, card);
    }

    private static readonly JsonSerializerOptions CardJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static SwapPreviewCard Card(SwapRoute route, SwapRequest req, BigInteger expected, BigInteger min, int bps, string? impact,
        SwapPool? pool, GroupedTransactions group, IReadOnlyList<string> notes)
    {
        var where = route == SwapRoute.BiatecRouter ? "Biatec Router" : $"Biatec DEX pool {pool!.AppId}";
        return new SwapPreviewCard(route,
            $"Swap {req.Amount} of asset {req.FromAssetId} for at least {min} of asset {req.ToAssetId} on {where} (base units)",
            req.Network, req.From, req.FromAssetId, req.ToAssetId, req.Amount, expected.ToString(CultureInfo.InvariantCulture),
            min.ToString(CultureInfo.InvariantCulture), bps, impact, pool, group.GroupHash, group.TxIds, notes);
    }

    /// <summary>Decodes the builder's transactions, requires every one to come from the user's address and groups them.</summary>
    private static GroupedTransactions Group(IReadOnlyList<string> base64, string from)
    {
        var txs = AlgorandGroup.Decode(base64);
        if (txs.Any(t => !string.Equals(t.Sender.EncodeAsString(), from, StringComparison.Ordinal)))
            throw new GroupException("A transaction in the swap is not sent from the user's address.");
        return AlgorandGroup.Ensure(txs);
    }

    /// <summary>Output of the pool for <paramref name="amount"/> of the given asset at the pool's spot price, net of its fee.</summary>
    private static bool TryEstimate(SwapPool pool, ulong fromAsset, BigInteger amount, out BigInteger expected)
    {
        expected = BigInteger.Zero;
        if (!TryBig(pool.CurrentPrice, out var price) || price.IsZero || !TryBig(pool.Fee, out var fee) || fee >= Scale) return false;
        // currentPrice is the price of asset A in asset B, scaled by 1e9.
        var gross = fromAsset == pool.AssetA ? amount * price / Scale : amount * Scale / price;
        expected = gross * (Scale - fee) / Scale;
        return !expected.IsZero;
    }

    private static BigInteger ApplySlippage(BigInteger expected, int bps) => expected * (Bps - bps) / Bps;

    private static string FormatPercent(int bps) => (bps / 100m).ToString("0.##", CultureInfo.InvariantCulture);

    private static bool TryBig(string? s, out BigInteger v)
    {
        v = BigInteger.Zero;
        return s is not null && BigInteger.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v) && v <= ulong.MaxValue;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static List<string> StringArray(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
}
