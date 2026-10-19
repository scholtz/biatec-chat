using System.Globalization;
using System.Text.Json.Nodes;
using Biatec.Chat.Tools.Wallet.Bitcoin;

namespace Biatec.Chat.Api.Wallet.Transfer;

/// <summary>Argument parsing and the preview card for <c>wallet.transfer</c> on Bitcoin.</summary>
public static class BitcoinTransferCard
{
    private static readonly System.Text.RegularExpressions.Regex AddressShape = new(@"\A(bc1|tb1|BC1|TB1|[13mn2])[A-Za-z0-9]{25,89}\z");

    /// <summary>The amount argument is whole satoshis, as the capability schema's integer string.</summary>
    public static BitcoinTransferRequest? ParseRequest(JsonNode arguments, out string? problem)
    {
        problem = null;
        var network = (string?)arguments["network"];
        var from = (string?)arguments["from"];
        var to = (string?)arguments["to"];
        if (network is not ("bitcoin" or "bitcoin-testnet") || from is null || to is null || !AddressShape.IsMatch(from) || !AddressShape.IsMatch(to))
        {
            problem = "The from and to values must be valid Bitcoin addresses.";
            return null;
        }
        if (arguments["assetId"] is { } a && a.GetValue<long>() != 0)
        {
            problem = "A Bitcoin transfer has no asset ID.";
            return null;
        }
        var amount = (string?)arguments["amount"];
        if (amount is null || !long.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out var sats) || sats <= 0)
        {
            problem = "The amount must be a positive whole number of satoshis.";
            return null;
        }
        return new BitcoinTransferRequest(network, from, to, sats);
    }

    public static Contracts.ActionPreview Map(BitcoinOutcome o)
    {
        var snap = o.Snapshot!;
        var r = o.Request!;
        var intent = o.Intent!;
        var step = snap.Steps.OrderBy(s => s.Seq).Last();
        var change = BitcoinTransferService.Change(intent, r);
        var rate = intent.FeeRate();

        var inputs = string.Join("; ", intent.Inputs.Select(i => $"{Short(i.Address ?? i.ScriptPubKey)} {Btc(i.Sats)} ({Short(i.Txid)}:{i.Vout})"));
        var outputs = string.Join("; ", intent.Outputs.Select(x => $"{Short(x.Address ?? x.ScriptPubKey)} {Btc(x.Sats)}" + (x == change ? " (change)" : "")));
        var summary = $"Inputs: {inputs}. Outputs: {outputs}. " +
                      (change is null ? "No change output. " : $"Change: {Btc(change.Sats)} BTC back to {Short(change.Address ?? "")}. ") +
                      $"Fee: {intent.FeeSats.ToString(CultureInfo.InvariantCulture)} sats, about {rate.ToString("0.##", CultureInfo.InvariantCulture)} sat/vB (estimated).";

        return new Contracts.ActionPreview
        {
            PreviewId = snap.Preview.Id.ToString(),
            Capability = "wallet.transfer",
            Intent = $"Send {Btc(r.Sats)} BTC from {Short(r.From)} to {Short(r.To)}",
            Network = r.Network,
            GenesisId = r.Testnet ? "bitcoin-testnet" : "bitcoin-mainnet",
            Status = TransferCard.PreviewState(snap.Preview.Status),
            ExpectsAuthenticatorApproval = snap.Preview.PredictedNeedsConsent,
            ExpiresAt = snap.Preview.ExpiresAt,
            Steps =
            [
                new Contracts.ActionStep
                {
                    StepId = step.Id.ToString(), Index = 0, Title = "Send Bitcoin", Status = TransferCard.StepState(step.Status), GroupHash = step.SubjectHash,
                    From = r.From, To = r.To,
                    Assets = [new Contracts.AssetAmount { AssetId = "BTC", Symbol = "BTC", Amount = r.Sats.ToString(CultureInfo.InvariantCulture), Decimals = 8 }],
                    FeeAmount = intent.FeeSats.ToString(CultureInfo.InvariantCulture),
                    SimulationSummary = summary,
                    TransactionId = step.TxIds.Length > 0 ? step.TxIds[0] : null,
                },
            ],
        };
    }

    private static string Btc(long sats) => BitcoinTransferService.SatsToBtc(sats);
    private static string Short(string a) => a.Length <= 12 ? a : a[..6] + "..." + a[^6..];
}
