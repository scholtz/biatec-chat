using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Biatec.Chat.Api.Contracts;

namespace Biatec.Chat.Api.Chat.DataCards;

/// <summary>
/// Builds the holdings <see cref="DataCardEvent"/> from the structured <c>wallet.balance</c> tool result. Never from model
/// text. All or nothing: when any entry cannot be shown exactly (no decimals, bad amount) or the asset list was truncated,
/// there is no card and the model's prose answers alone, so a card is never partial or approximate.
/// </summary>
public static class HoldingsCardBuilder
{
    public const string Kind = "holdings";
    public const string Capability = "wallet.balance";

    /// <summary>A card is built only from an executor-produced, successful wallet.balance result of a signed-in session.</summary>
    public static bool ShouldBuild(Biatec.Chat.Core.Data.SessionTier tier, string capability, bool fromExecutor, bool isError) =>
        fromExecutor && !isError && tier == Biatec.Chat.Core.Data.SessionTier.Authenticated && capability == Capability;

    public static DataCardEvent? TryBuild(string toolResultJson, DateTimeOffset asOf)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(toolResultJson).RootElement.Clone(); }
        catch (JsonException) { return null; }
        if (root.ValueKind != JsonValueKind.Object) return null;

        var network = Str(root, "network");
        var address = Str(root, "address");
        if (network is null || address is null) return null;
        if (root.TryGetProperty("assetsTruncated", out var t) && t.ValueKind == JsonValueKind.True) return null;
        if (!root.TryGetProperty("balances", out var balances) || balances.ValueKind != JsonValueKind.Array) return null;

        var assets = new List<DataCardAsset>();
        foreach (var e in balances.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            var amount = Str(e, "display");
            var raw = Str(e, "amount");
            if (amount is null || raw is null || !BigInteger.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return null;
            if (!e.TryGetProperty("decimals", out var d) || d.ValueKind != JsonValueKind.Number || !d.TryGetInt32(out var decimals) || decimals is < 0 or > 19) return null;
            var symbol = Str(e, "symbol");
            var id = Str(e, "assetId") ?? symbol;
            if (id is null) return null;
            assets.Add(new DataCardAsset { AssetId = id, UnitName = symbol ?? "#" + id, Amount = amount, Decimals = decimals });
        }
        if (assets.Count == 0) return null;

        return new DataCardEvent
        {
            Kind = Kind,
            Network = network,
            Addresses = [address],
            Assets = assets,
            AsOf = asOf,
        };
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
}
