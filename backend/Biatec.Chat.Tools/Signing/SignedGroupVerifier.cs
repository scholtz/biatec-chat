using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using Biatec.Chat.Tools.Algod;

namespace Biatec.Chat.Tools.Signing;

public sealed class SigningVerificationException(string message) : Exception(message);

/// <summary>
/// Checks what Biatec OIDC returned against what the user confirmed: the same transactions, byte for byte, in the
/// same order, carrying the previewed group hash. Nothing is submitted unless this passes.
/// </summary>
public static class SignedGroupVerifier
{
    /// <summary>Hash check of the unsigned group a step stores: it must still hash to the confirmed value.</summary>
    public static IReadOnlyList<Transaction> VerifyUnsigned(IEnumerable<string> unsignedBase64, string expectedGroupHash)
    {
        GroupedTransactions grouped;
        try { grouped = AlgorandGroup.Ensure(AlgorandGroup.Decode(unsignedBase64)); } // refuses a grp that is not the computed one
        catch (Exception ex) when (ex is GroupException or FormatException or InvalidOperationException or ArgumentException)
        {
            throw new SigningVerificationException("The stored group is not a valid transaction group.");
        }
        if (!string.Equals(grouped.GroupHash, expectedGroupHash, StringComparison.Ordinal))
            throw new SigningVerificationException("The stored group does not hash to the confirmed group hash.");
        return grouped.Transactions;
    }

    /// <summary>
    /// Returns the signed transactions' raw bytes (group order) after checking that each embeds exactly the previewed
    /// transaction, carries a signature, and that the group hash equals <paramref name="expectedGroupHash"/>.
    /// </summary>
    public static IReadOnlyList<byte[]> VerifySigned(IReadOnlyList<string> signedBase64, IReadOnlyList<Transaction> previewed, string expectedGroupHash)
    {
        if (signedBase64.Count != previewed.Count)
            throw new SigningVerificationException("The signed group does not have the previewed number of transactions.");
        var raw = new List<byte[]>();
        var embedded = new List<Transaction>();
        for (var i = 0; i < signedBase64.Count; i++)
        {
            byte[] bytes;
            SignedTransaction signed;
            try
            {
                bytes = Convert.FromBase64String(signedBase64[i]);
                signed = Encoder.DecodeFromMsgPack<SignedTransaction>(bytes);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or IOException)
            {
                throw new SigningVerificationException("A signed transaction could not be decoded.");
            }
            if (signed?.Tx is null) throw new SigningVerificationException("A signed transaction has no transaction.");
            if (signed.Sig is null && signed.MSig is null && signed.LSig is null)
                throw new SigningVerificationException("A returned transaction carries no signature.");
            if (!Encoder.EncodeToMsgPackOrdered(signed.Tx).AsSpan().SequenceEqual(Encoder.EncodeToMsgPackOrdered(previewed[i])))
                throw new SigningVerificationException("A signed transaction differs from the previewed one.");
            raw.Add(bytes);
            embedded.Add(signed.Tx);
        }
        if (!string.Equals(AlgorandGroup.ComputeGroupHash(embedded), expectedGroupHash, StringComparison.Ordinal))
            throw new SigningVerificationException("The signed group does not hash to the previewed group hash.");
        return raw;
    }

    /// <summary>The wire form for submission: the signed transactions' msgpack concatenated in group order, base64.</summary>
    public static string ToSubmissionBlob(IReadOnlyList<byte[]> signed) => Convert.ToBase64String(signed.SelectMany(b => b).ToArray());
}
