using System.Text.Json.Nodes;
using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Core.Capabilities;

/// <summary>Single source of truth for what the model may call (PRODUCT-DESCRIPTION.md, Capabilities And Intents).</summary>
public sealed class CapabilityRegistry
{
    public const string OpenId = "openid";
    public const string Sign = "sign";
    public const string ManageLimits = "manage-limits";
    public const string RequestSignIn = "requestSignIn";
    public const string KbSearch = "kb.search";

    private readonly Dictionary<string, Capability> _byName;
    private readonly List<Capability> _ordered = [];

    public CapabilityRegistry(IEnumerable<Capability> capabilities)
    {
        _byName = new Dictionary<string, Capability>(StringComparer.Ordinal);
        foreach (var c in capabilities)
        {
            if (!_byName.TryAdd(c.Name, c))
                throw new ArgumentException($"Duplicate capability '{c.Name}'.");
            if (c.InputSchema is not null) JsonSchemaValidator.AssertSupported(c.InputSchema, c.Name);
            _ordered.Add(c);
        }
    }

    public IReadOnlyCollection<Capability> All => _ordered;

    public Capability? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Tool-bearing capabilities visible to this context, in registry order.</summary>
    public IReadOnlyList<Capability> ToolsFor(GateContext ctx) =>
        _ordered.Where(c => c.ToolName is not null && Visibility(c, ctx) is null).ToList();

    /// <summary>Null when the capability is visible/callable for the context, otherwise a machine-readable reason.</summary>
    internal static string? Visibility(Capability c, GateContext ctx)
    {
        if (c.Flag == CapabilityFlag.X402 && !ctx.X402Enabled) return "feature_disabled";
        if (c.Flag == CapabilityFlag.LimitsUpdate && !ctx.LimitsUpdateEnabled) return "feature_disabled";
        if (ctx.Tier < c.MinTier) return "tier_too_low";
        foreach (var s in c.BuildScopes)
            if (!ctx.Scopes.Contains(s, StringComparer.Ordinal)) return "scope_missing";
        return null;
    }

    private static JsonObject Obj(JsonObject props, params string[] required)
    {
        var req = new JsonArray();
        foreach (var r in required) req.Add(r);
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = req,
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject WithMinProperties(JsonObject o, int n)
    {
        o["minProperties"] = n;
        return o;
    }

    private static JsonObject Str(int? max = null, int min = 1, string? pattern = null)
    {
        var o = new JsonObject { ["type"] = "string", ["minLength"] = min };
        if (max is not null) o["maxLength"] = max;
        if (pattern is not null) o["pattern"] = pattern;
        return o;
    }

    private static readonly string[] Open = [OpenId];
    private static readonly string[] NoScopes = [];

    private static JsonObject Network() => new() { ["type"] = "string", ["enum"] = new JsonArray("mainnet", "testnet") };
    private static JsonObject Address() => Str(max: 64, pattern: @"\A[A-Za-z0-9]+\z");
    // Canonical non-negative integer string (no leading zeros); AmountPositive additionally excludes zero.
    private static JsonObject Amount() => Str(max: 20, pattern: @"\A(0|[1-9][0-9]*)\z");
    private static JsonObject AmountPositive() => Str(max: 20, pattern: @"\A[1-9][0-9]*\z");
    private static JsonObject Id() => new() { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 9007199254740991 };

    /// <summary>The initial capability set from the product description.</summary>
    public static CapabilityRegistry CreateDefault()
    {
        var empty = Obj(new JsonObject());
        var networkOnly = Obj(new JsonObject { ["network"] = Network() }, "network");
        var transferInput = Obj(new JsonObject
        {
            ["network"] = Network(),
            ["from"] = Address(),
            ["to"] = Address(),
            ["assetId"] = Id(),
            ["amount"] = AmountPositive(),
        }, "network", "from", "to", "assetId", "amount");
        var swapInput = Obj(new JsonObject
        {
            ["network"] = Network(),
            ["from"] = Address(),
            ["fromAssetId"] = Id(),
            ["toAssetId"] = Id(),
            ["amount"] = AmountPositive(),
            ["poolAppId"] = Id(),
        }, "network", "from", "fromAssetId", "toAssetId", "amount");
        var liquidityInput = Obj(new JsonObject
        {
            ["network"] = Network(),
            ["from"] = Address(),
            ["poolAppId"] = Id(),
            ["amountA"] = AmountPositive(),
            ["amountB"] = AmountPositive(),
        }, "network", "from", "poolAppId", "amountA", "amountB");
        var removeLiquidityInput = Obj(new JsonObject
        {
            ["network"] = Network(),
            ["from"] = Address(),
            ["poolAppId"] = Id(),
            ["percent"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 },
        }, "network", "from", "poolAppId", "percent");
        var proposalInput = Obj(new JsonObject
        {
            ["network"] = Network(),
            ["safeAppId"] = Id(),
            ["proposalId"] = Id(),
            ["expectedPayloadVersion"] = Id(),
        }, "network", "safeAppId", "proposalId");

        static Capability Tool(string name, SessionTier tier, string[] build, string? sign, bool funds, ValuationBasis v,
            string desc, JsonObject? schema, CapabilityFlag flag = CapabilityFlag.None) =>
            new(name, 1, tier, build, sign, funds, v, desc, schema?.DeepClone().AsObject(), flag);

        const SessionTier A = SessionTier.Anonymous;
        const SessionTier U = SessionTier.Authenticated;
        return new CapabilityRegistry(
        [
            Tool(RequestSignIn, A, NoScopes, null, false, ValuationBasis.None, "Render the sign-in card.",
                Obj(new JsonObject { ["reason"] = Str(max: 500, min: 0) })),
            Tool(KbSearch, A, NoScopes, null, false, ValuationBasis.None, "Search the curated Biatec knowledge base.",
                Obj(new JsonObject { ["query"] = Str(max: 500) }, "query")),
            Tool("explain.product", A, NoScopes, null, false, ValuationBasis.None, "Explain Biatec products (no tool of its own).", null),
            Tool("explain.blockchain", A, NoScopes, null, false, ValuationBasis.None, "Explain Algorand and DEX concepts (no tool of its own).", null),
            Tool("wallet.listAddresses", U, Open, null, false, ValuationBasis.None, "List the user's Algorand addresses.", networkOnly),
            Tool("wallet.balance", U, Open, null, false, ValuationBasis.None, "Read a balance for one of the user's addresses.",
                Obj(new JsonObject { ["network"] = Network(), ["address"] = Address() }, "network", "address")),
            Tool("wallet.transfer", U, Open, Sign, true, ValuationBasis.DecodedByOidc, "Build and preview a transfer.", transferInput),
            Tool("dex.swap", U, Open, Sign, true, ValuationBasis.DecodedByOidc, "Quote and build a swap.", swapInput),
            Tool("dex.addLiquidity", U, Open, Sign, true, ValuationBasis.DecodedByOidc, "Build an add-liquidity group.", liquidityInput),
            Tool("dex.removeLiquidity", U, Open, Sign, true, ValuationBasis.DecodedByOidc, "Build a remove-liquidity group.", removeLiquidityInput),
            Tool("safe.create", U, Open, Sign, false, ValuationBasis.ZeroValued, "Deploy a safe.",
                Obj(new JsonObject { ["network"] = Network(), ["from"] = Address() }, "network", "from")),
            Tool("safe.propose", U, Open, Sign, false, ValuationBasis.ZeroValued, "Create a safe proposal.",
                Obj(new JsonObject { ["network"] = Network(), ["from"] = Address(), ["safeAppId"] = Id(), ["kind"] = Str(max: 64) }, "network", "from", "safeAppId", "kind")),
            Tool("safe.approve", U, Open, Sign, true, ValuationBasis.DecodedByOidc, "Approve a safe proposal.", proposalInput),
            Tool("safe.execute", U, Open, Sign, true, ValuationBasis.ZeroValued, "Execute a safe proposal.", proposalInput),
            Tool("safe.list", U, Open, null, false, ValuationBasis.None, "List the user's safes and pending proposals.", networkOnly),
            Tool("limits.view", U, Open, null, false, ValuationBasis.None, "View spending limits.", empty),
            Tool("limits.update", U, [ManageLimits], null, false, ValuationBasis.LimitsChange, "Update spending limits.",
                WithMinProperties(Obj(new JsonObject { ["daily"] = Amount(), ["weekly"] = Amount(), ["monthly"] = Amount() }), 1),
                CapabilityFlag.LimitsUpdate),
            Tool("x402.pay", U, Open, Sign, true, ValuationBasis.DecodedByOidc, "Pay for an x402-protected resource.",
                Obj(new JsonObject { ["network"] = Network(), ["from"] = Address(), ["resourceUrl"] = Str(max: 2048, pattern: @"\Ahttps://[^\s]+\z") }, "network", "from", "resourceUrl"),
                CapabilityFlag.X402),
        ]);
    }
}
