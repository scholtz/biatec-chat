using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Biatec.Chat.Core.Canonical;

namespace Biatec.Chat.Tests.Canonical;

/// <summary>
/// Cross-checks packages/consent-protocol/vectors against the backend RFC 8785 implementation, so the
/// published vectors and the code that will hash and verify receipts cannot drift apart. Covers the frozen protocol 1.0
/// receipts, the protocol 1.1 receipts (including the vectors a verifier MUST refuse), the device-signed statements,
/// the pairing code and the enrollment proofs.
/// </summary>
public class ConsentProtocolVectorTests
{
    private const string Issuer = "https://oidc.biatec.test";

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

    private static IEnumerable<(string File, JsonNode Vector)> ReceiptVectors() =>
        new[] { "receipts-1.0.json", "receipts.json" }.SelectMany(f => Load(f)["vectors"]!.AsArray().Select(v => (f, v!)));

    private static byte[] B64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    private static string B64UrlEncode(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ECDsa KeyOf(JsonNode jwk) => ECDsa.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = B64Url((string)jwk["x"]!), Y = B64Url((string)jwk["y"]!) },
    });

    private static bool VerifyEs256(JsonNode jwk, byte[] data, string signature)
    {
        using var key = KeyOf(jwk);
        var sig = B64Url(signature);
        return sig.Length == 64 && key.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>RFC 7638 thumbprint of an EC P-256 public key: SHA-256 over the members in lexicographic order.</summary>
    private static string Thumbprint(JsonNode pk)
    {
        var input = $"{{\"crv\":\"{(string)pk["crv"]!}\",\"kty\":\"{(string)pk["kty"]!}\",\"x\":\"{(string)pk["x"]!}\",\"y\":\"{(string)pk["y"]!}\"}}";
        return B64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    /// <summary>The attestation JWS verified with the JWKS key it names; returns its payload.</summary>
    private static JsonNode VerifiedAttestation(string attestation, JsonArray jwks)
    {
        var parts = attestation.Split('.');
        Assert.Equal(3, parts.Length);
        var header = JsonNode.Parse(B64Url(parts[0]))!;
        var payload = JsonNode.Parse(B64Url(parts[1]))!;
        Assert.Equal("biatec-device-attestation+jwt", (string)header["typ"]!);
        Assert.Equal("ES256", (string)header["alg"]!);
        Assert.Equal(Issuer, (string)payload["iss"]!);
        var jwk = jwks.Single(k => (string)k!["kid"]! == (string)header["kid"]!)!;
        Assert.True(VerifyEs256(jwk, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), parts[2]), "attestation signature");
        return payload;
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
        Assert.True(ReceiptVectors().Count() >= 18, "the frozen 1.0 set and the 1.1 set");
        foreach (var (file, v) in ReceiptVectors())
        {
            var name = file + " " + (string)v["name"]!;
            var receipt = v["receipt"]!;
            var canonical = JsonCanonicalizer.Canonicalize(receipt["signed"]);
            Assert.True((string)v["canonical"]! == canonical, name + ": canonical form");
            Assert.Equal((string)v["canonical_sha256"]!, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));

            // The device signature is valid for every vector, including those a verifier must refuse for another reason.
            var jwk = receipt["device"]!["public_key"]!;
            Assert.True(VerifyEs256(jwk, Encoding.UTF8.GetBytes(canonical), (string)receipt["signature"]!), name + ": device signature");

            // A changed byte must break it.
            var tampered = canonical.Replace("\"sub\":\"", "\"sub\":\"x");
            Assert.False(VerifyEs256(jwk, Encoding.UTF8.GetBytes(tampered), (string)receipt["signature"]!));
        }
    }

    [Fact]
    public void ReceiptVectors_AttestationVerifiesAndBindsSubAndDeviceKey()
    {
        var jwks = Load("jwks.json")["keys"]!.AsArray();
        foreach (var (file, v) in ReceiptVectors())
        {
            var receipt = v["receipt"]!;
            var dev = receipt["device"]!;
            var payload = VerifiedAttestation((string)dev["attestation"]!, jwks);
            Assert.True((long)payload["exp"]! > (long)receipt["issued_at"]!, "attestation outlives the receipt");
            Assert.Equal((string)receipt["signed"]!["sub"]!, (string)payload["sub"]!);
            Assert.Equal((string)dev["kid"]!, (string)payload["device_kid"]!);
            Assert.Equal(Thumbprint(dev["public_key"]!), (string)payload["device_jwk_thumbprint"]!);
            if ((string)receipt["version"]! == "1")
                Assert.Null(payload["biatec_key_use"]); // 1.0 predates the two keys
            else
                Assert.Equal((string)dev["key_use"]!, (string)payload["biatec_key_use"]!); // the informational member agrees with the attested claim
        }
    }

    [Fact]
    public void ReceiptVectors_KeyUseRule_ApproveNeedsAnApprovalKey_AndEveryVectorGetsItsExpectedVerdict()
    {
        var jwks = Load("jwks.json")["keys"]!.AsArray();
        var refused = 0;
        foreach (var (file, v) in ReceiptVectors())
        {
            var receipt = v["receipt"]!;
            var payload = VerifiedAttestation((string)receipt["device"]!["attestation"]!, jwks);
            var is11 = (string)receipt["version"]! == "1.1";
            // The rule of spec section 8.1, step 3: from 1.1 on an approve receipt MUST carry the attested use "approve".
            var accepted = !(is11 && (string)receipt["signed"]!["decision"]! == "approve" && (string?)payload["biatec_key_use"] != "approve");
            var expected = v["expected"]?["accepted"]?.GetValue<bool>() ?? true;
            Assert.True(expected == accepted, file + " " + (string)v["name"]! + ": expected verdict");
            if (!accepted) refused++;
        }
        Assert.True(refused >= 1, "the 1.1 set contains a vector that must be refused");
    }

    [Fact]
    public void ReceiptVectors_ClientIsNamedByFingerprint_InVersion11()
    {
        foreach (var v in Load("receipts.json")["vectors"]!.AsArray())
        {
            var signed = v!["receipt"]!["signed"]!;
            Assert.Null(signed["client_entity_id"]);
            Assert.Equal((string)v["request"]!["client"]!["fingerprint"]!, (string)signed["client_fingerprint"]!);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((string)v["client_id"]!)))[..8], (string)signed["client_fingerprint"]!);
        }
    }

    [Fact]
    public void LimitsChangeVector_RequestHashIsSha256OfCanonicalBody()
    {
        foreach (var file in new[] { "receipts-1.0.json", "receipts.json" })
        {
            var v = Load(file)["vectors"]!.AsArray().Single(x => x!["limits_request_body"] is not null)!;
            var body = JsonCanonicalizer.Canonicalize(v["limits_request_body"]);
            var expected = (string)v["request"]!["authorization_details"]![0]!["request_hash"]!;
            Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))));
        }
    }

    [Fact]
    public void DeviceSignedVectors_SignatureAttestationAndKeyUse_GiveTheExpectedVerdict()
    {
        var jwks = Load("jwks.json")["keys"]!.AsArray();
        var file = Load("device-signed.json");
        var all = file["settings_changes"]!.AsArray().Concat(file["device_approvals"]!.AsArray()).ToList();
        Assert.True(all.Count >= 6);
        foreach (var v in all)
        {
            var name = (string)v!["name"]!;
            var envelope = v["envelope"]!;
            var canonical = JsonCanonicalizer.Canonicalize(envelope["signed"]);
            Assert.True((string)v["canonical"]! == canonical, name + ": canonical form");
            Assert.Equal((string)v["canonical_sha256"]!, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
            var dev = envelope["device"]!;
            var signatureValid = VerifyEs256(dev["public_key"]!, Encoding.UTF8.GetBytes(canonical), (string)envelope["signature"]!);
            var payload = VerifiedAttestation((string)dev["attestation"]!, jwks);
            Assert.Equal((string)dev["kid"]!, (string)payload["device_kid"]!);
            Assert.Equal(Thumbprint(dev["public_key"]!), (string)payload["device_jwk_thumbprint"]!);
            var signer = (string?)envelope["signed"]!["kid"] ?? (string)envelope["signed"]!["approver_kid"]!;
            var verdict = signatureValid && (string)payload["biatec_key_use"]! == "approve" && signer == (string)dev["kid"]!;
            // The pairing code a device approval carries must be the one derived from the keys and nonce it names.
            if ((string)envelope["signed"]!["type"]! == "biatec_device_approval")
                verdict &= (string)envelope["signed"]!["pairing_code"]! == PairingCode((string)envelope["signed"]!["new_approval_jwk_thumbprint"]!, (string)envelope["signed"]!["new_presence_jwk_thumbprint"]!, (string)envelope["signed"]!["nonce"]!);
            Assert.True((bool)v["expected"]!["accepted"]! == verdict, name + ": expected verdict");
        }
    }

    private static string PairingCode(string approvalThumbprint, string presenceThumbprint, string nonce)
    {
        var d = SHA256.HashData(Encoding.UTF8.GetBytes($"{approvalThumbprint}.{presenceThumbprint}.{nonce}"));
        var n = ((uint)d[0] << 24) | ((uint)d[1] << 16) | ((uint)d[2] << 8) | d[3];
        return (n % 1_000_000u).ToString("D6");
    }

    [Fact]
    public void PairingCode_WorkedExample_IsReproduced()
    {
        var p = Load("device-signed.json")["pairing_code"]!;
        var input = $"{(string)p["new_approval_jwk_thumbprint"]!}.{(string)p["new_presence_jwk_thumbprint"]!}.{(string)p["nonce"]!}";
        Assert.Equal((string)p["input"]!, input);
        Assert.Equal((string)p["sha256_hex"]!, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input))));
        Assert.Equal((string)p["code"]!, PairingCode((string)p["new_approval_jwk_thumbprint"]!, (string)p["new_presence_jwk_thumbprint"]!, (string)p["nonce"]!));
        Assert.Equal(6, ((string)p["code"]!).Length);
    }

    [Fact]
    public void EnrollmentProofVectors_SignatureUnderTheHeaderKey_AndPresenceThumbprint()
    {
        foreach (var v in Load("device-signed.json")["enrollment_proofs"]!.AsArray())
        {
            var name = (string)v!["name"]!;
            var parts = ((string)v["jws"]!).Split('.');
            Assert.Equal(3, parts.Length);
            var header = JsonNode.Parse(B64Url(parts[0]))!;
            var payload = JsonNode.Parse(B64Url(parts[1]))!;
            Assert.Equal("biatec-device-enroll+jwt", (string)header["typ"]!);
            var signatureValid = VerifyEs256(header["jwk"]!, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), parts[2]);
            var presenceBound = (string)payload["presence_jwk_thumbprint"]! == Thumbprint(v["presence_public_key"]!);
            Assert.True(presenceBound, name + ": the proof names the presence key by thumbprint");
            Assert.True((bool)v["expected"]!["accepted"]! == signatureValid, name + ": expected verdict");
        }
    }
}
