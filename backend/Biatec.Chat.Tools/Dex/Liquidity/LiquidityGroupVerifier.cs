using Algorand.Algod.Model.Transactions;
using Biatec.Chat.Tools.Algod;
using Biatec.Chat.Tools.Safe.Proposals;

namespace Biatec.Chat.Tools.Dex.Liquidity;

/// <summary>
/// Checks that a liquidity group returned by the builder sidecar does what the preview card says before the card is
/// built (spec 7.2: builders are verified independently). Every transaction is the user's; none rekeys, closes out,
/// claws back, registers keys or creates/updates/deletes an application; deposits (add) or the LP return (remove) go
/// only to the pool application's address with exactly the requested amounts; application calls are plain calls to the
/// pool or the network's pool-provider application; the only other transfers are zero-amount opt-ins to the user.
/// </summary>
public static class LiquidityGroupVerifier
{
    private const string ZeroAddress = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAY5HFKQ";

    /// <exception cref="GroupException">The group differs from the request.</exception>
    public static void VerifyAdd(IReadOnlyList<string> base64, IReadOnlyList<Transaction> txs, string network, string user, PoolInfo pool, ulong amountA, ulong amountB) =>
        Verify(base64, txs, network, user, pool, [(pool.AssetA, amountA), (pool.AssetB, amountB)], add: true);

    /// <exception cref="GroupException">The group differs from the request.</exception>
    public static void VerifyRemove(IReadOnlyList<string> base64, IReadOnlyList<Transaction> txs, string network, string user, PoolInfo pool, ulong lpAmount) =>
        Verify(base64, txs, network, user, pool, [(pool.LpTokenId, lpAmount)], add: false);

    private static void Verify(IReadOnlyList<string> base64, IReadOnlyList<Transaction> txs, string network, string user, PoolInfo pool, (ulong Asset, ulong Amount)[] expected, bool add)
    {
        // The SDK model has no field for some close-outs, so decoding could drop them: re-encoding must give back the builder's bytes.
        if (base64.Count != txs.Count) throw new GroupException("The builder group could not be verified.");
        for (var i = 0; i < txs.Count; i++)
            if (!Algorand.Utils.Encoder.EncodeToMsgPackOrdered(txs[i]).AsSpan().SequenceEqual(Convert.FromBase64String(base64[i])))
                throw new GroupException("A liquidity transaction contains fields that cannot be verified.");

        // The Biatec app ids come from the backend's own table, never from the builder's facts.
        var apps = BiatecDexApps.For(network) ?? throw new GroupException("No Biatec DEX deployment is known for this network.");
        var poolAddress = Algorand.Address.ForApplication(pool.AppId).EncodeAsString();
        var remaining = expected.ToList();
        int poolCalls = 0, providerCalls = 0;
        var optedIn = new HashSet<ulong>();

        foreach (var tx in txs)
        {
            if (!Same(tx.Sender, user)) throw new GroupException("The liquidity group has a transaction from a different sender.");
            if (IsSet(tx.RekeyTo)) throw new GroupException("A transaction in the liquidity group rekeys an account.");
            switch (tx)
            {
                case PaymentTransaction pay:
                    if (IsSet(pay.CloseRemainderTo)) throw new GroupException("A payment in the liquidity group closes out an account.");
                    Deposit(remaining, 0, pay.Amount ?? 0, pay.Receiver, poolAddress);
                    break;
                case AssetClawbackTransaction or AssetCloseTransaction:
                    throw new GroupException("An asset transfer in the liquidity group is a clawback or a close-out.");
                case AssetAcceptTransaction accept:
                    // Opt-in: only an add may opt the user in, and only to the LP token (the builder library never opts in to anything else).
                    if (!Same(accept.AssetReceiver, user) || accept.AssetAmount != 0
                        || !add || accept.XferAsset != pool.LpTokenId
                        || !optedIn.Add(accept.XferAsset))
                        throw new GroupException("The liquidity group opts in to something other than the pool's assets.");
                    break;
                case AssetTransferTransaction axfer:
                    Deposit(remaining, axfer.XferAsset, axfer.AssetAmount, axfer.AssetReceiver, poolAddress);
                    break;
                case ApplicationNoopTransaction call:
                    var app = call.ApplicationId;
                    var first = call.ApplicationArgs?.FirstOrDefault();
                    if (app == pool.AppId)
                    {
                        // Only the add call's method is known here; a remove call is bound by the LP transfer it pairs with.
                        if (add && (first is null || !first.AsSpan().SequenceEqual(BiatecDexApps.AddLiquidity)))
                            throw new GroupException("The pool call is not an addLiquidity call.");
                        poolCalls++;
                    }
                    else if (app == apps.PoolProviderAppId)
                    {
                        if (first is null || !first.AsSpan().SequenceEqual(BiatecDexApps.ProviderNoop))
                            throw new GroupException("The pool-provider call is not the provider's noop.");
                        providerCalls++;
                    }
                    else throw new GroupException("The liquidity group calls an application other than the pool.");
                    break;
                default:
                    throw new GroupException("The liquidity group contains a transaction a liquidity change never needs.");
            }
        }
        if (remaining.Count != 0) throw new GroupException("The liquidity group does not move the requested assets and amounts.");
        if (providerCalls > 1) throw new GroupException("The liquidity group calls the pool provider more than once.");
        if (poolCalls != 1) throw new GroupException("The liquidity group does not call the chosen pool exactly once.");
    }

    /// <summary>A transfer out of the user's account must be one of the expected (asset, amount) pairs, paid to the pool.</summary>
    private static void Deposit(List<(ulong Asset, ulong Amount)> remaining, ulong asset, ulong amount, Algorand.Address? receiver, string poolAddress)
    {
        if (receiver is null || !string.Equals(receiver.EncodeAsString(), poolAddress, StringComparison.Ordinal))
            throw new GroupException("The liquidity group pays someone other than the pool.");
        var i = remaining.FindIndex(r => r.Asset == asset && r.Amount == amount);
        if (i < 0) throw new GroupException("The liquidity group moves a different asset or amount than was requested.");
        remaining.RemoveAt(i);
    }

    private static bool Same(Algorand.Address a, string b) => string.Equals(a.EncodeAsString(), b, StringComparison.Ordinal);

    private static bool IsSet(Algorand.Address? a) => a is not null && a.EncodeAsString() != ZeroAddress;
}
