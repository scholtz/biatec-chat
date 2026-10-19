using Algorand.Algod.Model.Transactions;
using Biatec.Chat.Tools.Algod;

namespace Biatec.Chat.Tools.Dex.Swap;

/// <summary>
/// Checks that a group returned by a builder or by Biatec MCP does what the swap card says before the card is built:
/// it moves exactly the requested asset and amount out of the user's account, targets the chosen pool (pool path),
/// and does not rekey the account, close anything out, claw back, or create/update/delete an application.
/// </summary>
public static class SwapGroupVerifier
{
    private const string ZeroAddress = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAY5HFKQ";

    /// <exception cref="GroupException">The group differs from the request.</exception>
    public static void Verify(IReadOnlyList<Transaction> txs, string from, ulong fromAssetId, ulong amount, ulong? poolAppId)
    {
        var funded = false;
        var calledPool = poolAppId is null;
        var poolAddress = poolAppId is { } id ? Algorand.Address.ForApplication(id).EncodeAsString() : null;
        foreach (var tx in txs)
        {
            if (IsSet(tx.RekeyTo)) throw new GroupException("A transaction in the swap rekeys an account.");
            switch (tx)
            {
                case PaymentTransaction pay:
                    if (IsSet(pay.CloseRemainderTo)) throw new GroupException("A payment in the swap closes out an account.");
                    if (!Same(pay.Sender, from)) break;
                    if (fromAssetId == 0 && pay.Amount == amount && !funded && ReceiverOk(pay.Receiver, poolAddress)) funded = true;
                    else throw new GroupException("The swap contains a payment from the user that is not the requested funding transfer.");
                    break;
                case AssetClawbackTransaction or AssetCloseTransaction:
                    throw new GroupException("An asset transfer in the swap is a clawback or a close-out.");
                case AssetTransferTransaction axfer:
                    if (!Same(axfer.Sender, from)) break;
                    if (fromAssetId != 0 && axfer.XferAsset == fromAssetId && axfer.AssetAmount == amount && !funded && ReceiverOk(axfer.AssetReceiver, poolAddress)) funded = true;
                    else throw new GroupException("The swap contains an asset transfer from the user that is not the requested funding transfer.");
                    break;
                case var _ when tx.GetType().Name.Contains("KeyReg", StringComparison.OrdinalIgnoreCase)
                    || tx.GetType().Name.Contains("KeyRegistration", StringComparison.OrdinalIgnoreCase):
                    throw new GroupException("The swap contains a key registration.");
                case ApplicationCreateTransaction or ApplicationUpdateTransaction or ApplicationDeleteTransaction
                    or ApplicationCloseOutTransaction or ApplicationClearStateTransaction:
                    throw new GroupException("The swap contains an application call a swap never needs.");
                case ApplicationCallTransaction:
                    if (poolAppId is { } pool && AppId(tx) == pool) calledPool = true;
                    break;
            }
        }
        if (!funded) throw new GroupException("The swap does not send the requested asset and amount from the user's account.");
        if (!calledPool) throw new GroupException("The swap does not call the chosen pool.");
    }

    /// <summary>On the pool path the funding transfer must go to the pool application's address.</summary>
    private static bool ReceiverOk(Algorand.Address? receiver, string? poolAddress) =>
        poolAddress is null || (receiver is not null && string.Equals(receiver.EncodeAsString(), poolAddress, StringComparison.Ordinal));

    // The SDK declares ApplicationId on the concrete call types, not on the abstract base.
    private static ulong? AppId(Transaction tx) => tx.GetType().GetProperty("ApplicationId")?.GetValue(tx) as ulong?;

    private static bool Same(Algorand.Address a, string b) => string.Equals(a.EncodeAsString(), b, StringComparison.Ordinal);

    private static bool IsSet(Algorand.Address? a) => a is not null && a.EncodeAsString() != ZeroAddress;
}
