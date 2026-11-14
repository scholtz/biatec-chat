using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Biatec.Chat.Core.Canonical;
using Biatec.Chat.Tools.Consent;

namespace Biatec.Chat.Tests.Consent;

/// <summary>What a test receipt says and how it is (mis)built. Defaults give a valid 1.1 approval; every member a test may alter is a field.</summary>
public sealed class ReceiptSpec
{
    public string Version = "1.1";
    public string Decision = "approve";
    public string Sub = "user-1";
    public string AuthReqId = "ciba_8257c10231dd9b46e2f877484cf0b2a3";
    public string ClientEntityId = "biatec-chat";
    public string BindingCode = "483920";
    public long Expiry;
    public string GroupHash = "BJPMYYUBSMQWYXMEBZT2ZHFZTWCIJAFAIMUZEGOJZKJPRBKU5KNA";
    public string Network = "algorand-testnet";
    public string Sender = "YKBLPFVKZFZFGOVHQK6L63YX67B76YBZCSYYGU4WD7QC3PRRP4U7Q2DCWE";
    public string Origin = "https://app.biatec.io";
    public string BindingMessage = "";
    public string Issuer = "https://oidc.example.test";
    /// <summary>Unix seconds the attestation is minted at; it expires a year later.</summary>
    public long IssuedAt;

    /// <summary>The key of keys.json whose public half is the receipt's <c>device.public_key</c>: "device" (approval key) or "presence".</summary>
    public string DeviceKey = "device";
    /// <summary>When set, another key of keys.json signs the receipt while <c>device.public_key</c> stays <see cref="DeviceKey"/>.</summary>
    public string? SigningKey;
    /// <summary>The attested <c>biatec_key_use</c>; null = "approve" for the approval key and "presence" for the presence key (1.1) and none (1.0).</summary>
    public string? KeyUse;
    public bool OmitKeyUse;

    public string AttestationKid = "oidc-test-key-1";
    /// <summary>The key of keys.json that signs the attestation: "oidc" is the one in the JWKS.</summary>
    public string AttestationSigningKey = "oidc";

    /// <summary>Changes the signed object before it is signed (the receipt is then genuinely signed for the changed object).</summary>
    public Action<JsonObject>? MutateSigned;
    /// <summary>Changes the signed object after it was signed (the signature no longer fits).</summary>
    public Action<JsonObject>? TamperSigned;
    /// <summary>Changes the attestation payload before it is signed.</summary>
    public Action<JsonObject>? MutateAttestation;
}

/// <summary>Builds real signed consent receipts with the TEST-ONLY keys of packages/consent-protocol/vectors/keys.json, and gives the matching JWKS.</summary>
public static class ConsentTestKit
{
    public static string VectorsDir()
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

    public static JsonNode Load(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(VectorsDir(), name)))!;

    private static readonly Lazy<JsonNode> Keys = new(() => Load("keys.json"));

    public static byte[] B64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    public static string B64UrlEncode(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The JWKS of Biatec OIDC (the test key), as the verifier takes it.</summary>
    public static IReadOnlyList<OidcSigningKey> OidcKeys() =>
        Load("jwks.json")["keys"]!.AsArray().Select(k => new OidcSigningKey((string)k!["kid"]!, (string)k["kty"]!, (string)k["crv"]!, (string?)k["use"], (string)k["x"]!, (string)k["y"]!)).ToList();

    public static JsonObject PublicJwk(string key)
    {
        var k = Keys.Value[key]!;
        return new JsonObject { ["kty"] = (string)k["kty"]!, ["crv"] = (string)k["crv"]!, ["x"] = (string)k["x"]!, ["y"] = (string)k["y"]! };
    }

    public static string Kid(string key) => (string)Keys.Value[key]!["kid"]!;

    private static string SignEs256(string key, byte[] data)
    {
        var k = Keys.Value[key]!;
        using var ec = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256, D = B64Url((string)k["d"]!),
            Q = new ECPoint { X = B64Url((string)k["x"]!), Y = B64Url((string)k["y"]!) },
        });
        return B64UrlEncode(ec.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>RFC 7638 thumbprint, written out by hand so the test does not lean on the code it checks.</summary>
    public static string Thumbprint(JsonNode jwk) =>
        B64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes($"{{\"crv\":\"{(string)jwk["crv"]!}\",\"kty\":\"{(string)jwk["kty"]!}\",\"x\":\"{(string)jwk["x"]!}\",\"y\":\"{(string)jwk["y"]!}\"}}")));

    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The receipt as JSON text, signed and attested as <paramref name="s"/> says.</summary>
    public static string Build(ReceiptSpec s) => BuildNode(s).ToJsonString();

    public static JsonObject BuildNode(ReceiptSpec s)
    {
        var is11 = s.Version == "1.1";
        var money = new JsonObject { ["amount"] = "4.00", ["currency"] = "EUR" };
        var asset = new JsonObject { ["id"] = "0", ["symbol"] = "ALGO", ["decimals"] = 6 };
        var receiver = "WSNJGAWN52IJMHAEFZIOPCS3IXSHS4C46QDNSKWDEWQ3E6M4HUNLYTHJXE";
        JsonObject details = is11
            ? new JsonObject
            {
                ["type"] = "biatec_transaction", ["network"] = s.Network, ["group_hash"] = s.GroupHash, ["sender"] = s.Sender,
                ["transfers"] = new JsonArray(new JsonObject { ["receiver"] = receiver, ["asset"] = asset.DeepClone(), ["amount"] = "5.000000", ["fiat_value"] = money.DeepClone() }),
                ["applications"] = new JsonArray(), ["value_known"] = true, ["fiat_value"] = money.DeepClone(), ["origin"] = s.Origin,
            }
            : new JsonObject
            {
                ["type"] = "biatec_transaction", ["network"] = s.Network, ["group_hash"] = s.GroupHash, ["sender"] = s.Sender,
                ["receiver"] = receiver, ["asset"] = asset.DeepClone(), ["amount"] = "5.000000", ["fiat_value"] = money.DeepClone(),
            };
        var signed = new JsonObject
        {
            ["authorization_details"] = new JsonArray(details), ["auth_req_id"] = s.AuthReqId,
            [is11 ? "client_fingerprint" : "client_entity_id"] = is11 ? ConsentReceiptVerifier.ClientFingerprint(s.ClientEntityId) : s.ClientEntityId,
            ["binding_code"] = s.BindingCode, ["decision"] = s.Decision, ["expiry"] = s.Expiry, ["sub"] = s.Sub, ["binding_message"] = s.BindingMessage,
        };
        s.MutateSigned?.Invoke(signed);
        var signature = SignEs256(s.SigningKey ?? s.DeviceKey, Encoding.UTF8.GetBytes(JsonCanonicalizer.Canonicalize(signed)));
        s.TamperSigned?.Invoke(signed);

        var publicKey = PublicJwk(s.DeviceKey);
        var use = s.OmitKeyUse ? null : s.KeyUse ?? (is11 ? (s.DeviceKey == "presence" ? "presence" : "approve") : null);
        var claims = new JsonObject
        {
            ["iss"] = s.Issuer, ["sub"] = s.Sub, ["device_kid"] = Kid(s.DeviceKey), ["device_jwk_thumbprint"] = Thumbprint(publicKey),
        };
        if (use is not null) claims["biatec_key_use"] = use;
        claims["iat"] = s.IssuedAt;
        claims["exp"] = s.IssuedAt + 365L * 24 * 3600;
        s.MutateAttestation?.Invoke(claims);
        var header = new JsonObject { ["alg"] = "ES256", ["kid"] = s.AttestationKid, ["typ"] = ConsentReceiptVerifier.AttestationTyp };
        var input = B64UrlEncode(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + B64UrlEncode(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var attestation = input + "." + SignEs256(s.AttestationSigningKey, Encoding.ASCII.GetBytes(input));

        var device = new JsonObject { ["kid"] = Kid(s.DeviceKey) };
        if (is11) device["key_use"] = use;
        device["public_key"] = publicKey;
        device["attestation"] = attestation;
        return new JsonObject
        {
            ["version"] = s.Version, ["signed"] = signed, ["alg"] = "ES256", ["signature"] = signature, ["issued_at"] = s.IssuedAt, ["device"] = device,
        };
    }
}
