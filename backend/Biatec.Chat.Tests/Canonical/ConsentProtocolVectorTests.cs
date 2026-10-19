using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Biatec.Chat.Core.Canonical;

namespace Biatec.Chat.Tests.Canonical;

/// <summary>
/// Cross-checks packages/consent-protocol/vectors against the backend RFC 8785 implementation, so the
/// published vectors and the code that will hash and verify receipts cannot drift apart.
/// </summary>
public class ConsentProtocolVectorTests
{
    private static string VectorsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "packages", "consent-protocol", "vectors");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("packages/consent-protocol/vectors not found above " + AppContext.BaseDirectory);
    }

    private static JsonNode Load(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(VectorsDir(), name)))!;

    private static byte[] B64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    [Fact]
    public void CanonicalizationVectors_MatchBackendCanonicalizer()
    {
        var vectors = Load("canonicalization.json")["vectors"]!.AsArray();
        Assert.NotEmpty(vectors);
        foreach (var v in vectors)
            Assert.Equal((string)v!["canonical"]!, JsonCanonicalizer.Canonicalize(v["input"]));
    }

    [Fact]
    public void ReceiptVectors_CanonicalFormHashAndDeviceSignatureVerify()
    {
        var vectors = Load("receipts.json")["vectors"]!.AsArray();
        Assert.NotEmpty(vectors);
        foreach (var v in vectors)
        {
            var name = (string)v!["name"]!;
            var receipt = v["receipt"]!;
            var canonical = JsonCanonicalizer.Canonicalize(receipt["signed"]);
            Assert.True((string)v["canonical"]! == canonical, name + ": canonical form");
            Assert.Equal((string)v["canonical_sha256"]!, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));

            var jwk = receipt["device"]!["public_key"]!;
            using var key = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = B64Url((string)jwk["x"]!), Y = B64Url((string)jwk["y"]!) },
            });
            var sig = B64Url((string)receipt["signature"]!);
            Assert.True(
                key.VerifyData(Encoding.UTF8.GetBytes(canonical), sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
                name + ": device signature");

            // A changed byte must break it.
            var tampered = canonical.Replace("\"sub\":\"", "\"sub\":\"x");
            Assert.False(key.VerifyData(Encoding.UTF8.GetBytes(tampered), sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
    }

    [Fact]
    public void LimitsChangeVector_RequestHashIsSha256OfCanonicalBody()
    {
        var v = Load("receipts.json")["vectors"]!.AsArray().Single(x => x!["limits_request_body"] is not null)!;
        var body = JsonCanonicalizer.Canonicalize(v["limits_request_body"]);
        var expected = (string)v["request"]!["authorization_details"]![0]!["request_hash"]!;
        Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))));
    }
}
