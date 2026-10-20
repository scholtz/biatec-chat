using System.Numerics;
using System.Security.Cryptography;

namespace Biatec.Chat.Api.Evidence.Keys;

/// <summary>
/// Minimal NIST P-256 arithmetic, only to compute the public point of a derived private scalar (the platform ECDsa
/// APIs do not all accept a private scalar without its public point). Runs once per key, never on the signing path.
/// </summary>
internal static class P256
{
    private static readonly BigInteger P = BigInteger.Parse("0FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger N = BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger Gx = BigInteger.Parse("06B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger Gy = BigInteger.Parse("04FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger A = P - 3;

    /// <summary>Maps 48 uniform bytes to a valid private scalar in [1, n-1] (bias is negligible at 384 bits).</summary>
    public static byte[] ScalarFromBytes(byte[] uniform48)
    {
        var v = new BigInteger(uniform48, isUnsigned: true, isBigEndian: true);
        return ToBytes32(v % (N - 1) + 1);
    }

    public static ECParameters ToParameters(byte[] d)
    {
        var (x, y) = Multiply(new BigInteger(d, isUnsigned: true, isBigEndian: true));
        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = d,
            Q = new ECPoint { X = ToBytes32(x), Y = ToBytes32(y) },
        };
    }

    private static byte[] ToBytes32(BigInteger v) => v.ToByteArray(isUnsigned: true, isBigEndian: true) is { Length: 32 } b
        ? b : PadLeft(v.ToByteArray(isUnsigned: true, isBigEndian: true));

    private static byte[] PadLeft(byte[] b)
    {
        var r = new byte[32];
        Buffer.BlockCopy(b, 0, r, 32 - b.Length, b.Length);
        return r;
    }

    private static BigInteger Mod(BigInteger a) { var r = a % P; return r < 0 ? r + P : r; }
    private static BigInteger Inv(BigInteger a) => BigInteger.ModPow(Mod(a), P - 2, P);

    private static (BigInteger X, BigInteger Y)? Add((BigInteger X, BigInteger Y)? p, (BigInteger X, BigInteger Y)? q)
    {
        if (p is null) return q;
        if (q is null) return p;
        var (x1, y1) = p.Value;
        var (x2, y2) = q.Value;
        BigInteger l;
        if (x1 == x2)
        {
            if (Mod(y1 + y2) == 0) return null;
            l = Mod((3 * x1 * x1 + A) * Inv(2 * y1));
        }
        else l = Mod((y2 - y1) * Inv(x2 - x1));
        var x3 = Mod(l * l - x1 - x2);
        return (x3, Mod(l * (x1 - x3) - y1));
    }

    private static (BigInteger X, BigInteger Y) Multiply(BigInteger k)
    {
        (BigInteger X, BigInteger Y)? result = null;
        (BigInteger X, BigInteger Y)? addend = (Gx, Gy);
        for (; k > 0; k >>= 1)
        {
            if (!k.IsEven) result = Add(result, addend);
            addend = Add(addend, addend);
        }
        return result ?? throw new CryptographicException("Invalid scalar.");
    }
}
