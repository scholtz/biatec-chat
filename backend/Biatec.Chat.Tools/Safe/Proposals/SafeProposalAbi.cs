using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Algorand.Algod.Model.Transactions;
using Biatec.Chat.Tools.Safe.Create;

namespace Biatec.Chat.Tools.Safe.Proposals;

/// <summary>algo-safe payload transaction types (TX_PAYMENT, TX_ASSET, TX_APP).</summary>
public static class SafeTxType
{
    public const ulong Payment = 1;
    public const ulong AssetTransfer = 2;
    public const ulong AppCall = 3;
}

/// <summary>
/// One entry of a proposal's payload, decoded from the bytes the propose call carries. <see cref="Sender"/> is null
/// when the safe itself sends (the zero address); <see cref="CloseTo"/> is null when the entry closes nothing.
/// </summary>
public sealed record SafePayloadEntry(
    ulong TxType,
    string? Sender = null,
    string? Receiver = null,
    ulong AssetId = 0,
    ulong Amount = 0,
    string? CloseTo = null,
    ulong AppId = 0,
    ulong OnCompletion = 0,
    IReadOnlyList<byte[]>? AppArgs = null,
    IReadOnlyList<string>? Accounts = null,
    IReadOnlyList<ulong>? ForeignApps = null,
    IReadOnlyList<ulong>? ForeignAssets = null,
    string Note = "")
{
    public static SafePayloadEntry Pay(string receiver, ulong amount) => new(SafeTxType.Payment, Receiver: receiver, Amount: amount);
    public static SafePayloadEntry Axfer(ulong assetId, string receiver, ulong amount) => new(SafeTxType.AssetTransfer, Receiver: receiver, AssetId: assetId, Amount: amount);

    /// <summary>Same transaction field for field (lists compared by content).</summary>
    public bool SameAs(SafePayloadEntry o) =>
        TxType == o.TxType && Sender == o.Sender && Receiver == o.Receiver && AssetId == o.AssetId && Amount == o.Amount
        && CloseTo == o.CloseTo && AppId == o.AppId && OnCompletion == o.OnCompletion && Note == o.Note
        && (AppArgs ?? []).Count == (o.AppArgs ?? []).Count && (AppArgs ?? []).Zip(o.AppArgs ?? []).All(p => p.First.AsSpan().SequenceEqual(p.Second))
        && (Accounts ?? []).SequenceEqual(o.Accounts ?? []) && (ForeignApps ?? []).SequenceEqual(o.ForeignApps ?? [])
        && (ForeignAssets ?? []).SequenceEqual(o.ForeignAssets ?? []);
}

/// <summary>An algo-safe <c>AdminChange</c>, field for field as the contract stores it.</summary>
public sealed record SafeAdminChange(
    ulong ChangeType,
    ulong TargetGroupId = 0,
    string GroupName = "",
    string? Member = null,
    ulong MemberType = 1,
    string MemberLabel = "",
    ulong Threshold = 0,
    ulong AdminPrivileges = 0,
    ulong AllowedActions = 0,
    ulong LimitAssetId = 0,
    ulong DailyLimit = 0,
    ulong MonthlyLimit = 0,
    ulong CooldownRounds = 0,
    ulong ActiveFlag = 0,
    ulong GuardAmount = 0)
{
    public const ulong CreateGroup = 1;
    public const ulong AddMember = 2;
    public const ulong RemoveMember = 3;
    public const ulong ChangeThreshold = 4;

    public string TypeName => ChangeType switch
    {
        CreateGroup => "createGroup",
        AddMember => "addMember",
        RemoveMember => "removeMember",
        ChangeThreshold => "changeThreshold",
        5 => "setPolicy",
        6 => "setPrivileges",
        7 => "setActive",
        10 => "setPaused",
        _ => "admin-" + ChangeType,
    };
}

/// <summary>A decoded <c>proposeTransactionGroup</c> or <c>proposeAdminChange</c> call. Exactly one of the payloads is set.</summary>
public sealed record EncodedProposal(ulong GroupId, IReadOnlyList<SafePayloadEntry>? Entries, SafeAdminChange? Change, ulong ExpiryRound, bool Execute, string PayloadHash);

/// <summary>
/// Decodes the algo-safe proposal, approval and execution calls from the unsigned transaction bytes, so what is signed
/// is checked against the request, never the sidecar's description. Every decoded value is re-encoded canonically and
/// compared with the original bytes; anything that does not round-trip, and any shape it does not know, throws
/// <see cref="SafeBuildException"/> (fail closed, including a contract ABI change).
/// </summary>
public static class SafeProposalAbi
{
    /// <summary>proposeTransactionGroup(uint64,(uint64,byte[])[],uint64,bool,uint64)uint64</summary>
    public static readonly byte[] ProposeTransactionGroup = Convert.FromHexString("ac3a9a32");
    /// <summary>proposeAdminChange(uint64,(uint64,uint64,string,address,uint64,string,uint64,uint64,uint64,uint64,uint64,uint64,uint64,uint64,uint64),uint64,uint64)uint64</summary>
    public static readonly byte[] ProposeAdminChange = Convert.FromHexString("3f9b8a30");
    /// <summary>approveProposal(uint64,uint64,uint64)void</summary>
    public static readonly byte[] ApproveProposal = Convert.FromHexString("f4b486ee");
    /// <summary>executeProposal(uint64,uint64)void</summary>
    public static readonly byte[] ExecuteProposal = Convert.FromHexString("fb7088e8");

    /// <summary>Most transactions one algo-safe payload holds.</summary>
    public const int MaxEntries = 16;

    public static string ZeroAddress { get; } = new Algorand.Address(new byte[32]).EncodeAsString();

    /// <summary>The proposal a propose call encodes.</summary>
    public static EncodedProposal Propose(Transaction tx)
    {
        var a = SafeAbi.Args(tx);
        try
        {
            if (a.Count == 6 && a[0].AsSpan().SequenceEqual(ProposeTransactionGroup))
            {
                if (a[4].Length != 1 || a[4][0] is not (0 or 0x80)) throw new SafeBuildException("The execute flag is malformed.");
                var entries = DecodePayload(a[2]);
                if (!EncodePayload(entries).AsSpan().SequenceEqual(a[2])) throw new SafeBuildException("The payload is not canonically encoded.");
                return new EncodedProposal(U64Arg(a[1]), entries, null, U64Arg(a[3]), a[4][0] == 0x80, Hash(a[2]));
            }
            if (a.Count == 5 && a[0].AsSpan().SequenceEqual(ProposeAdminChange))
            {
                _ = U64Arg(a[4]);
                var change = DecodeAdmin(a[2]);
                if (!EncodeAdmin(change).AsSpan().SequenceEqual(a[2])) throw new SafeBuildException("The admin change is not canonically encoded.");
                return new EncodedProposal(U64Arg(a[1]), null, change, U64Arg(a[3]), false, Hash(a[2]));
            }
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or DecoderFallbackException or OverflowException)
        {
            throw new SafeBuildException("The proposal arguments could not be decoded.");
        }
        throw new SafeBuildException("The call is not an algo-safe propose call.");
    }

    /// <summary>(proposalId, expectedPayloadVersion) of an approveProposal call.</summary>
    public static (ulong ProposalId, ulong ExpectedPayloadVersion) Approve(Transaction tx)
    {
        var a = SafeAbi.Args(tx);
        if (a.Count != 4 || !a[0].AsSpan().SequenceEqual(ApproveProposal)) throw new SafeBuildException("The call is not an algo-safe approveProposal call.");
        _ = U64Arg(a[3]);
        return (U64Arg(a[1]), U64Arg(a[2]));
    }

    /// <summary>(proposalId, ensureBudgetValue) of an executeProposal call.</summary>
    public static (ulong ProposalId, ulong EnsureBudget) Execute(Transaction tx)
    {
        var a = SafeAbi.Args(tx);
        if (a.Count != 3 || !a[0].AsSpan().SequenceEqual(ExecuteProposal)) throw new SafeBuildException("The call is not an algo-safe executeProposal call.");
        return (U64Arg(a[1]), U64Arg(a[2]));
    }

    /// <summary>SHA-256 (lowercase hex) of the payload argument exactly as encoded; the sidecar reports the same hash for a stored proposal.</summary>
    public static string Hash(byte[] payloadArg) => Convert.ToHexString(SHA256.HashData(payloadArg)).ToLowerInvariant();

    private static ulong U64Arg(byte[] b) =>
        b.Length == 8 ? BinaryPrimitives.ReadUInt64BigEndian(b) : throw new SafeBuildException("An integer argument is malformed.");

    // ---- decoding ----

    private static List<SafePayloadEntry> DecodePayload(byte[] b)
    {
        var r = new Reader(b, 0);
        var count = r.U16(0);
        if (count is 0 or > MaxEntries) throw new SafeBuildException("The payload must hold 1 to 16 transactions.");
        var entries = new List<SafePayloadEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var start = 2 + r.U16(2 + 2 * i);
            var tuple = new Reader(b, start);
            var txType = tuple.U64(0);
            var data = tuple.Bytes(tuple.U16(8));
            entries.Add(DecodeEntry(txType, data));
        }
        return entries;
    }

    private static SafePayloadEntry DecodeEntry(ulong txType, byte[] d)
    {
        var r = new Reader(d, 0);
        switch (txType)
        {
            case SafeTxType.Payment:
                // (address sender, address receiver, uint64 amount, uint64 hasClose, address closeTo, string note)
                return new SafePayloadEntry(txType, Sender(r.Addr(0)), r.Addr(32), Amount: r.U64(64),
                    CloseTo: Close(r.U64(72), r.Addr(80)), Note: r.Str(r.U16(112)));
            case SafeTxType.AssetTransfer:
                // (address sender, uint64 asset, address receiver, uint64 amount, uint64 hasClose, address closeTo, string note)
                return new SafePayloadEntry(txType, Sender(r.Addr(0)), r.Addr(40), r.U64(32), r.U64(72),
                    Close(r.U64(80), r.Addr(88)), Note: r.Str(r.U16(120)));
            case SafeTxType.AppCall:
            {
                // (uint64 appId, uint64 onCompletion, byte[][] args, address[] accounts, uint64[] apps, uint64[] assets, string note)
                var args = new List<byte[]>();
                var argsAt = r.U16(16);
                var argCount = r.U16(argsAt);
                for (var i = 0; i < argCount; i++) args.Add(new Reader(d, argsAt + 2).Bytes(r.U16(argsAt + 2 + 2 * i)));
                var accountsAt = r.U16(18);
                var accounts = Enumerable.Range(0, r.U16(accountsAt)).Select(i => r.Addr(accountsAt + 2 + 32 * i)).ToList();
                var appsAt = r.U16(20);
                var apps = Enumerable.Range(0, r.U16(appsAt)).Select(i => r.U64(appsAt + 2 + 8 * i)).ToList();
                var assetsAt = r.U16(22);
                var assets = Enumerable.Range(0, r.U16(assetsAt)).Select(i => r.U64(assetsAt + 2 + 8 * i)).ToList();
                return new SafePayloadEntry(txType, AppId: r.U64(0), OnCompletion: r.U64(8), AppArgs: args, Accounts: accounts,
                    ForeignApps: apps, ForeignAssets: assets, Note: r.Str(r.U16(24)));
            }
            default:
                throw new SafeBuildException("The payload holds a transaction type chat does not propose.");
        }
    }

    private static SafeAdminChange DecodeAdmin(byte[] b)
    {
        var r = new Reader(b, 0);
        // (uint64,uint64,string,address,uint64,string,uint64 x9): head 8+8+2+32+8+2+72 = 132 bytes
        return new SafeAdminChange(r.U64(0), r.U64(8), r.Str(r.U16(16)), Sender(r.Addr(18)), r.U64(50), r.Str(r.U16(58)),
            r.U64(60), r.U64(68), r.U64(76), r.U64(84), r.U64(92), r.U64(100), r.U64(108), r.U64(116), r.U64(124));
    }

    private static string? Sender(string a) => a == ZeroAddress ? null : a;
    private static string? Close(ulong has, string to) => has switch
    {
        0 when to == ZeroAddress => null,
        1 => to,
        _ => throw new SafeBuildException("A close-to field is malformed."),
    };

    private readonly struct Reader(byte[] b, int origin)
    {
        public int U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(At(at, 2), 2));
        public ulong U64(int at) => BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(At(at, 8), 8));
        public string Addr(int at) => new Algorand.Address(b.AsSpan(At(at, 32), 32).ToArray()).EncodeAsString();
        public byte[] Bytes(int at) { var len = U16(at); return b.AsSpan(At(at + 2, len), len).ToArray(); }
        public string Str(int at) => new UTF8Encoding(false, true).GetString(Bytes(at));

        private int At(int at, int len)
        {
            var p = origin + at;
            if (at < 0 || p < 0 || p + len > b.Length) throw new SafeBuildException("An ABI value is truncated.");
            return p;
        }
    }

    // ---- canonical encoding (used for the round-trip check) ----

    public static byte[] EncodePayload(IReadOnlyList<SafePayloadEntry> entries)
    {
        var tuples = entries.Select(e => Tuple([U64(e.TxType)], [Dyn(Bytes(EncodeEntry(e)))])).ToList();
        return DynArray(tuples);
    }

    private static byte[] EncodeEntry(SafePayloadEntry e) => e.TxType switch
    {
        SafeTxType.Payment => Tuple([Addr(e.Sender), Addr(e.Receiver), U64(e.Amount), U64(e.CloseTo is null ? 0UL : 1UL), Addr(e.CloseTo)], [Dyn(Str(e.Note))]),
        SafeTxType.AssetTransfer => Tuple([Addr(e.Sender), U64(e.AssetId), Addr(e.Receiver), U64(e.Amount), U64(e.CloseTo is null ? 0UL : 1UL), Addr(e.CloseTo)], [Dyn(Str(e.Note))]),
        SafeTxType.AppCall => Tuple([U64(e.AppId), U64(e.OnCompletion)],
        [
            Dyn(DynArray((e.AppArgs ?? []).Select(Bytes).ToList())),
            Dyn([.. U16(e.Accounts?.Count ?? 0), .. (e.Accounts ?? []).SelectMany(a => Addr(a))]),
            Dyn([.. U16(e.ForeignApps?.Count ?? 0), .. (e.ForeignApps ?? []).SelectMany(U64)]),
            Dyn([.. U16(e.ForeignAssets?.Count ?? 0), .. (e.ForeignAssets ?? []).SelectMany(U64)]),
            Dyn(Str(e.Note)),
        ]),
        _ => throw new SafeBuildException("The payload holds a transaction type chat does not propose."),
    };

    public static byte[] EncodeAdmin(SafeAdminChange c)
    {
        // Static head with the two string offsets in place; the strings follow in order.
        var name = Str(c.GroupName);
        var label = Str(c.MemberLabel);
        const int head = 132;
        var o = new List<byte>();
        o.AddRange(U64(c.ChangeType)); o.AddRange(U64(c.TargetGroupId)); o.AddRange(U16(head)); o.AddRange(Addr(c.Member));
        o.AddRange(U64(c.MemberType)); o.AddRange(U16(head + name.Length));
        foreach (var v in new[] { c.Threshold, c.AdminPrivileges, c.AllowedActions, c.LimitAssetId, c.DailyLimit, c.MonthlyLimit, c.CooldownRounds, c.ActiveFlag, c.GuardAmount })
            o.AddRange(U64(v));
        o.AddRange(name); o.AddRange(label);
        return o.ToArray();
    }

    /// <summary>A tuple whose static fields all come first, then the dynamic ones: their offsets, then their bodies.</summary>
    private static byte[] Tuple(IReadOnlyList<byte[]> statics, IReadOnlyList<(bool Dynamic, byte[] Body)> dynamics)
    {
        var headLen = statics.Sum(s => s.Length) + 2 * dynamics.Count;
        var o = new List<byte>();
        foreach (var s in statics) o.AddRange(s);
        var offset = headLen;
        foreach (var d in dynamics) { o.AddRange(U16(offset)); offset += d.Body.Length; }
        foreach (var d in dynamics) o.AddRange(d.Body);
        return o.ToArray();
    }

    private static (bool, byte[]) Dyn(byte[] body) => (true, body);

    /// <summary>A dynamic array of dynamic elements: count, element offsets (from after the count), elements.</summary>
    private static byte[] DynArray(IReadOnlyList<byte[]> elements)
    {
        var o = new List<byte>(U16(elements.Count));
        var offset = 2 * elements.Count;
        foreach (var e in elements) { o.AddRange(U16(offset)); offset += e.Length; }
        foreach (var e in elements) o.AddRange(e);
        return o.ToArray();
    }

    private static byte[] Bytes(byte[] b) => [.. U16(b.Length), .. b];
    private static byte[] Str(string s) => Bytes(Encoding.UTF8.GetBytes(s));
    private static byte[] Addr(string? a) => a is null ? new byte[32] : new Algorand.Address(a).Bytes;

    private static byte[] U16(int v)
    {
        if (v is < 0 or > ushort.MaxValue) throw new SafeBuildException("An ABI value is too large.");
        return [(byte)(v >> 8), (byte)v];
    }

    private static byte[] U64(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        return b;
    }
}
