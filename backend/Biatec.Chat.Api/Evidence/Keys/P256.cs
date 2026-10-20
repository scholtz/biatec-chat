using System.Numerics;
using System.Security.Cryptography;

namespace Biatec.Chat.Api.Evidence.Keys;

/// <summary>
/// NIST P-256 helpers for derived private scalars. The public point is computed by the platform crypto library
/// (OpenSSL/CNG, constant-time) from the private scalar alone; no custom curve arithmetic is used.
/// </summary>
internal static class P256
{
    private static readonly BigInteger N = BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", System.Globalization.NumberStyles.HexNumber);

    /// <summary>Maps 48 uniform bytes to a valid private scalar in [1, n-1] (bias is negligible at 384 bits).</summary>
    public static byte[] ScalarFromBytes(byte[] uniform48)
    {
        var v = new BigInteger(uniform48, isUnsigned: true, isBigEndian: true);
        var s = v % (N - 1) + 1;
        var b = s.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (b.Length == 32) return b;
        var r = new byte[32];
        Buffer.BlockCopy(b, 0, r, 32 - b.Length, b.Length);
        return r;
    }

    /// <summary>Full key parameters (D plus the platform-computed Q) for the private scalar <paramref name="d"/>.</summary>
    public static ECParameters ToParameters(byte[] d)
    {
        using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d });
        return ec.ExportParameters(true);
    }
}
