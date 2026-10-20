using System.Globalization;
using System.Net;
using System.Text.Json;
using Biatec.Chat.Api.Chat.Confirm;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Tools.Wallet.Bitcoin;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Wallet.Transfer;

/// <summary>
/// Configuration "Bitcoin": an Esplora-compatible HTTP API (mempool.space, blockstream.info or a self-hosted one) per
/// network, used to notice that a pending Bitcoin preview's input was spent or the fee rate moved. If the endpoint is
/// unreachable only the 30 minute expiry applies. No credentials are involved.
/// </summary>
public sealed class BitcoinOptions
{
    public const string Section = "Bitcoin";

    public Dictionary<string, string> Esplora { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bitcoin"] = "https://mempool.space/api",
        ["bitcoin-testnet"] = "https://mempool.space/testnet4/api",
    };
}

/// <summary>Esplora implementation of <see cref="IBitcoinChainWatcher"/>. A transport failure reads as "cannot tell" (not spent, no fee rate).</summary>
public sealed class EsploraBitcoinChainWatcher(HttpClient http, IOptions<BitcoinOptions> options) : IBitcoinChainWatcher
{
    private Uri? Base(string network) =>
        options.Value.Esplora.TryGetValue(network, out var u) && Uri.TryCreate(u.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback) ? uri : null;

    public async Task<bool> IsSpentAsync(string network, string txid, uint vout, CancellationToken ct)
    {
        if (Base(network) is not { } b || txid.Length != 64 || !txid.All(Uri.IsHexDigit)) return false;
        try
        {
            using var res = await http.GetAsync(new Uri(b, $"tx/{txid}/outspend/{vout.ToString(CultureInfo.InvariantCulture)}"), ct);
            if (res.StatusCode == HttpStatusCode.NotFound) return true; // unknown to the chain
            if (!res.IsSuccessStatusCode) return false;
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.TryGetProperty("spent", out var spent) && spent.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or JsonException or TaskCanceledException) { return false; }
    }

    public async Task<decimal?> GetFeeRateAsync(string network, CancellationToken ct)
    {
        if (Base(network) is not { } b) return null;
        try
        {
            using var res = await http.GetAsync(new Uri(b, "v1/fees/recommended"), ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.TryGetProperty("halfHourFee", out var f) && f.TryGetDecimal(out var d) && d > 0 ? d : null;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or JsonException or TaskCanceledException) { return null; }
    }
}

/// <summary>Runs the Bitcoin preview checks (30 minutes, spent input, fee rate move) at Confirm, before consent is requested.</summary>
public sealed class BitcoinPreviewFreshnessGate(BitcoinTransferService bitcoin) : IPreviewFreshnessGate
{
    public async Task<string?> CheckAsync(ActionPreview preview, ActionStep pendingStep, CancellationToken ct)
    {
        if (preview.Capability != "wallet.transfer" || pendingStep.Network is not { } net || !net.StartsWith("bitcoin", StringComparison.Ordinal)) return null;
        return await bitcoin.CheckAsync(preview.ConversationId, preview.Id, ct) switch
        {
            BitcoinInvalidation.None => null,
            BitcoinInvalidation.TimeExpired => "The preview has expired. Nothing was signed; ask for the transfer again to get a fresh preview.",
            BitcoinInvalidation.InputSpent => "An input of this transaction has been spent. Nothing was signed; ask for the transfer again to get a fresh preview.",
            _ => "The network fee rate has moved by more than 25 percent. Nothing was signed; ask for the transfer again to get a fresh preview.",
        };
    }
}
