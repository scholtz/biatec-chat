using System.Security.Cryptography;
using System.Text;
using Algorand;
using Algorand.Algod.Model.Transactions;
using Encoder = Algorand.Utils.Encoder;
using Biatec.Chat.Tools.Algod;
using Org.BouncyCastle.Crypto.Digests;

namespace Biatec.Chat.Tests.Previews;

public sealed class AlgorandGroupTests
{
    private const string AddrA = "UPYAFLHSIPMJOHVXU2MPLQ46GXJKSDCEMZ6RLCQ7GWB5PRDKJUWKKXECXI";
    private const string AddrB = "KV2XGKMXGYJ6PWYQA5374BYIQBL3ONRMSIARPCFCJEAMAHQEVYPB7PL3KU";

    private static PaymentTransaction Pay(ulong amount, ulong firstValid = 710399, string note = "n") => new()
    {
        Sender = new Address(AddrA),
        Receiver = new Address(AddrB),
        Amount = amount,
        Fee = 1000,
        FirstValid = firstValid,
        LastValid = firstValid + 1000,
        GenesisId = "devnet-v1.0",
        GenesisHash = new Digest(Convert.FromBase64String("sC3P7e2SdbqKJK0tbiCdK9tdSpbe6XeCGKdoNzmlj0E=")),
        Note = Encoding.UTF8.GetBytes(note),
    };

    private static List<Transaction> Group(int n) =>
        Enumerable.Range(0, n).Select(i => (Transaction)Pay(2000 + (ulong)i, 710399 + (ulong)i, "note" + i)).ToList();

    // Independent reference: SHA-512/256 (BouncyCastle, not the SDK) over "TG" + msgpack({"txlist": [bin32...]}),
    // where each element is SHA-512/256("TX" + msgpack(txn without grp)).
    private static byte[] Sha512_256(byte[] prefix, byte[] data)
    {
        var d = new Sha512tDigest(256);
        d.BlockUpdate(prefix, 0, prefix.Length);
        d.BlockUpdate(data, 0, data.Length);
        var o = new byte[32];
        d.DoFinal(o, 0);
        return o;
    }

    private static string ReferenceGroupId(IReadOnlyList<Transaction> txns)
    {
        var body = new List<byte> { 0x81, 0xa6 };
        body.AddRange(Encoding.ASCII.GetBytes("txlist"));
        if (txns.Count < 16) body.Add((byte)(0x90 | txns.Count)); else { body.Add(0xdc); body.Add(0); body.Add((byte)txns.Count); }
        foreach (var t in txns)
        {
            var copy = Encoder.DecodeFromMsgPack<Transaction>(Encoder.EncodeToMsgPackOrdered(t));
            copy.Group = null!;
            var txid = Sha512_256(Encoding.ASCII.GetBytes("TX"), Encoder.EncodeToMsgPackOrdered(copy));
            body.Add(0xc4); body.Add(0x20); body.AddRange(txid);
        }
        return Convert.ToHexStringLower(Sha512_256(Encoding.ASCII.GetBytes("TG"), body.ToArray()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(16)]
    public void GroupHash_EqualsSdkGroupId_AndTheIndependentReference(int n)
    {
        var txns = Group(n);
        var sdk = Convert.ToHexStringLower(TxGroup.ComputeGroupID(txns.ToArray()).Bytes);
        Assert.Equal(sdk, AlgorandGroup.ComputeGroupHash(txns));
        Assert.Equal(ReferenceGroupId(txns), AlgorandGroup.ComputeGroupHash(txns));
        Assert.Equal(64, sdk.Length);
    }

    [Fact]
    public void GroupHash_PinnedVectors()
    {
        // Pinned regression vectors; each was also matched against ReferenceGroupId above.
        Assert.Equal(GoldenTwo, AlgorandGroup.ComputeGroupHash(Group(2)));
        Assert.Equal(GoldenOne, AlgorandGroup.ComputeGroupHash(Group(1)));
    }

    private const string GoldenOne = "a098f1b2e4b12f0d64d8700cbd4acd0b6090c018051cc56a29664d04e760229e";
    private const string GoldenTwo = "8755ded8950a21d59ec9739a39cb55af9bef4b087449fd72f5fc97c25f92cf1a";

    [Fact]
    public void GroupHash_IsOrderSensitive_AndIgnoresAStaleGrp()
    {
        var txns = Group(3);
        var reversed = Enumerable.Reverse(txns).ToList();
        Assert.NotEqual(AlgorandGroup.ComputeGroupHash(txns), AlgorandGroup.ComputeGroupHash(reversed));

        var grouped = AlgorandGroup.Ensure(txns);
        Assert.Equal(AlgorandGroup.ComputeGroupHash(txns), AlgorandGroup.ComputeGroupHash(grouped.Transactions));
    }

    [Fact]
    public void Ensure_LoneTransaction_GetsGrp_AndNoOtherFieldChanges()
    {
        var lone = Pay(5000);
        var before = Encoder.DecodeFromMsgPack<Transaction>(Encoder.EncodeToMsgPackOrdered(lone));

        var grouped = AlgorandGroup.Ensure([lone]);

        var after = Assert.Single(grouped.Transactions);
        Assert.NotNull(after.Group);
        Assert.Equal(grouped.GroupHash, Convert.ToHexStringLower(after.Group!.Bytes));
        Assert.Equal(AlgorandGroup.ComputeGroupHash([lone]), grouped.GroupHash);
        Assert.Equal(ReferenceGroupId([lone]), grouped.GroupHash);

        // Clearing grp must give back byte-identical encoding of the original: nothing else changed.
        var stripped = Encoder.DecodeFromMsgPack<Transaction>(Encoder.EncodeToMsgPackOrdered(after));
        stripped.Group = null!;
        Assert.Equal(Encoder.EncodeToMsgPackOrdered(before), Encoder.EncodeToMsgPackOrdered(stripped));
        // The caller's own transaction object is not mutated.
        Assert.False(lone.Group is { } g && g.Bytes.Any(b => b != 0));
    }

    [Fact]
    public void Ensure_MultiTransactionGroup_AssignsSameGrpToEveryTransaction()
    {
        var grouped = AlgorandGroup.Ensure(Group(3));
        Assert.All(grouped.Transactions, t => Assert.Equal(grouped.GroupHash, Convert.ToHexStringLower(t.Group!.Bytes)));
        Assert.Equal(3, grouped.TxIds.Count);
    }

    [Fact]
    public void Ensure_AlreadyGrouped_IsAcceptedWhenCorrect_AndRefusedWhenRelabelled()
    {
        var grouped = AlgorandGroup.Ensure(Group(2));
        var again = AlgorandGroup.Ensure(grouped.Transactions);
        Assert.Equal(grouped.GroupHash, again.GroupHash);

        var other = AlgorandGroup.Ensure(Group(3));
        var forged = new List<Transaction> { grouped.Transactions[0], grouped.Transactions[1] };
        forged[1] = Encoder.DecodeFromMsgPack<Transaction>(Encoder.EncodeToMsgPackOrdered(forged[1]));
        forged[1].Group = other.Transactions[0].Group!;
        Assert.Throws<GroupException>(() => AlgorandGroup.Ensure(forged));
    }

    [Fact]
    public void Ensure_MixedGrp_Refused()
    {
        var grouped = AlgorandGroup.Ensure(Group(2));
        var mixed = new List<Transaction> { grouped.Transactions[0], Pay(1) };
        Assert.Throws<GroupException>(() => AlgorandGroup.Ensure(mixed));
    }

    [Fact]
    public void Group_SizeLimits()
    {
        Assert.Throws<GroupException>(() => AlgorandGroup.Ensure([]));
        Assert.Throws<GroupException>(() => AlgorandGroup.Ensure(Group(17)));
    }

    [Fact]
    public void Base64_RoundTrips_AndKeepsTheHash()
    {
        var grouped = AlgorandGroup.Ensure(Group(2));
        var decoded = AlgorandGroup.Decode(grouped.ToBase64());
        Assert.Equal(grouped.GroupHash, AlgorandGroup.Ensure(decoded).GroupHash);
        Assert.Equal(grouped.TxIds, decoded.Select(t => t.TxID()).ToList());
    }

    [Fact]
    public void ValidityWindow_IsTheIntersection()
    {
        var grouped = AlgorandGroup.Ensure(Group(3));
        var (first, last) = grouped.ValidityWindow();
        Assert.Equal(710399UL + 2, first);
        Assert.Equal(710399UL + 1000, last);
    }
}
