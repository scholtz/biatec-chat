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
    ILogger<SwapExecutor> log,
    AlgodClientFactory? algod = null)
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

        var router = await TryRouterAsync(req, amount, slippage, ct);
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
    private async Task<RouterAttempt> TryRouterAsync(SwapRequest req, BigInteger amount, int slippageBps, CancellationToken ct)
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
        try { group = Group(txs, req.From, req.FromAssetId, amount, null); }
        catch (Exception e) when (e is GroupException or FormatException or ArgumentException or InvalidOperationException)
        {
            log.LogWarning(e, "Router swap group rejected");
            return new(new SwapRefused("The swap the router built did not pass the safety checks, so it was discarded."), "");
        }

        var (sim, simFailed) = await SimulateAsync(req.Network, group, ct);
        if (simFailed) return new(new SwapRefused("The swap the router built failed simulation, so it was discarded. " + sim), "");
        var card = Card(SwapRoute.BiatecRouter, req, expected, min, effectiveBps, Str(quote, "priceImpact"), null, group,
            ["Quoted by Biatec Router through Biatec MCP."], null, sim);
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

        if (!TryEstimate(pool, req.FromAssetId, amount, out var expected, out var impactPpm))
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
        try { group = Group(built.Transactions, req.From, req.FromAssetId, amount, pool.AppId); }
        catch (Exception e) when (e is GroupException or FormatException or ArgumentException or InvalidOperationException)
        {
            log.LogWarning(e, "Pool swap group rejected");
            return new SwapRefused("The swap the builder produced did not pass the safety checks, so it was discarded.");
        }

        var (sim, simFailed) = await SimulateAsync(req.Network, group, ct);
        if (simFailed) return new SwapRefused("The swap the builder produced failed simulation, so it was discarded. " + sim);

        var notes = new List<string>
        {
            "Biatec Router was not used; this swap runs on the pool you chose.",
            "The expected amount is an estimate from the pool's current price, fee and liquidity. The minimum received is enforced on chain: the swap fails rather than pay less.",
        };
        if (impactPpm is null) notes.Add("The pool's liquidity is unknown, so the price impact was not estimated and the expected amount may be too high.");
        else if (impactPpm.Value >= 10_000) notes.Add($"This amount is large relative to the pool's liquidity: the estimated price impact is {FormatImpact(impactPpm.Value)} %.");
        if (TryBig(pool.CurrentPrice, out var cp) && (!TryBig(pool.PriceMin, out var lo) || !TryBig(pool.PriceMax, out var hi) || cp < lo || cp > hi))
            notes.Add("The pool's current price is outside its configured price range.");
        var impact = impactPpm is { } ppm ? FormatImpact(ppm) : null;
        var card = Card(SwapRoute.BiatecPool, req, expected, min, slippageBps, impact, pool, group, notes, listing.AccountVerificationClass, sim);
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
        SwapPool? pool, GroupedTransactions group, IReadOnlyList<string> notes, int? accountClass, string? simulation)
    {
        var where = route == SwapRoute.BiatecRouter ? "Biatec Router" : $"Biatec DEX pool {pool!.AppId}";
        return new SwapPreviewCard(route,
            $"Swap {req.Amount} of asset {req.FromAssetId} for at least {min} of asset {req.ToAssetId} on {where} (base units)",
            req.Network, req.From, req.FromAssetId, req.ToAssetId, req.Amount, expected.ToString(CultureInfo.InvariantCulture),
            min.ToString(CultureInfo.InvariantCulture), bps, impact, pool, group.GroupHash, group.TxIds, notes, accountClass, simulation);
    }

    /// <summary>Decodes the builder's transactions, requires every one to come from the user's address and groups them.</summary>
    private static GroupedTransactions Group(IReadOnlyList<string> base64, string from, ulong fromAsset, BigInteger amount, ulong? poolAppId)
    {
        var txs = AlgorandGroup.Decode(base64);
        if (txs.Any(t => !string.Equals(t.Sender.EncodeAsString(), from, StringComparison.Ordinal)))
            throw new GroupException("A transaction in the swap is not sent from the user's address.");
        SwapGroupVerifier.Verify(txs, from, fromAsset, (ulong)amount, poolAppId);
        return AlgorandGroup.Ensure(txs);
    }

    /// <summary>
    /// Output of the pool for <paramref name="amount"/> of the given asset: spot price, net of the fee and of the price
    /// impact. Impact treats the active range as constant product with virtual reserves derived from the liquidity L
    /// (x = L / sqrtP, y = L * sqrtP), so a trade that moves the price is not shown at the spot rate. It is an estimate
    /// that ignores crossing out of the active range. <paramref name="impactPpm"/> is null when liquidity is unknown.
    /// </summary>
    private static bool TryEstimate(SwapPool pool, ulong fromAsset, BigInteger amount, out BigInteger expected, out BigInteger? impactPpm)
    {
        expected = BigInteger.Zero;
        impactPpm = null;
        if (!TryBig(pool.CurrentPrice, out var price) || price.IsZero || !TryBig(pool.Fee, out var fee) || fee >= Scale) return false;
        // currentPrice is the price of asset A in asset B, scaled by 1e9.
        var gross = fromAsset == pool.AssetA ? amount * price / Scale : amount * Scale / price;
        if (TryBig(pool.Liquidity, out var liquidity) && !liquidity.IsZero)
        {
            var sqrt = ISqrt(price * Scale); // sqrt(price) scaled by 1e9
            var reserveIn = fromAsset == pool.AssetA ? liquidity * Scale / sqrt : liquidity * sqrt / Scale;
            if (reserveIn.IsZero) return false;
            var denom = reserveIn + amount;
            gross -= gross * amount / denom;
            impactPpm = amount * 1_000_000 / denom;
        }
        expected = gross * (Scale - fee) / Scale;
        return !expected.IsZero;
    }

    private static string FormatImpact(BigInteger ppm) => ((decimal)(long)BigInteger.Min(ppm, 1_000_000) / 10_000m).ToString("0.####", CultureInfo.InvariantCulture);

    private static BigInteger ISqrt(BigInteger n)
    {
        if (n < 2) return n;
        var x = (BigInteger)Math.Sqrt((double)n);
        while (x * x > n) x--;
        while ((x + 1) * (x + 1) <= n) x++;
        return x;
    }

    /// <summary>Simulates the group on algod when a client is configured. Failed means the chain would reject it.</summary>
    private async Task<(string? Summary, bool Failed)> SimulateAsync(string network, GroupedTransactions group, CancellationToken ct)
    {
        var client = ClientFor(network);
        if (client is null) return ("Not simulated: no algod endpoint is configured for this network.", false);
        try
        {
            var outcome = await client.SimulateAsync(group.Transactions, false, ct);
            return (outcome.Summary, !outcome.Success);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Swap simulation unavailable for {Network}", network);
            return ("Not simulated: algod was unreachable.", false);
        }
    }

    private IAlgodSimulateClient? ClientFor(string network)
    {
        if (algod is null) return null;
        foreach (var name in new[] { network, "algorand-" + network })
        {
            try { return algod.For(name); }
            catch (ArgumentException) { }
        }
        return null;
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
