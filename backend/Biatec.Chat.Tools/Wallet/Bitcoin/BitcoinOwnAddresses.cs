using System.Text.Json;
using Biatec.Chat.Tools.Mcp;

namespace Biatec.Chat.Tools.Wallet.Bitcoin;

/// <summary>One of the signed-in user's Bitcoin addresses: getCryptoAddress derivation slot, "primary" or "secondary", address.</summary>
public sealed record OwnBitcoinAddress(int Slot, string Label, string Address);

/// <summary>
/// The user's own Bitcoin addresses, shared by wallet.listAddresses, wallet.balance and the wallet.transfer ownership check.
/// Biatec MCP <c>getCryptoAddress</c> returns one <c>{network, family, address, error}</c> per derivation slot of the
/// primary seed: slot 0 is the primary address, slot 1 the secondary.
/// </summary>
public static class BitcoinOwnAddresses
{
    private static readonly (int Slot, string Label)[] Slots = [(0, "primary"), (1, "secondary")];

    /// <summary>
    /// Null when the primary cannot be read. A secondary that cannot be read (any failure, including a client that cannot
    /// pass a slot), or that equals the primary (a server ignoring the slot), is left out.
    /// </summary>
    public static async Task<IReadOnlyList<OwnBitcoinAddress>?> ReadAsync(IBiatecMcpClient mcp, McpCallContext ctx, string network, CancellationToken ct)
    {
        var result = new List<OwnBitcoinAddress>();
        foreach (var (slot, label) in Slots)
        {
            string? address;
            if (slot == 0) address = AddressOf(await mcp.GetCryptoAddressAsync(ctx, network, slot, ct));
            else
            {
                try { address = AddressOf(await mcp.GetCryptoAddressAsync(ctx, network, slot, ct)); }
                catch (Exception e) when (e is not OperationCanceledException) { address = null; }
            }
            if (address is null)
            {
                if (slot == 0) return null;
                continue;
            }
            if (result.Any(a => SameAddress(a.Address, address))) continue;
            result.Add(new OwnBitcoinAddress(slot, label, address));
        }
        return result;
    }

    /// <summary>The own address matching <paramref name="address"/> (bech32 compared case-insensitively), or null.</summary>
    public static OwnBitcoinAddress? Find(IReadOnlyList<OwnBitcoinAddress> own, string address) =>
        own.FirstOrDefault(a => SameAddress(a.Address, address));

    /// <summary>Bech32/Bech32m addresses are case-insensitive (lowercase is canonical); Base58 addresses are case-sensitive.</summary>
    public static string Normalize(string address) =>
        address.StartsWith("bc1", StringComparison.OrdinalIgnoreCase) || address.StartsWith("tb1", StringComparison.OrdinalIgnoreCase)
            ? address.ToLowerInvariant()
            : address;

    public static bool SameAddress(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    /// <summary>The <c>address</c> field, or else the first address-shaped string anywhere in the result.</summary>
    private static string? AddressOf(McpToolResult r)
    {
        if (r.Failed) return null;
        if (r.Json is { ValueKind: JsonValueKind.Object } o)
            foreach (var p in o.EnumerateObject())
                if (string.Equals(p.Name, "address", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    return string.IsNullOrWhiteSpace(p.Value.GetString()) ? null : p.Value.GetString();
        return BitcoinTransferService.OwnAddresses(r).FirstOrDefault();
    }
}
