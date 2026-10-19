using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Tools.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Biatec.Chat.Api.Wallet.Read;

/// <summary>Shared guards and MCP plumbing for the read-only wallet executors. The ToolGate has already checked tier, scope and schema; tier is re-checked here as defence in depth.</summary>
public abstract class WalletReadExecutor(IBiatecMcpClient mcp) : IToolExecutor
{
    public abstract string CapabilityName { get; }
    public abstract string? StatusLabel(JsonNode arguments);
    protected abstract Task<ToolExecutionResult> RunAsync(McpCallContext mcpCtx, JsonNode arguments, CancellationToken ct);

    protected IBiatecMcpClient Mcp => mcp;

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, JsonNode arguments, CancellationToken ct)
    {
        if (context.Tier != SessionTier.Authenticated)
            return new ToolExecutionResult("This needs a signed-in user. Call requestSignIn instead.", true);
        try
        {
            return await RunAsync(new McpCallContext(context.TenantId, context.SessionId, context.Scopes), arguments, ct);
        }
        catch (McpAuthenticationException)
        {
            return new ToolExecutionResult("The sign-in has expired. Ask the user to sign in again.", true);
        }
        catch (McpToolUnavailableException)
        {
            return new ToolExecutionResult("The wallet service is temporarily unavailable. Tell the user to try again later.", true);
        }
    }

    protected static string? Network(JsonNode arguments) => arguments["network"]?.GetValue<string>();

    /// <summary>Addresses from <c>listAlgorandAddresses</c>: tolerates a bare array, an object with an array, strings or objects.</summary>
    internal static List<string> ParseAddresses(JsonElement? json)
    {
        var result = new List<string>();
        if (json is not { } root) return result;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "addresses", "accounts", "result", "items" })
                if (TryGet(root, key, out var inner) && inner.ValueKind == JsonValueKind.Array) { root = inner; break; }
        }
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

    internal static bool TryGet(JsonElement o, string name, out JsonElement value)
    {
        if (o.ValueKind == JsonValueKind.Object)
            foreach (var p in o.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }
}

/// <summary><c>wallet.listAddresses</c>: the user's own Algorand addresses via Biatec MCP <c>listAlgorandAddresses</c>.</summary>
public sealed class WalletListAddressesExecutor(IBiatecMcpClient mcp) : WalletReadExecutor(mcp)
{
    public override string CapabilityName => "wallet.listAddresses";
    public override string? StatusLabel(JsonNode arguments) => "Listing your addresses";

    protected override async Task<ToolExecutionResult> RunAsync(McpCallContext mcpCtx, JsonNode arguments, CancellationToken ct)
    {
        var r = await Mcp.ListAlgorandAddressesAsync(mcpCtx, ct);
        if (r.IsError) return new ToolExecutionResult("The addresses could not be read. Tell the user to try again.", true);
        var addresses = ParseAddresses(r.Json);
        var o = new JsonObject
        {
            ["network"] = Network(arguments),
            ["count"] = addresses.Count,
            ["addresses"] = new JsonArray(addresses.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
        };
        return new ToolExecutionResult(o.ToJsonString());
    }
}

/// <summary><c>wallet.balance</c>: balance of one of the user's own addresses via Biatec MCP <c>getCryptoBalance</c>.</summary>
public sealed class WalletBalanceExecutor(IBiatecMcpClient mcp) : WalletReadExecutor(mcp)
{
    public override string CapabilityName => "wallet.balance";
    public override string? StatusLabel(JsonNode arguments) => "Reading your balance";

    protected override async Task<ToolExecutionResult> RunAsync(McpCallContext mcpCtx, JsonNode arguments, CancellationToken ct)
    {
        var network = Network(arguments)!;
        var address = arguments["address"]!.GetValue<string>();

        // Only the user's own addresses may be read through this tool.
        var own = await Mcp.ListAlgorandAddressesAsync(mcpCtx, ct);
        if (own.IsError) return new ToolExecutionResult("The addresses could not be read. Tell the user to try again.", true);
        if (!ParseAddresses(own.Json).Contains(address, StringComparer.Ordinal))
            return new ToolExecutionResult("That address does not belong to the signed-in user. Use wallet.listAddresses to see the user's addresses.", true);

        var r = await Mcp.GetCryptoBalanceAsync(mcpCtx, network, address, ct);
        if (r.IsError) return new ToolExecutionResult("The balance could not be read. Tell the user to try again.", true);

        var o = new JsonObject { ["network"] = network, ["address"] = address, ["balances"] = Shape(r.Json, r.Text) };
        return new ToolExecutionResult(o.ToJsonString());
    }

    /// <summary>Normalises the MCP balance into entries of base-unit amount, decimals and a decimal display string.</summary>
    internal static JsonArray Shape(JsonElement? json, string rawText)
    {
        var arr = new JsonArray();
        if (json is not { } root)
        {
            arr.Add(new JsonObject { ["raw"] = rawText.Length > 2000 ? rawText[..2000] : rawText });
            return arr;
        }
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "balances", "assets", "holdings" })
                if (WalletReadExecutor.TryGet(root, key, out var inner) && inner.ValueKind == JsonValueKind.Array) { root = inner; break; }

        if (root.ValueKind == JsonValueKind.Array)
            foreach (var e in root.EnumerateArray()) arr.Add(Entry(e));
        else
            arr.Add(Entry(root));
        return arr;
    }

    private static JsonObject Entry(JsonElement e)
    {
        var o = new JsonObject();
        if (e.ValueKind != JsonValueKind.Object)
        {
            o["amount"] = e.ToString();
            return o;
        }
        string? Str(params string[] names)
        {
            foreach (var n in names)
                if (WalletReadExecutor.TryGet(e, n, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number) return v.ToString();
            return null;
        }

        var asset = Str("assetId", "asset_id", "asset", "id");
        var symbol = Str("symbol", "unitName", "unit_name", "name");
        var amount = Str("amount", "balance", "microAlgos", "value");
        var decimalsText = Str("decimals", "decimal");
        if (asset is not null) o["assetId"] = asset;
        if (symbol is not null) o["symbol"] = symbol;
        // Native ALGO (asset 0 or none given) has 6 decimals when the server does not say.
        if (decimalsText is null && (asset is null || asset == "0")) decimalsText = "6";
        o["amount"] = amount;
        if (int.TryParse(decimalsText, NumberStyles.None, CultureInfo.InvariantCulture, out var decimals) && decimals is >= 0 and <= 19)
        {
            o["decimals"] = decimals;
            if (amount is not null && Format(amount, decimals) is { } display) o["display"] = display;
        }
        else
        {
            // Pass through anything else the server reported so the model can still answer.
            foreach (var p in e.EnumerateObject())
                if (!o.ContainsKey(p.Name)) o[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        }
        return o;
    }

    /// <summary>Base units to a decimal string, for example 1234567 with 6 decimals is "1.234567". Null when the amount is not a non-negative integer.</summary>
    internal static string? Format(string baseUnits, int decimals)
    {
        if (!BigInteger.TryParse(baseUnits, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return null;
        var s = v.ToString(CultureInfo.InvariantCulture);
        if (decimals == 0) return s;
        s = s.PadLeft(decimals + 1, '0');
        return s[..^decimals] + "." + s[^decimals..];
    }
}

public static class WalletReadRegistration
{
    /// <summary>Registers the wallet read executors with the orchestrator. Needs <see cref="IBiatecMcpClient"/> (AddBiatecMcp).</summary>
    public static IServiceCollection AddWalletReadTools(this IServiceCollection services)
    {
        services.AddSingleton<IToolExecutor, WalletListAddressesExecutor>();
        services.AddSingleton<IToolExecutor, WalletBalanceExecutor>();
        return services;
    }
}
