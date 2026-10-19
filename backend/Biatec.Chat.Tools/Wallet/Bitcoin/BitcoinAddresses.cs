using System.Security.Cryptography;
using System.Text;

namespace Biatec.Chat.Tools.Wallet.Bitcoin;

/// <summary>
/// Reads a standard output script as an address (decoding only, nothing is built). P2PKH and P2SH use Base58Check;
/// P2WPKH, P2WSH (Bech32) and P2TR (Bech32m) use the segwit encodings. Anything else (OP_RETURN, bare scripts) has no address.
/// </summary>
public static class BitcoinAddresses
{
    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private const string Bech32Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    public static bool IsTestnet(string network) => network.EndsWith("testnet", StringComparison.OrdinalIgnoreCase);

    public static string? FromScript(ReadOnlySpan<byte> s, bool testnet)
    {
        var hrp = testnet ? "tb" : "bc";
        // P2PKH: OP_DUP OP_HASH160 <20> OP_EQUALVERIFY OP_CHECKSIG
        if (s.Length == 25 && s[0] == 0x76 && s[1] == 0xa9 && s[2] == 0x14 && s[23] == 0x88 && s[24] == 0xac)
            return Base58Check(testnet ? (byte)0x6f : (byte)0x00, s.Slice(3, 20));
        // P2SH: OP_HASH160 <20> OP_EQUAL
        if (s.Length == 23 && s[0] == 0xa9 && s[1] == 0x14 && s[22] == 0x87)
            return Base58Check(testnet ? (byte)0xc4 : (byte)0x05, s.Slice(2, 20));
        // Witness programs: OP_0 or OP_1 followed by one push of 20 or 32 bytes.
        if (s.Length == 22 && s[0] == 0x00 && s[1] == 0x14) return Segwit(hrp, 0, s[2..]);
        if (s.Length == 34 && s[0] == 0x00 && s[1] == 0x20) return Segwit(hrp, 0, s[2..]);
        if (s.Length == 34 && s[0] == 0x51 && s[1] == 0x20) return Segwit(hrp, 1, s[2..]);
        return null;
    }

    public static string Base58Check(byte version, ReadOnlySpan<byte> payload)
    {
        var data = new byte[1 + payload.Length + 4];
        data[0] = version;
        payload.CopyTo(data.AsSpan(1));
        var check = SHA256.HashData(SHA256.HashData(data.AsSpan(0, 1 + payload.Length)));
        Array.Copy(check, 0, data, 1 + payload.Length, 4);

        var digits = new List<byte>();
        foreach (var b in data)
        {
            var carry = (int)b;
            for (var i = 0; i < digits.Count; i++)
            {
                carry += digits[i] << 8;
                digits[i] = (byte)(carry % 58);
                carry /= 58;
            }
            while (carry > 0) { digits.Add((byte)(carry % 58)); carry /= 58; }
        }
        var sb = new StringBuilder();
        foreach (var b in data) { if (b != 0) break; sb.Append('1'); }
        for (var i = digits.Count - 1; i >= 0; i--) sb.Append(Base58Alphabet[digits[i]]);
        return sb.ToString();
    }

    public static string Segwit(string hrp, int version, ReadOnlySpan<byte> program)
    {
        var values = new List<byte> { (byte)version };
        values.AddRange(ConvertBits(program, 8, 5));
        const uint bech32 = 1, bech32m = 0x2bc830a3;
        var constant = version == 0 ? bech32 : bech32m;
        var expanded = new List<byte>();
        foreach (var c in hrp) expanded.Add((byte)(c >> 5));
        expanded.Add(0);
        foreach (var c in hrp) expanded.Add((byte)(c & 31));
        expanded.AddRange(values);
        expanded.AddRange(new byte[6]);
        var mod = Polymod(expanded) ^ constant;
        var sb = new StringBuilder(hrp).Append('1');
        foreach (var v in values) sb.Append(Bech32Charset[v]);
        for (var i = 0; i < 6; i++) sb.Append(Bech32Charset[(int)((mod >> (5 * (5 - i))) & 31)]);
        return sb.ToString();
    }

    private static uint Polymod(List<byte> values)
    {
        uint[] gen = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
        uint chk = 1;
        foreach (var v in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ v;
            for (var i = 0; i < 5; i++) if (((top >> i) & 1) != 0) chk ^= gen[i];
        }
        return chk;
    }

    private static List<byte> ConvertBits(ReadOnlySpan<byte> data, int from, int to)
    {
        var acc = 0;
        var bits = 0;
        var result = new List<byte>();
        var max = (1 << to) - 1;
        foreach (var v in data)
        {
            acc = (acc << from) | v;
            bits += from;
            while (bits >= to) { bits -= to; result.Add((byte)((acc >> bits) & max)); }
            acc &= (1 << bits) - 1;
        }
        if (bits > 0) result.Add((byte)((acc << (to - bits)) & max));
        return result;
    }
}
