using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Biatec.Chat.Tools.Wallet.Transfer;

public enum TransferStepKind { Fund, OptIn, Transfer }

/// <summary>What the user asked for. <see cref="Amount"/> is in base units; <see cref="AssetId"/> 0 is native ALGO.</summary>
public sealed record TransferRequest(string Network, string From, string To, ulong AssetId, string Amount)
{
    /// <summary>The network name stored on steps and sent to Biatec OIDC, for example <c>algorand-testnet</c>.</summary>
    public string StepNetwork => "algorand-" + Network;

    public bool SameAs(TransferRequest o) =>
        Network == o.Network && From == o.From && To == o.To && AssetId == o.AssetId && Amount == o.Amount;
}

/// <summary>One planned transaction group (always a single transaction). Only the first is built at first.</summary>
public sealed record TransferStepPlan(TransferStepKind Kind, string Sender, string Receiver, ulong AssetId, string Amount, string Title);

/// <summary>An entry of a <c>getCryptoBalance</c> result.</summary>
public sealed record BalanceEntry(ulong AssetId, string? Symbol, string Amount, int? Decimals);

/// <summary>Tolerant readers for Biatec MCP wallet results.</summary>
public static class McpWalletJson
{
    public static List<string> Addresses(JsonElement? json)
    {
        var result = new List<string>();
        if (json is not { } root) return result;
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "addresses", "accounts", "result", "items" })
                if (TryGet(root, key, out var inner) && inner.ValueKind == JsonValueKind.Array) { root = inner; break; }
        if (root.ValueKind != JsonValueKind.Array) return result;
        foreach (var e in root.EnumerateArray())
        {
            string? a = null;
            if (e.ValueKind == JsonValueKind.String) a = e.GetString();
            else if (TryGet(e, "address", out var v) && v.ValueKind == JsonValueKind.String) a = v.GetString();
            if (!string.IsNullOrWhiteSpace(a)) result.Add(a);
        }
        return result;
    }

    public static List<BalanceEntry> Balances(JsonElement? json)
    {
        var list = new List<BalanceEntry>();
        if (json is not { } root) return list;
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "balances", "assets", "holdings" })
                if (TryGet(root, key, out var inner) && inner.ValueKind == JsonValueKind.Array) { root = inner; break; }
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        foreach (var e in items)
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var asset = Str(e, "assetId", "asset_id", "asset", "id");
            var symbol = Str(e, "symbol", "unitName", "unit_name", "name");
            var amount = Str(e, "amount", "balance", "microAlgos", "value");
            var dec = Str(e, "decimals", "decimal");
            ulong id = 0;
            if (asset is not null) { if (!ulong.TryParse(asset, NumberStyles.None, CultureInfo.InvariantCulture, out id)) continue; }
            else if (symbol is not null && !string.Equals(symbol, "ALGO", StringComparison.OrdinalIgnoreCase)) continue; // unidentifiable asset
            int? decimals = int.TryParse(dec, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 0 and <= 19 ? d : null;
            if (id == 0) decimals ??= 6;
            if (amount is null || !BigInteger.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out _)) amount = "0";
            list.Add(new BalanceEntry(id, id == 0 ? "ALGO" : symbol, amount, decimals));
        }
        return list;
    }

    public static string? Str(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (TryGet(e, n, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number) return v.ToString();
        return null;
    }

    public static bool TryGet(JsonElement o, string name, out JsonElement value)
    {
        if (o.ValueKind == JsonValueKind.Object)
            foreach (var p in o.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }
}

/// <summary>
/// Decides the steps of a transfer. A transfer is one step, except an ASA transfer to the user's own address that
/// has not opted in: then an opt-in from that address precedes it, and a small ALGO payment from the source address
/// precedes the opt-in when that address lacks the ALGO for the asset's minimum balance and the fee.
/// </summary>
public static class TransferPlanner
{
    /// <summary>The receiver is funded when it holds less than this (0.1 ALGO per asset plus fees and a cushion).</summary>
    public const ulong MinReceiverMicroAlgos = 300_000;

    /// <summary>The ALGO sent from the source address to fund the opt-in.</summary>
    public const ulong FundingMicroAlgos = 300_000;

    public static IReadOnlyList<TransferStepPlan> Plan(TransferRequest r, bool receiverIsOwn, bool receiverOptedIn, BigInteger receiverMicroAlgos, string symbol)
    {
        var steps = new List<TransferStepPlan>();
        var needsOptIn = r.AssetId != 0 && receiverIsOwn && !receiverOptedIn && !string.Equals(r.From, r.To, StringComparison.Ordinal);
        if (needsOptIn && receiverMicroAlgos < MinReceiverMicroAlgos)
            steps.Add(new TransferStepPlan(TransferStepKind.Fund, r.From, r.To, 0, FundingMicroAlgos.ToString(CultureInfo.InvariantCulture),
                "Send a little ALGO to your receiving address to cover the opt-in"));
        if (needsOptIn)
            steps.Add(new TransferStepPlan(TransferStepKind.OptIn, r.To, r.To, r.AssetId, "0", $"Opt your receiving address in to {symbol}"));
        steps.Add(new TransferStepPlan(TransferStepKind.Transfer, r.From, r.To, r.AssetId, r.Amount, $"Send {symbol}"));
        return steps;
    }

    public static string Kind(TransferStepKind k) => k switch { TransferStepKind.Fund => "fund", TransferStepKind.OptIn => "optin", _ => "transfer" };

    public static TransferStepKind ParseKind(string? s) => s switch { "fund" => TransferStepKind.Fund, "optin" => TransferStepKind.OptIn, _ => TransferStepKind.Transfer };

    /// <summary>The preview's canonical JSON: the intent and the full step plan, bound by the preview's steps.</summary>
    public static JsonObject ToJson(TransferRequest r, string symbol, int decimals, IReadOnlyList<TransferStepPlan> plan) => new()
    {
        ["capability"] = "wallet.transfer",
        ["network"] = r.Network,
        ["from"] = r.From,
        ["to"] = r.To,
        ["assetId"] = (long)r.AssetId,
        ["amount"] = r.Amount,
        ["symbol"] = symbol,
        ["decimals"] = decimals,
        ["totalSteps"] = plan.Count,
        ["steps"] = new JsonArray(plan.Select(p => (JsonNode?)new JsonObject
        {
            ["kind"] = Kind(p.Kind), ["sender"] = p.Sender, ["receiver"] = p.Receiver, ["assetId"] = (long)p.AssetId, ["amount"] = p.Amount, ["title"] = p.Title,
        }).ToArray()),
    };

    public static (TransferRequest Request, string Symbol, int Decimals, IReadOnlyList<TransferStepPlan> Plan)? FromJson(string canonicalJson)
    {
        try
        {
            var o = JsonNode.Parse(canonicalJson)!.AsObject();
            var r = new TransferRequest((string)o["network"]!, (string)o["from"]!, (string)o["to"]!, (ulong)(long)o["assetId"]!, (string)o["amount"]!);
            var plan = o["steps"]!.AsArray().Select(s => new TransferStepPlan(ParseKind((string?)s!["kind"]), (string)s["sender"]!, (string)s["receiver"]!,
                (ulong)(long)s["assetId"]!, (string)s["amount"]!, (string)s["title"]!)).ToList();
            return (r, (string)o["symbol"]!, (int)o["decimals"]!, plan);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Base units to a decimal string, for example 1500000 with 6 decimals is "1.5".</summary>
    public static string FormatAmount(string baseUnits, int decimals)
    {
        if (!BigInteger.TryParse(baseUnits, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return baseUnits;
        var s = v.ToString(CultureInfo.InvariantCulture);
        if (decimals == 0) return s;
        s = s.PadLeft(decimals + 1, '0');
        var frac = s[^decimals..].TrimEnd('0');
        return frac.Length == 0 ? s[..^decimals] : s[..^decimals] + "." + frac;
    }
}
