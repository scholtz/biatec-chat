using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Biatec.Chat.Core.Canonical;

namespace Biatec.Chat.Tools.Wallet.Bitcoin;

public sealed class PsbtException(string message) : Exception(message);

public sealed record BitcoinInput(string Txid, uint Vout, long Sats, uint Sequence, string ScriptPubKey, string? Address);
public sealed record BitcoinOutput(string ScriptPubKey, string? Address, long Sats);

/// <summary>
/// The decoded intent of an unsigned Bitcoin transaction: everything the hash commits to
/// (PRODUCT-DESCRIPTION.md, "Action Preview": version, locktime, inputs with prevout values and scripts, outputs, fee).
/// </summary>
public sealed record BitcoinIntent(int Version, uint Locktime, IReadOnlyList<BitcoinInput> Inputs, IReadOnlyList<BitcoinOutput> Outputs, long FeeSats)
{
    /// <summary>The intent as JSON: <c>{version, locktime, inputs: [{txid, vout, sats, sequence, scriptPubKey}], outputs: [{scriptPubKey, address, sats}], feeSats}</c>.</summary>
    public JsonObject ToJson()
    {
        var inputs = new JsonArray();
        foreach (var i in Inputs)
            inputs.Add(new JsonObject { ["txid"] = i.Txid, ["vout"] = (long)i.Vout, ["sats"] = i.Sats, ["sequence"] = (long)i.Sequence, ["scriptPubKey"] = i.ScriptPubKey });
        var outputs = new JsonArray();
        foreach (var o in Outputs)
            outputs.Add(new JsonObject { ["scriptPubKey"] = o.ScriptPubKey, ["address"] = o.Address, ["sats"] = o.Sats });
        return new JsonObject { ["version"] = Version, ["locktime"] = (long)Locktime, ["inputs"] = inputs, ["outputs"] = outputs, ["feeSats"] = FeeSats };
    }

    /// <summary>SHA-256 over the RFC 8785 canonical JSON of the intent, lowercase hex. This is the group hash of a Bitcoin step.</summary>
    public string Hash() => Hashing.CanonicalSha256Hex(ToJson());

    /// <summary>
    /// Estimated virtual size once signed, from the prevout script types (the unsigned transaction has no witnesses).
    /// An estimate: unknown input types count as P2WPKH.
    /// </summary>
    public decimal EstimatedVirtualSize()
    {
        decimal vb = 10.5m + CompactSizeLength(Inputs.Count) - 1 + CompactSizeLength(Outputs.Count) - 1;
        foreach (var i in Inputs)
        {
            var s = i.ScriptPubKey;
            vb += s.Length switch
            {
                50 when s.StartsWith("76a914", StringComparison.Ordinal) => 148m,      // P2PKH
                46 when s.StartsWith("a914", StringComparison.Ordinal) => 91m,          // P2SH-P2WPKH
                68 when s.StartsWith("5120", StringComparison.Ordinal) => 57.5m,        // P2TR key path
                68 when s.StartsWith("0020", StringComparison.Ordinal) => 105m,         // P2WSH, a typical 2-of-3
                _ => 68m,                                                              // P2WPKH
            };
        }
        foreach (var o in Outputs) vb += 8 + CompactSizeLength(o.ScriptPubKey.Length / 2) + o.ScriptPubKey.Length / 2;
        return vb;
    }

    /// <summary>Fee rate in sat/vB from <see cref="EstimatedVirtualSize"/>, rounded to two decimals.</summary>
    public decimal FeeRate() => Math.Round(FeeSats / EstimatedVirtualSize(), 2);

    private static int CompactSizeLength(int n) => n < 253 ? 1 : n <= 0xffff ? 3 : 5;
}

/// <summary>
/// Decodes a BIP-174 PSBT (version 0) that carries the prevout of every input in <c>witness_utxo</c> or
/// <c>non_witness_utxo</c>. Decoding only: nothing is built or signed. A <c>non_witness_utxo</c> must hash to the
/// txid the input spends, so the prevout value cannot be misreported.
/// </summary>
public static class PsbtDecoder
{
    private static readonly byte[] Magic = [0x70, 0x73, 0x62, 0x74, 0xff];

    /// <summary>Accepts base64 or hex.</summary>
    public static BitcoinIntent Decode(string psbt, bool testnet)
    {
        var text = psbt.Trim();
        byte[] bytes;
        try
        {
            bytes = text.StartsWith("70736274ff", StringComparison.OrdinalIgnoreCase) ? Convert.FromHexString(text) : Convert.FromBase64String(text);
        }
        catch (FormatException) { throw new PsbtException("The PSBT is not valid base64 or hex."); }
        return Decode(bytes, testnet);
    }

    public static bool LooksLikePsbt(string s)
    {
        var t = s.Trim();
        return t.StartsWith("cHNidP8", StringComparison.Ordinal) || t.StartsWith("70736274ff", StringComparison.OrdinalIgnoreCase);
    }

    public static BitcoinIntent Decode(byte[] bytes, bool testnet)
    {
        try { return DecodeCore(bytes, testnet); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidOperationException)
        {
            throw new PsbtException("The PSBT is malformed.");
        }
    }

    private static BitcoinIntent DecodeCore(byte[] bytes, bool testnet)
    {
        var r = new Reader(bytes);
        if (!r.Take(5).SequenceEqual(Magic)) throw new PsbtException("The data is not a PSBT.");

        byte[]? unsignedTx = null;
        foreach (var (key, value) in ReadMap(r))
        {
            if (key.Length == 1 && key[0] == 0x00) unsignedTx = value;
            else if (key.Length == 1 && key[0] == 0xfb && value.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(value) != 0)
                throw new PsbtException("Only PSBT version 0 is supported.");
        }
        if (unsignedTx is null) throw new PsbtException("The PSBT has no unsigned transaction.");

        var tx = ParseTx(unsignedTx);
        if (tx.HasWitness) throw new PsbtException("The PSBT's transaction must be unsigned.");

        var inputs = new List<BitcoinInput>();
        long inTotal = 0;
        foreach (var txin in tx.Inputs)
        {
            byte[]? witnessUtxo = null, nonWitness = null;
            foreach (var (key, value) in ReadMap(r))
            {
                if (key.Length != 1) continue;
                if (key[0] == 0x01) witnessUtxo = value;
                else if (key[0] == 0x00) nonWitness = value;
            }
            long sats;
            byte[] script;
            if (nonWitness is not null)
            {
                var prev = ParseTx(nonWitness);
                if (!Txid(prev).SequenceEqual(txin.PrevTxid)) throw new PsbtException("An input's non_witness_utxo does not match the transaction it spends.");
                if (txin.Vout >= prev.Outputs.Count) throw new PsbtException("An input spends an output that does not exist.");
                (sats, script) = prev.Outputs[(int)txin.Vout];
                if (witnessUtxo is not null)
                {
                    var w = ParseTxOut(witnessUtxo);
                    if (w.Sats != sats || !w.Script.SequenceEqual(script)) throw new PsbtException("An input's witness_utxo and non_witness_utxo disagree.");
                }
            }
            else if (witnessUtxo is not null) (sats, script) = ParseTxOut(witnessUtxo);
            else throw new PsbtException("An input carries no prevout data, so its value cannot be shown.");

            inTotal = checked(inTotal + sats);
            var display = Convert.ToHexStringLower(txin.PrevTxid.Reverse().ToArray());
            inputs.Add(new BitcoinInput(display, txin.Vout, sats, txin.Sequence, Convert.ToHexStringLower(script), BitcoinAddresses.FromScript(script, testnet)));
        }
        for (var i = 0; i < tx.Outputs.Count; i++) ReadMap(r).ToList(); // output maps carry nothing the intent needs
        if (r.Remaining != 0) throw new PsbtException("The PSBT has trailing data.");

        long outTotal = 0;
        var outputs = new List<BitcoinOutput>();
        foreach (var (sats, script) in tx.Outputs)
        {
            outTotal = checked(outTotal + sats);
            outputs.Add(new BitcoinOutput(Convert.ToHexStringLower(script), BitcoinAddresses.FromScript(script, testnet), sats));
        }
        if (outTotal > inTotal) throw new PsbtException("The outputs exceed the inputs.");
        return new BitcoinIntent(tx.Version, tx.Locktime, inputs, outputs, inTotal - outTotal);
    }

    private sealed record RawIn(byte[] PrevTxid, uint Vout, uint Sequence);
    private sealed record RawTx(int Version, uint Locktime, List<RawIn> Inputs, List<(long Sats, byte[] Script)> Outputs, bool HasWitness, byte[] NonWitnessBytes);

    private static (long Sats, byte[] Script) ParseTxOut(byte[] data)
    {
        var r = new Reader(data);
        var sats = r.Int64();
        var script = r.Take((int)r.CompactSize());
        if (r.Remaining != 0 || sats < 0) throw new PsbtException("A prevout is malformed.");
        return (sats, script);
    }

    private static byte[] Txid(RawTx tx) => SHA256.HashData(SHA256.HashData(tx.NonWitnessBytes));

    private static RawTx ParseTx(byte[] data)
    {
        var r = new Reader(data);
        var version = r.Int32();
        var witness = false;
        if (r.Peek() == 0x00) { r.Take(1); if (r.Take(1)[0] != 0x01) throw new PsbtException("Bad transaction marker."); witness = true; }
        var inputs = new List<RawIn>();
        var inCount = r.CompactSize();
        for (ulong i = 0; i < inCount; i++)
        {
            var txid = r.Take(32);
            var vout = r.UInt32();
            var sigLen = (int)r.CompactSize();
            r.Take(sigLen);
            inputs.Add(new RawIn(txid, vout, r.UInt32()));
        }
        var outputs = new List<(long, byte[])>();
        var outCount = r.CompactSize();
        for (ulong i = 0; i < outCount; i++)
        {
            var sats = r.Int64();
            if (sats < 0) throw new PsbtException("A transaction output has a negative value.");
            outputs.Add((sats, r.Take((int)r.CompactSize())));
        }
        var outputsEnd = r.Position;
        if (witness)
            for (var i = 0; i < inputs.Count; i++)
            {
                var items = r.CompactSize();
                for (ulong j = 0; j < items; j++) r.Take((int)r.CompactSize());
            }
        var locktime = r.UInt32();
        if (r.Remaining != 0) throw new PsbtException("A transaction has trailing data.");

        // The txid is the double SHA-256 of the serialization without marker, flag and witnesses.
        byte[] stripped;
        if (witness)
        {
            using var ms = new MemoryStream();
            ms.Write(data, 0, 4);
            ms.Write(data, 6, outputsEnd - 6);
            ms.Write(data, data.Length - 4, 4);
            stripped = ms.ToArray();
        }
        else stripped = data;
        return new RawTx(version, locktime, inputs, outputs, witness, stripped);
    }

    private static IEnumerable<(byte[] Key, byte[] Value)> ReadMap(Reader r)
    {
        var list = new List<(byte[], byte[])>();
        while (true)
        {
            var keyLen = (int)r.CompactSize();
            if (keyLen == 0) break;
            var key = r.Take(keyLen);
            var value = r.Take((int)r.CompactSize());
            list.Add((key, value));
        }
        return list;
    }

    private sealed class Reader(byte[] data)
    {
        private int _pos;
        public int Position => _pos;
        public int Remaining => data.Length - _pos;
        public byte Peek() => data[_pos];

        public byte[] Take(int n)
        {
            if (n < 0 || n > Remaining) throw new PsbtException("The PSBT is truncated.");
            var slice = data.AsSpan(_pos, n).ToArray();
            _pos += n;
            return slice;
        }

        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        public ulong CompactSize()
        {
            var first = Take(1)[0];
            return first switch
            {
                < 0xfd => first,
                0xfd => BinaryPrimitives.ReadUInt16LittleEndian(Take(2)),
                0xfe => BinaryPrimitives.ReadUInt32LittleEndian(Take(4)),
                _ => BinaryPrimitives.ReadUInt64LittleEndian(Take(8)),
            };
        }
    }
}
