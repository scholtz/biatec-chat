using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Wallet.Read;
using Biatec.Chat.Core.Canonical;
using SessionTier = Biatec.Chat.Core.Data.SessionTier;
using ActionStepKind = Biatec.Chat.Core.Data.ActionStepKind;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Builders;
using Biatec.Chat.Tools.Dex.Liquidity;
using Biatec.Chat.Tools.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ContractPreview = Biatec.Chat.Api.Contracts.ActionPreview;
using ContractStep = Biatec.Chat.Api.Contracts.ActionStep;

namespace Biatec.Chat.Api.Dex.Liquidity;

/// <summary>
/// Shared flow of <c>dex.addLiquidity</c> and <c>dex.removeLiquidity</c>: own-address check, the clarifying pool
/// listing when no pool was named, the build through the sidecar, and the persisted preview card. The executors never
/// sign or submit: the user's Confirm runs the shared confirm path (sign at Biatec OIDC, broadcast, track).
/// </summary>
public abstract class LiquidityExecutor(ILiquidityBuilder liquidity, IBiatecMcpClient mcp, IServiceScopeFactory scopes, TimeProvider clock) : IToolExecutor
{
    public abstract string CapabilityName { get; }
    public abstract string? StatusLabel(JsonNode arguments);

    protected ILiquidityBuilder Liquidity => liquidity;
    protected IBiatecMcpClient Mcp => mcp;

        protected abstract Task<ToolExecutionResult> RunAsync(ToolExecutionContext context, McpCallContext mcpCtx, LiquidityRequest request, JsonNode arguments, CancellationToken ct);

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, JsonNode arguments, CancellationToken ct)
    {
        if (context.Tier != SessionTier.Authenticated)
            return new ToolExecutionResult("This needs a signed-in user. Call requestSignIn instead.", true);
        try
        {
            var request = LiquidityRequest.Parse(arguments);
            var mcpCtx = new McpCallContext(context.TenantId, context.SessionId, context.Scopes);

            // Only the user's own addresses may be the liquidity provider.
            var own = await mcp.ListAlgorandAddressesAsync(mcpCtx, ct);
            if (own.IsError) return new ToolExecutionResult("The addresses could not be read. Tell the user to try again.", true);
            if (!WalletReadExecutor.ParseAddresses(own.Json).Contains(request.From, StringComparer.Ordinal))
                return new ToolExecutionResult("That address does not belong to the signed-in user. Use wallet.listAddresses to see the user's addresses.", true);

            return await RunAsync(context, mcpCtx, request, arguments, ct);
        }
        catch (McpAuthenticationException)
        {
            return new ToolExecutionResult("The sign-in has expired. Ask the user to sign in again.", true);
        }
        catch (McpToolUnavailableException)
        {
            return new ToolExecutionResult("The wallet service is temporarily unavailable. Tell the user to try again later.", true);
        }
        catch (BuilderException e)
        {
            return new ToolExecutionResult(e.PublicMessage + " Tell the user it could not be prepared; do not retry with guessed values.", true);
        }
        catch (LiquidityBuildException)
        {
            return new ToolExecutionResult("The transaction builder returned a group that does not match the request, so nothing was previewed.", true);
        }
        catch (HttpRequestException)
        {
            return new ToolExecutionResult("The transaction builder is temporarily unavailable. Tell the user to try again later.", true);
        }
    }

    /// <summary>
    /// The pool the user named, or the clarifying listing. A non-null <paramref name="refusal"/> is the tool result to
    /// return as is (a listing to ask about, or a reason the pool cannot be used).
    /// </summary>
    protected async Task<(PoolInfo? Pool, ToolExecutionResult? Refusal, int? AccountClass)> ResolvePoolAsync(LiquidityRequest r, string callAgain, CancellationToken ct)
    {
        if (r.PoolAppId == 0)
        {
            if (r.AssetA is null || r.AssetB is null)
                return (null, new ToolExecutionResult(
                    "No pool was named. Ask the user which two assets the pool is for (wallet.balance shows asset ids), then call this tool again with poolAppId 0, assetAId and assetBId. Do not guess a pool.", true), null);
            var listing = await liquidity.ListPoolsAsync(r.Network, r.AssetA, r.AssetB, null, r.From, ct);
            return (null, new ToolExecutionResult(DescribePools(r, listing, callAgain)), listing.AccountVerificationClass);
        }

        var one = await liquidity.ListPoolsAsync(r.Network, null, null, r.PoolAppId, r.From, ct);
        if (one.Pools.Count == 1) return (one.Pools[0], null, one.AccountVerificationClass);
        if (one.Excluded.Count > 0)
            return (null, new ToolExecutionResult(
                $"Pool {r.PoolAppId} is not open to this address: it needs Biatec verification class {one.Excluded[0].RequiredVerificationClass} and the address has class {one.AccountVerificationClass?.ToString(CultureInfo.InvariantCulture) ?? "none"}. " +
                "Explain that the address must be verified with Biatec to use this pool, and offer to look at other pools instead.", true), one.AccountVerificationClass);
        return (null, new ToolExecutionResult($"{r.PoolAppId} is not a Biatec DEX pool on {r.Network}. Ask the user which pool they mean.", true), one.AccountVerificationClass);
    }

    private static string DescribePools(LiquidityRequest r, PoolListing listing, string callAgain)
    {
        var o = new JsonObject
        {
            ["network"] = r.Network,
            ["pools"] = new JsonArray(listing.Pools.Select(p => (JsonNode?)new JsonObject
            {
                ["poolAppId"] = p.AppId.ToString(CultureInfo.InvariantCulture),
                ["assetA"] = p.AssetA.ToString(CultureInfo.InvariantCulture),
                ["assetB"] = p.AssetB.ToString(CultureInfo.InvariantCulture),
                ["feePercent"] = p.FeePercent,
                ["priceRangeOfAInB"] = p.PriceRange,
                ["currentPriceOfAInB"] = LiquidityFormat.Scaled(p.CurrentPrice),
                ["liquidity"] = p.Liquidity,
                ["minimumVerificationClass"] = p.VerificationClass,
            }).ToArray()),
        };
        if (listing.Excluded.Count > 0)
        {
            o["notAvailableToThisAddress"] = new JsonArray(listing.Excluded.Select(x => (JsonNode?)new JsonObject
            {
                ["poolAppId"] = x.AppId.ToString(CultureInfo.InvariantCulture),
                ["requiresVerificationClass"] = x.RequiredVerificationClass,
            }).ToArray());
            o["addressVerificationClass"] = listing.AccountVerificationClass;
        }
        o["instruction"] = listing.Pools.Count == 0
            ? "No pool of this pair is open to the address. Say so, and if notAvailableToThisAddress is present explain that the address must be verified with Biatec to use those pools."
            : "Nothing has been built. Ask the user which pool they want, listing each pool's id, fee, price range and liquidity, and do not choose for them, even if only one is listed. " + callAgain;
        return o.ToJsonString();
    }

    /// <summary>Symbols and decimals from the user's balances, best effort: a failure only means assets show by id.</summary>
    protected async Task<AssetBook> ReadAssetsAsync(McpCallContext ctx, string network, string address, CancellationToken ct)
    {
        try
        {
            var r = await mcp.GetCryptoBalanceAsync(ctx, McpNetworks.Algorand(network), address, ct);
            return r.Failed ? AssetBook.Unreadable : AssetBook.From(WalletBalanceExecutor.Shape(r.Json, r.Text));
        }
        catch (Exception e) when (e is McpToolUnavailableException)
        {
            return AssetBook.Unreadable;
        }
    }

    /// <summary>Persists the preview and returns the card plus a model-facing summary. Nothing is signed.</summary>
    protected async Task<ToolExecutionResult> CreatePreviewAsync(
        ToolExecutionContext context, LiquidityRequest r, LiquidityBuild b, string intent, IReadOnlyList<AssetAmount> assets, string title, JsonObject extraFacts, int? accountVerificationClass, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var expiresAt = Biatec.Chat.Core.Previews.PreviewPolicy.ExpiryFromLastValid(b.LastValid, b.BuiltAtRound, now);
        var stepNetwork = McpNetworks.Algorand(r.Network);
        var fee = b.Group.Transactions.Aggregate(BigInteger.Zero, (s, t) => s + t.Fee);
        var accountClass = accountVerificationClass?.ToString(CultureInfo.InvariantCulture) ?? "none";
        var summary = $"Biatec DEX pool {b.Pool.AppId} · fee tier {b.Pool.FeePercent}% · price range {b.Pool.PriceRange} · pool minimum verification class {b.Pool.VerificationClass} · your verification class {accountClass} · simulation passed";

        var canonical = new JsonObject
        {
            ["capability"] = CapabilityName,
            ["intent"] = intent,
            ["network"] = stepNetwork,
            ["sender"] = b.Sender,
            ["poolAppId"] = b.Pool.AppId.ToString(CultureInfo.InvariantCulture),
            ["feePercent"] = b.Pool.FeePercent,
            ["priceMin"] = b.Pool.PriceMin,
            ["priceMax"] = b.Pool.PriceMax,
            ["verificationClass"] = b.Pool.VerificationClass,
            ["accountVerificationClass"] = accountVerificationClass,
            ["groupHash"] = b.Group.GroupHash,
            ["txIds"] = new JsonArray(b.Group.TxIds.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["firstValid"] = b.FirstValid.ToString(CultureInfo.InvariantCulture),
            ["lastValid"] = b.LastValid.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (k, v) in extraFacts) canonical[k] = v?.DeepClone();

        await using var scope = scopes.CreateAsyncScope();
        var previews = scope.ServiceProvider.GetRequiredService<ActionPreviewService>();
        var snap = await previews.CreateAsync(new NewPreviewSpec(context.ConversationId, CapabilityName, JsonCanonicalizer.Canonicalize(canonical),
        [
            new NewStepSpec(ActionStepKind.Sign, b.Group.GroupHash, expiresAt, UnsignedGroup: JsonSerializer.Serialize(b.Group.ToBase64()),
                SenderAddress: b.Sender, Network: stepNetwork, FirstValid: (long)b.FirstValid, LastValid: (long)b.LastValid),
        ], PredictedNeedsConsent: false));

        var step = snap.Steps[0];
        var card = new ContractPreview
        {
            PreviewId = snap.Preview.Id.ToString(),
            Capability = CapabilityName,
            Intent = intent,
            Network = stepNetwork,
            GenesisId = b.Group.Transactions[0].GenesisId ?? "",
            Status = Contracts.ActionPreviewStatus.AwaitingConfirmation,
            ExpiresAt = snap.Preview.ExpiresAt,
            Steps =
            [
                new ContractStep
                {
                    StepId = step.Id.ToString(),
                    Index = 0,
                    Title = title,
                    Status = Contracts.ActionPreviewStatus.AwaitingConfirmation,
                    GroupHash = b.Group.GroupHash,
                    From = b.Sender,
                    To = $"Biatec DEX pool {b.Pool.AppId}",
                    Assets = assets,
                    FeeAmount = WalletBalanceExecutor.Format(fee.ToString(CultureInfo.InvariantCulture), 6) + " ALGO",
                    SimulationSummary = summary,
                },
            ],
        };

        var text = new StringBuilder("A preview card is now shown to the user. NOTHING has been signed or sent yet; the user must press Confirm on the card. ")
            .Append(intent).Append(". ").Append(summary).Append(". ")
            .Append("Describe it briefly and tell the user to review the card and confirm.").ToString();
        return new ToolExecutionResult(text, Events: [new PreviewCardEvent(card)]);
    }

    protected static AssetAmount Amount(AssetBook book, ulong assetId, string baseUnits)
    {
        var meta = book.Find(assetId);
        return new AssetAmount
        {
            AssetId = assetId == 0 ? "ALGO" : assetId.ToString(CultureInfo.InvariantCulture),
            Symbol = meta?.Symbol ?? (assetId == 0 ? "ALGO" : $"ASA {assetId}"),
            Amount = baseUnits,
            Decimals = meta?.Decimals ?? (assetId == 0 ? 6 : 0),
        };
    }

    protected static string Display(AssetAmount a) =>
        (a.Decimals > 0 ? WalletBalanceExecutor.Format(a.Amount, a.Decimals) : a.Amount) + " " + a.Symbol;
}

/// <summary>The arguments both capabilities share, already schema-checked by the gate.</summary>
public sealed record LiquidityRequest(string Network, string From, ulong PoolAppId, ulong? AssetA, ulong? AssetB)
{
    public static LiquidityRequest Parse(JsonNode a) => new(
        a["network"]!.GetValue<string>(), a["from"]!.GetValue<string>(), Id(a["poolAppId"]) ?? 0, Id(a["assetAId"]), Id(a["assetBId"]));

    /// <summary>Integer value of a node whatever number type it holds (<c>GetValue&lt;long&gt;</c> is strict about int versus long).</summary>
    internal static long AsLong(JsonNode n) => long.Parse(n.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    private static ulong? Id(JsonNode? n) => n is null ? null : (ulong)AsLong(n);
}

/// <summary>Symbol, decimals and balance per asset id, read from the user's balances.</summary>
public sealed class AssetBook
{
    public sealed record Meta(string? Symbol, int? Decimals, string? Amount);

    private readonly Dictionary<ulong, Meta> _byId = [];
    public static AssetBook Empty => new();
    /// <summary>The balance read failed or the tool was unavailable: the book says nothing about what is held.</summary>
    public static AssetBook Unreadable => new() { ReadFailed = true };
    public bool ReadFailed { get; private init; }

    public Meta? Find(ulong id) => _byId.GetValueOrDefault(id);

    public static AssetBook From(JsonArray balances)
    {
        var book = new AssetBook();
        foreach (var e in balances.OfType<JsonObject>())
        {
            var symbol = e["symbol"]?.ToString();
            ulong? id = ulong.TryParse(e["assetId"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v
                : string.Equals(symbol, "ALGO", StringComparison.OrdinalIgnoreCase) ? 0 : null;
            if (id is null) continue;
            book._byId[id.Value] = new Meta(symbol, e["decimals"] is { } d ? (int)LiquidityRequest.AsLong(d) : null, e["amount"]?.ToString());
        }
        return book;
    }
}

/// <summary><c>dex.addLiquidity</c>: deposit both assets of a pool, previewed and built from the pool's own state.</summary>
public sealed class DexAddLiquidityExecutor(ILiquidityBuilder liquidity, IBiatecMcpClient mcp, IServiceScopeFactory scopes, TimeProvider clock)
    : LiquidityExecutor(liquidity, mcp, scopes, clock)
{
    public override string CapabilityName => "dex.addLiquidity";
    public override string? StatusLabel(JsonNode arguments) => "Preparing the liquidity deposit";

    protected override async Task<ToolExecutionResult> RunAsync(ToolExecutionContext context, McpCallContext mcpCtx, LiquidityRequest r, JsonNode arguments, CancellationToken ct)
    {
        var (pool, refusal, accountClass) = await ResolvePoolAsync(r, "After the user chooses, call dex.addLiquidity again with that poolAppId and amountA/amountB in base units of that pool's assetA and assetB.", ct);
        if (refusal is not null) return refusal;

        var amountA = arguments["amountA"]!.GetValue<string>();
        var amountB = arguments["amountB"]!.GetValue<string>();
        var build = await Liquidity.BuildAddAsync(r.Network, r.From, pool!.AppId, amountA, amountB, ct);

        var book = await ReadAssetsAsync(mcpCtx, r.Network, r.From, ct);
        var a = Amount(book, build.Pool.AssetA, amountA);
        var b = Amount(book, build.Pool.AssetB, amountB);
        var intent = $"Add {Display(a)} and {Display(b)} to Biatec DEX pool {build.Pool.AppId} ({build.Pool.FeePercent}% fee, price range {build.Pool.PriceRange})";
        var extra = new JsonObject { ["amountA"] = amountA, ["amountB"] = amountB, ["assetA"] = a.AssetId, ["assetB"] = b.AssetId, ["lpOptIn"] = build.LpOptIn };
        return await CreatePreviewAsync(context, r, build, intent, [a, b], $"Add liquidity to pool {build.Pool.AppId}", extra, accountClass, ct);
    }
}

/// <summary><c>dex.removeLiquidity</c>: return a percentage of the user's LP tokens of one pool for their share of both assets.</summary>
public sealed class DexRemoveLiquidityExecutor(ILiquidityBuilder liquidity, IBiatecMcpClient mcp, IServiceScopeFactory scopes, TimeProvider clock)
    : LiquidityExecutor(liquidity, mcp, scopes, clock)
{
    public override string CapabilityName => "dex.removeLiquidity";
    public override string? StatusLabel(JsonNode arguments) => "Preparing the liquidity withdrawal";

    protected override async Task<ToolExecutionResult> RunAsync(ToolExecutionContext context, McpCallContext mcpCtx, LiquidityRequest r, JsonNode arguments, CancellationToken ct)
    {
        var (pool, refusal, accountClass) = await ResolvePoolAsync(r, "After the user chooses, call dex.removeLiquidity again with that poolAppId and the percent of their position to remove.", ct);
        if (refusal is not null) return refusal;

        var percent = LiquidityRequest.AsLong(arguments["percent"]!);
        var book = await ReadAssetsAsync(mcpCtx, r.Network, r.From, ct);
        if (book.ReadFailed)
            return new ToolExecutionResult("The balance could not be read. Tell the user to try again.", true);
        var held = book.Find(pool!.LpTokenId)?.Amount;
        if (!BigInteger.TryParse(held, NumberStyles.None, CultureInfo.InvariantCulture, out var balance) || balance.IsZero)
            return new ToolExecutionResult($"This address holds no liquidity tokens of pool {pool.AppId}, so there is nothing to remove. Tell the user.", true);

        var lp = balance * percent / 100;
        if (lp.IsZero)
            return new ToolExecutionResult("That percentage of the position rounds down to zero liquidity tokens. Ask the user for a larger percentage.", true);
        var lpAmount = lp.ToString(CultureInfo.InvariantCulture);

        var build = await Liquidity.BuildRemoveAsync(r.Network, r.From, pool.AppId, lpAmount, ct);
        var meta = book.Find(pool.LpTokenId);
        var lpAsset = new AssetAmount
        {
            AssetId = pool.LpTokenId.ToString(CultureInfo.InvariantCulture),
            Symbol = meta?.Symbol ?? $"LP of pool {pool.AppId}",
            Amount = lpAmount,
            Decimals = meta?.Decimals ?? 0,
        };
        var intent = $"Withdraw {percent}% of your position ({Display(lpAsset)}) from Biatec DEX pool {build.Pool.AppId} ({build.Pool.FeePercent}% fee, price range {build.Pool.PriceRange}); you receive your share of both pool assets";
        var extra = new JsonObject { ["percent"] = percent, ["lpTokenId"] = lpAsset.AssetId, ["lpAmount"] = lpAmount };
        return await CreatePreviewAsync(context, r, build, intent, [lpAsset], $"Remove liquidity from pool {build.Pool.AppId}", extra, accountClass, ct);
    }
}

public static class DexLiquidityRegistration
{
    /// <summary>
    /// Registers the liquidity executors when a builder sidecar is configured (<c>Builders:BaseAddress</c>); without
    /// one the capabilities have no executor and the orchestrator reports them unavailable. Needs AddBiatecMcp and AddConfirm.
    /// </summary>
    public static IServiceCollection AddDexLiquidityTools(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration[$"{BuildersServiceCollectionExtensions.Section}:BaseAddress"])) return services;
        services.AddBuildersClient(configuration);
        services.AddSingleton<ILiquidityBuilder, LiquidityBuilder>();
        services.AddSingleton<IToolExecutor, DexAddLiquidityExecutor>();
        services.AddSingleton<IToolExecutor, DexRemoveLiquidityExecutor>();
        return services;
    }
}
