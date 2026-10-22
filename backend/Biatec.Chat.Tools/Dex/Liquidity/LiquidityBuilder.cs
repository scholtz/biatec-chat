using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using Biatec.Chat.Tools.Algod;
using Biatec.Chat.Tools.Builders;

namespace Biatec.Chat.Tools.Dex.Liquidity;

/// <summary>A registered Biatec DEX pool as the sidecar reports it. Prices are in the pool's 1e9 scale; use <see cref="LiquidityFormat.Scaled"/>.</summary>
public sealed record PoolInfo(
    ulong AppId,
    ulong AssetA,
    ulong AssetB,
    ulong LpTokenId,
    string FeePercent,
    string PriceMin,
    string PriceMax,
    string CurrentPrice,
    string Liquidity,
    int VerificationClass)
{
    /// <summary>Price range of asset A in asset B as decimal text, for example "0.13 to 0.14".</summary>
    public string PriceRange => $"{LiquidityFormat.Scaled(PriceMin)} to {LiquidityFormat.Scaled(PriceMax)}";
}

/// <summary>A pool the account may not use, and why (its verification class is below the pool's minimum).</summary>
public sealed record ExcludedPool(ulong AppId, int RequiredVerificationClass);

public sealed record PoolListing(IReadOnlyList<PoolInfo> Pools, IReadOnlyList<ExcludedPool> Excluded, int? AccountVerificationClass);

/// <summary>An unsigned, grouped liquidity transaction set with the facts the builder decoded for the preview.</summary>
public sealed record LiquidityBuild(
    string Kind,
    PoolInfo Pool,
    string Sender,
    GroupedTransactions Group,
    string? AmountA,
    string? AmountB,
    string? LpAmount,
    bool LpOptIn,
    ulong FirstValid,
    ulong LastValid,
    ulong BuiltAtRound);

/// <summary>The group the sidecar returned is not what was asked for (wrong sender, malformed facts). Nothing is previewed.</summary>
public sealed class LiquidityBuildException(string message) : Exception(message);

/// <summary>Pool discovery and the two liquidity groups, all built by <c>biatec-concentrated-liquidity-amm</c> in the sidecar (never by hand).</summary>
public interface ILiquidityBuilder
{
    /// <summary>
    /// The pools of an asset pair, or the one pool <paramref name="poolAppId"/> when given. With an
    /// <paramref name="account"/>, pools its verification class does not meet are listed under <see cref="PoolListing.Excluded"/>.
    /// </summary>
    Task<PoolListing> ListPoolsAsync(string network, ulong? assetA, ulong? assetB, ulong? poolAppId, string? account, CancellationToken ct = default);

    Task<LiquidityBuild> BuildAddAsync(string network, string sender, PoolInfo pool, string amountA, string amountB, CancellationToken ct = default);

    Task<LiquidityBuild> BuildRemoveAsync(string network, string sender, PoolInfo pool, string lpAmount, CancellationToken ct = default);
}

public sealed class LiquidityBuilder(IBuildersClient builders) : ILiquidityBuilder
{
    public const string PoolsQuery = "clamm.pools.list";
    public const string AddBuilder = "clamm.liquidity.add";
    public const string RemoveBuilder = "clamm.liquidity.remove";

    public async Task<PoolListing> ListPoolsAsync(string network, ulong? assetA, ulong? assetB, ulong? poolAppId, string? account, CancellationToken ct = default)
    {
        var p = new Dictionary<string, object?>();
        if (poolAppId is { } id) p["poolAppId"] = id.ToString(CultureInfo.InvariantCulture);
        else if (assetA is { } a && assetB is { } b)
        {
            p["assetA"] = a.ToString(CultureInfo.InvariantCulture);
            p["assetB"] = b.ToString(CultureInfo.InvariantCulture);
        }
        else throw new ArgumentException("A pool app id or both asset ids are required.");
        if (account is not null) p["account"] = account;

        var json = await builders.QueryAsync(PoolsQuery, network, p, ct);
        try
        {
            var pools = Items(json, "pools").Select(ParsePool).ToList();
            var excluded = Items(json, "excluded").Select(e => new ExcludedPool(U64(e, "appId"), (int)U64(e, "verificationClass"))).ToList();
            int? cls = json.TryGetProperty("accountVerificationClass", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
            return new PoolListing(pools, excluded, cls);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new BuilderException("The pool query returned an unexpected shape.");
        }
    }

    public Task<LiquidityBuild> BuildAddAsync(string network, string sender, PoolInfo pool, string amountA, string amountB, CancellationToken ct = default)
    {
        // Amounts must fit a uint64 before anything is sent to the sidecar.
        var a = U64Amount(amountA);
        var b = U64Amount(amountB);
        return BuildAsync(AddBuilder, network, sender, pool, new { poolAppId = pool.AppId.ToString(CultureInfo.InvariantCulture), sender, amountA, amountB },
            x => { Same(x.AmountA, amountA, "amountA"); Same(x.AmountB, amountB, "amountB"); },
            (raw, txs) => LiquidityGroupVerifier.VerifyAdd(raw, txs, network, sender, pool, a, b), ct);
    }

    public Task<LiquidityBuild> BuildRemoveAsync(string network, string sender, PoolInfo pool, string lpAmount, CancellationToken ct = default)
    {
        var lp = U64Amount(lpAmount);
        return BuildAsync(RemoveBuilder, network, sender, pool, new { poolAppId = pool.AppId.ToString(CultureInfo.InvariantCulture), sender, lpAmount },
            x => Same(x.LpAmount, lpAmount, "lpAmount"),
            (raw, txs) => LiquidityGroupVerifier.VerifyRemove(raw, txs, network, sender, pool, lp), ct);
    }

    private static ulong U64Amount(string amount) =>
        ulong.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : throw new LiquidityBuildException("The requested amount is not a valid amount.");

    /// <summary>The amount the builder reports must equal the requested one, else the card would show a transfer the group does not make.</summary>
    private static void Same(string? reported, string requested, string name)
    {
        if (!BigInteger.TryParse(reported, NumberStyles.None, CultureInfo.InvariantCulture, out var r)
            || !BigInteger.TryParse(requested, NumberStyles.None, CultureInfo.InvariantCulture, out var q) || r != q)
            throw new LiquidityBuildException($"The builder facts {name} differ from the request.");
    }

    private async Task<LiquidityBuild> BuildAsync(string builder, string network, string sender, PoolInfo listed, object parameters, Action<LiquidityBuild> matchesRequest, Action<IReadOnlyList<string>, IReadOnlyList<Transaction>> verifyGroup, CancellationToken ct)
    {
        var result = await builders.BuildAsync(builder, network, parameters, ct);
        if (result.Facts is not { ValueKind: JsonValueKind.Object } facts)
            throw new LiquidityBuildException("The builder returned no facts to preview.");

        GroupedTransactions group;
        IReadOnlyList<Transaction> decoded;
        try { decoded = AlgorandGroup.Decode(result.Transactions); group = AlgorandGroup.Ensure(decoded); }
        catch (Exception e) when (e is GroupException or ArgumentException or InvalidOperationException or FormatException)
        {
            throw new LiquidityBuildException("The builder returned a group that could not be read.");
        }

        // The group must be the sender's alone: nothing in it may be sent by anyone else.
        if (group.Transactions.Any(t => !string.Equals(t.Sender.EncodeAsString(), sender, StringComparison.Ordinal)))
            throw new LiquidityBuildException("The built group has a transaction from a different sender.");
        if (!string.Equals(Str(facts, "sender"), sender, StringComparison.Ordinal))
            throw new LiquidityBuildException("The builder facts name a different sender.");

        try
        {
            if (!facts.TryGetProperty("pool", out var pool)) throw new LiquidityBuildException("The builder facts have no pool.");
            var (first, last) = group.ValidityWindow();
            var builtAt = Str(facts, "firstValid") is null ? first : U64(facts, "firstValid");
            var build = new LiquidityBuild(Str(facts, "kind") ?? builder, ParsePool(pool), sender, group,
                Str(facts, "amountA"), Str(facts, "amountB"), Str(facts, "lpAmount"),
                facts.TryGetProperty("lpOptIn", out var o) && o.ValueKind == JsonValueKind.True, first, last, builtAt);
            // The pool the user chose comes from the pool query; the facts must agree with it on every asset the group moves.
            if (build.Pool.AppId != listed.AppId || build.Pool.AssetA != listed.AssetA || build.Pool.AssetB != listed.AssetB || build.Pool.LpTokenId != listed.LpTokenId)
                throw new LiquidityBuildException("The builder facts name a different pool.");
            matchesRequest(build);
            // The signed bytes are the transactions, not the facts: verify the transactions themselves.
            try { verifyGroup(result.Transactions, decoded); }
            catch (GroupException e) { throw new LiquidityBuildException(e.Message); }
            return build;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new LiquidityBuildException("The builder facts are malformed.");
        }
    }

    private static PoolInfo ParsePool(JsonElement e) => new(
        U64(e, "appId"), U64(e, "assetA"), U64(e, "assetB"), U64(e, "lpTokenId"),
        Str(e, "feePercent") ?? "", Str(e, "priceMin") ?? "0", Str(e, "priceMax") ?? "0",
        Str(e, "currentPrice") ?? "0", Str(e, "liquidity") ?? "0", (int)U64(e, "verificationClass"));

    private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null } : null;

    private static ulong U64(JsonElement e, string name) =>
        ulong.Parse(Str(e, name) ?? throw new KeyNotFoundException(name), NumberStyles.None, CultureInfo.InvariantCulture);
}

public static class LiquidityFormat
{
    private static readonly BigInteger Scale = 1_000_000_000;

    /// <summary>A 1e9-scaled integer as decimal text without trailing zeros, for example "130000000" is "0.13". Unparseable input is returned unchanged.</summary>
    public static string Scaled(string scaled)
    {
        if (!BigInteger.TryParse(scaled, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return scaled;
        var whole = BigInteger.DivRem(v, Scale, out var frac);
        var f = frac.ToString(CultureInfo.InvariantCulture).PadLeft(9, '0').TrimEnd('0');
        return f.Length == 0 ? whole.ToString(CultureInfo.InvariantCulture) : whole.ToString(CultureInfo.InvariantCulture) + "." + f;
    }
}
