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
        if (c.IsFundMoving)
        {
            if (ctx.Paused) return "agent_paused";
            if (ctx.Mode == AgentMode.Analyze) return "agent_analyze_only";
        }
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
    // Algorand or Bitcoin; the Bitcoin names are the ones wallet.transfer uses.
    private static JsonObject NetworkWithBitcoin() => new() { ["type"] = "string", ["enum"] = new JsonArray("mainnet", "testnet", "bitcoin", "bitcoin-testnet") };
    private static JsonObject Address() => Str(max: 64, pattern: @"\A[A-Za-z0-9]+\z");
    // Canonical non-negative integer string (no leading zeros); AmountPositive additionally excludes zero.
    private static JsonObject Amount() => Str(max: 20, pattern: @"\A(0|[1-9][0-9]*)\z");
    private static JsonObject AmountPositive() => Str(max: 20, pattern: @"\A[1-9][0-9]*\z");
    private static JsonObject Id() => new() { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 9007199254740991 };

    /// <summary>The initial capability set from the product description.</summary>
    public static CapabilityRegistry CreateDefault()
    {
        var empty = Obj(new JsonObject());
        var networkOnly = Obj(new JsonObject { ["network"] = NetworkWithBitcoin() }, "network");
        var transferInput = Obj(new JsonObject
        {
            // Algorand (createPaymentTransaction) or Bitcoin (createBitcoinTransaction); assetId omitted or 0 = native ALGO.
            ["network"] = NetworkWithBitcoin(),
            ["from"] = Address(),
            ["to"] = Address(),
            ["assetId"] = Id(),
            ["amount"] = AmountPositive(),
        }, "network", "from", "to", "amount");
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
            // 0 = the user has not named a pool: pass assetAId and assetBId and the tool lists the pair's pools to ask about.
            ["poolAppId"] = Id(),
            ["assetAId"] = Id(),
            ["assetBId"] = Id(),
            ["amountA"] = AmountPositive(),
            ["amountB"] = AmountPositive(),
        }, "network", "from", "poolAppId", "amountA", "amountB");
        var removeLiquidityInput = Obj(new JsonObject
        {
            ["network"] = Network(),
            ["from"] = Address(),
            ["poolAppId"] = Id(),
            ["assetAId"] = Id(),
            ["assetBId"] = Id(),
            ["percent"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 },
        }, "network", "from", "poolAppId", "percent");
        // from: the user's own address that signs (a member of the proposal's signer group for an approval; anyone may execute).
        var proposalInput = Obj(new JsonObject
        {
            ["network"] = Network(),
            ["from"] = Address(),
            ["safeAppId"] = Id(),
            ["proposalId"] = Id(),
            ["expectedPayloadVersion"] = Id(),
        }, "network", "from", "safeAppId", "proposalId");
        var executeInput = Obj(new JsonObject
        {
            ["network"] = Network(),
            ["from"] = Address(),
            ["safeAppId"] = Id(),
            ["proposalId"] = Id(),
        }, "network", "from", "safeAppId", "proposalId");
        var proposeInput = Obj(new JsonObject
        {
            ["network"] = Network(),
            ["from"] = Address(),
            ["safeAppId"] = Id(),
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("payment", "assetTransfer", "optIn", "addLiquidity", "admin") },
            ["groupId"] = Id(),
            ["receiver"] = Address(),
            ["assetId"] = Id(),
            ["amount"] = AmountPositive(),
            ["assetIds"] = new JsonObject { ["type"] = "array", ["items"] = Id(), ["minItems"] = 1, ["maxItems"] = 16 },
            ["poolAppId"] = Id(),
            ["assetAId"] = Id(),
            ["assetBId"] = Id(),
            ["amountA"] = Amount(),
            ["amountB"] = Amount(),
            ["change"] = Obj(new JsonObject
            {
                ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("addMember", "removeMember", "changeThreshold", "createGroup") },
                ["groupId"] = Id(),
                ["member"] = Address(),
                ["threshold"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 15 },
                ["name"] = Str(max: 32),
                ["members"] = new JsonObject { ["type"] = "array", ["items"] = Address(), ["minItems"] = 1, ["maxItems"] = 15 },
            }, "type"),
        }, "network", "from", "safeAppId", "kind");

        static Capability Tool(string name, SessionTier tier, string[] build, string? sign, bool funds, ValuationBasis v,
            string desc, JsonObject? schema, CapabilityFlag flag = CapabilityFlag.None) =>
            new(name, 1, tier, build, sign, funds, v, desc, schema?.DeepClone().AsObject(), flag);

        // Evidence redaction: no shipped capability sets OutputSchema or marks a field "sensitive". Tool results are public
        // on-chain data (the user's own addresses and balances, previews) or text, never secrets or tokens; the generic
        // pattern redactor still runs. If a tool ever returns private data, set OutputSchema with "sensitive": true there.
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
            Tool("wallet.listAddresses", U, Open, null, false, ValuationBasis.None, "List the user's own addresses: Algorand on mainnet/testnet, Bitcoin on bitcoin/bitcoin-testnet (each Bitcoin address is labelled primary or secondary).", networkOnly),
            Tool("wallet.balance", U, Open, null, false, ValuationBasis.None, "Read a balance for one of the user's addresses (on Bitcoin: amount in sats, 8 decimals).",
                Obj(new JsonObject { ["network"] = NetworkWithBitcoin(), ["address"] = Address() }, "network", "address")),
            Tool("wallet.transfer", U, Open, Sign, true, ValuationBasis.DecodedByOidc, "Build and preview a transfer.", transferInput),
            Tool("dex.swap", U, Open, Sign, true, ValuationBasis.DecodedByOidc, "Quote and build a swap.", swapInput),
            Tool("dex.addLiquidity", U, Open, Sign, true, ValuationBasis.DecodedByOidc, "Add liquidity to a Biatec DEX pool (amountA/amountB are in base units of the pool's asset A/B). Set poolAppId 0 with assetAId and assetBId when the user has not named a pool: the tool lists the pair's pools and you must ask which one.", liquidityInput),
            Tool("dex.removeLiquidity", U, Open, Sign, true, ValuationBasis.DecodedByOidc, "Remove liquidity (percent of the user's LP tokens) from a Biatec DEX pool. Set poolAppId 0 with assetAId and assetBId when the user has not named a pool: the tool lists the pair's pools and you must ask which one.", removeLiquidityInput),
            Tool("safe.create", U, Open, Sign, false, ValuationBasis.ZeroValued,
                "Create a Biatec Safe (multi-signature shared account) deployed from one of the user's addresses (from). signers are the OTHER members' Algorand addresses exactly as the user pasted them (never invent or complete one); the creator is included automatically. threshold is how many approvals a proposal needs. Call without name, signers or threshold to learn what to ask. The creation is two confirmations (deploy, then signer-group setup) with a separate wallet.transfer funding the safe in between; call again with the same arguments after each step lands.",
                Obj(new JsonObject
                {
                    ["network"] = Network(),
                    ["from"] = Address(),
                    ["name"] = Str(max: 32),
                    ["signers"] = new JsonObject { ["type"] = "array", ["items"] = Address(), ["minItems"] = 0, ["maxItems"] = 14 },
                    ["threshold"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 15 },
                }, "network", "from")),
            Tool("safe.propose", U, Open, Sign, false, ValuationBasis.ZeroValued,
                "Propose a transaction or change on a Biatec Safe, proposed by from (one of the user's addresses, a member of the safe). kind payment: receiver and amount (microAlgo). kind assetTransfer: receiver, assetId, amount (base units). kind optIn: assetIds the safe should opt in to (zero-amount transfers to itself). kind addLiquidity: poolAppId (0 with assetAId and assetBId to list the pools and ask the user), amountA/amountB in base units of the pool's asset A/B (omit to deposit everything the safe holds). kind admin: change {type addMember|removeMember (groupId, member), changeThreshold (groupId, threshold), createGroup (name, members, threshold)}. groupId optionally picks the proposing signer group. Proposing moves nothing and records the proposer's approval; funding the safe is a separate wallet.transfer.",
                proposeInput),
            Tool("safe.approve", U, Open, Sign, true, ValuationBasis.DecodedByOidc,
                "Approve a pending safe proposal from the user's own member address (from). expectedPayloadVersion: the payload version the user reviewed (from safe.list); the approval fails if the payload changed.",
                proposalInput),
            Tool("safe.execute", U, Open, Sign, true, ValuationBasis.ZeroValued,
                "Execute a safe proposal that has reached its approval threshold; from is the user's address that sends the call and pays its fee.", executeInput),
            Tool("safe.list", U, Open, null, false, ValuationBasis.None,
                "List the Biatec Safes the user is a member of, with signer groups, pending proposals, approval progress, payload versions and expiry rounds. safeAppIds: app ids of safes the user named (optional; safes created through this chat are found without them).",
                Obj(new JsonObject
                {
                    ["network"] = Network(),
                    ["safeAppIds"] = new JsonObject { ["type"] = "array", ["items"] = Id(), ["maxItems"] = 10 },
                }, "network")),
            Tool("limits.view", U, Open, null, false, ValuationBasis.None, "View spending limits.", empty),
            // Visible at openid so the model can call it; the manage-limits elevation is enforced by CheckSigningScope (re-authorize).
            Tool("limits.update", U, Open, ManageLimits, false, ValuationBasis.LimitsChange, "Update spending limits.",
                WithMinProperties(Obj(new JsonObject { ["daily"] = Amount(), ["weekly"] = Amount(), ["monthly"] = Amount() }), 1),
                CapabilityFlag.LimitsUpdate),
            Tool("x402.pay", U, Open, Sign, true, ValuationBasis.DecodedByOidc, "Pay for an x402-protected resource.",
                Obj(new JsonObject { ["network"] = Network(), ["from"] = Address(), ["resourceUrl"] = Str(max: 2048, pattern: @"\Ahttps://[^\s]+\z") }, "network", "from", "resourceUrl"),
                CapabilityFlag.X402),
        ]);
    }
}
