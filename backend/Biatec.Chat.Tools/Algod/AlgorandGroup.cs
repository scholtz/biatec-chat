using Algorand;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;

namespace Biatec.Chat.Tools.Algod;

public sealed class GroupException(string message) : Exception(message);

/// <summary>An unsigned transaction group together with its binding group ID.</summary>
/// <param name="Transactions">The transactions, every one carrying <c>grp</c>.</param>
/// <param name="GroupHash">The protocol group ID as lowercase hex (64 characters). This is the preview's group hash.</param>
/// <param name="TxIds">Transaction IDs in group order.</param>
public sealed record GroupedTransactions(IReadOnlyList<Transaction> Transactions, string GroupHash, IReadOnlyList<string> TxIds)
{
    /// <summary>The unsigned transactions as base64 msgpack, in group order (the step's <c>UnsignedGroup</c> JSON array).</summary>
    public IReadOnlyList<string> ToBase64() =>
        Transactions.Select(t => Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(t))).ToList();

    /// <summary>Highest <c>FirstValid</c> and lowest <c>LastValid</c>: the window in which the whole group is valid.</summary>
    public (ulong FirstValid, ulong LastValid) ValidityWindow() =>
        (Transactions.Max(t => t.FirstValid), Transactions.Min(t => t.LastValid));
}

/// <summary>
/// Algorand group ID handling using the Algorand .NET SDK, the same implementation Biatec OIDC uses, so the hash
/// is byte-identical to what it recomputes. The backend never assembles a group itself: builders return
/// transactions, and this only computes or assigns the group ID.
/// </summary>
public static class AlgorandGroup
{
    /// <summary>The protocol group ID of the transactions, as lowercase hex. Any existing <c>grp</c> is ignored.</summary>
    public static string ComputeGroupHash(IReadOnlyList<Transaction> transactions)
    {
        Validate(transactions);
        return Convert.ToHexStringLower(TxGroup.ComputeGroupID(WithoutGroup(transactions)).Bytes);
    }

    /// <summary>
    /// Makes the transactions a group that carries its ID. A lone transaction without <c>grp</c> gets one
    /// assigned (the SDK helper changes no other field); a multi-transaction group that arrives with no
    /// <c>grp</c> is assigned too. Transactions that already carry a <c>grp</c> must carry the one the SDK
    /// computes, else this throws rather than relabel the group.
    /// </summary>
    public static GroupedTransactions Ensure(IReadOnlyList<Transaction> transactions)
    {
        Validate(transactions);
        var withGroup = transactions.Count(HasGroup);
        Transaction[] result;
        if (withGroup == 0)
        {
            result = TxGroup.AssignGroupID(WithoutGroup(transactions));
        }
        else if (withGroup == transactions.Count)
        {
            var expected = TxGroup.ComputeGroupID(WithoutGroup(transactions));
            if (transactions.Any(t => !t.Group!.Bytes.AsSpan().SequenceEqual(expected.Bytes)))
                throw new GroupException("A transaction carries a grp that does not match the computed group ID.");
            result = transactions.ToArray();
        }
        else
        {
            throw new GroupException("Some transactions carry a grp and some do not.");
        }

        var hash = Convert.ToHexStringLower(result[0].Group!.Bytes);
        return new GroupedTransactions(result, hash, result.Select(t => t.TxID()).ToList());
    }

    /// <summary>Decodes base64 msgpack unsigned transactions (a step's <c>UnsignedGroup</c>).</summary>
    public static IReadOnlyList<Transaction> Decode(IEnumerable<string> base64) =>
        base64.Select(Transaction.FromBase64String).ToList();

    private static bool HasGroup(Transaction t) => t.Group is { } g && g.Bytes.Any(b => b != 0);

    private static void Validate(IReadOnlyList<Transaction> transactions)
    {
        if (transactions.Count == 0) throw new GroupException("A group needs at least one transaction.");
        if (transactions.Count > TxGroup.MAX_TX_GROUP_SIZE)
            throw new GroupException($"A group holds at most {TxGroup.MAX_TX_GROUP_SIZE} transactions.");
    }

    /// <summary>Copies of the transactions with <c>grp</c> cleared, so computing never depends on a stale value.</summary>
    private static Transaction[] WithoutGroup(IReadOnlyList<Transaction> transactions) =>
        transactions.Select(t =>
        {
            var copy = Encoder.DecodeFromMsgPack<Transaction>(Encoder.EncodeToMsgPackOrdered(t));
            copy.Group = null!;
            return copy;
        }).ToArray();
}
