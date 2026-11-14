using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Biatec.Chat.Core.Canonical;

namespace Biatec.Chat.Tools.Consent;

/// <summary>An EC signing key of the Biatec OIDC JWKS (public members only).</summary>
public sealed record OidcSigningKey(string Kid, string Kty, string Crv, string? Use, string X, string Y);

/// <summary>Why a receipt was accepted or refused. One distinct value per refusal; <see cref="ReceiptVerdictExtensions.Code"/> gives the stable text.</summary>
public enum ReceiptVerdict
{
    Valid,

    /// <summary>Not produced by the verifier: the caller could not get the Biatec OIDC signing keys, so nothing could be verified (a refusal).</summary>
    KeysUnavailable,

    // Step 1: shape.
    Malformed,
    UnsupportedVersion,
    VersionShapeMismatch,
    BadSignatureEncoding,

    // Step 2: attestation.
    AttestationMalformed,
    AttestationHeaderInvalid,
    AttestationUnknownKey,
    AttestationSignatureInvalid,
    AttestationIssuerMismatch,
    AttestationExpired,
    AttestationSubjectMismatch,
    AttestationDeviceMismatch,
    AttestationKeyMismatch,
    KeyUseInvalid,
    ApprovalKeyRequired,

    // Step 3: the device signature.
    DeviceSignatureInvalid,

    // Step 4 and 5: the stored request.
    DecisionNotAllowed,
    AuthReqIdMismatch,
    ClientMismatch,
    SubjectMismatch,
    BindingCodeMismatch,
    ReceiptExpired,
    BindingMessageMismatch,
    DetailsMismatch,
    GroupHashMismatch,
    NetworkMismatch,
    SenderMismatch,
    OriginMismatch,
}

public static class ReceiptVerdictExtensions
{
    /// <summary>The snake_case failure code written to evidence and logs, for example <c>group_hash_mismatch</c>. <c>valid</c> for an accepted receipt.</summary>
    public static string Code(this ReceiptVerdict v)
    {
        var name = v.ToString();
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}

/// <summary>
/// What the backend itself asked for. The receipt is compared with this, never with data it supplies (spec section 8.1, step 5).
/// </summary>
/// <param name="Issuer">The configured Biatec OIDC issuer (<c>Oidc:Issuer</c>).</param>
/// <param name="Now">The trusted clock.</param>
/// <param name="AuthReqId">The auth_req_id of the stored consent request.</param>
/// <param name="ClientEntityId">The tenant's client entity id: protocol 1.1 names the client by the fingerprint of it, 1.0 names it directly.</param>
/// <param name="Sub">The session's subject.</param>
/// <param name="BindingCode">The code Biatec OIDC issued; null skips the comparison (a denial carries a typed or no code).</param>
/// <param name="BindingMessage">The binding message of the request (the empty string for a step-up entry).</param>
/// <param name="GroupHash">The group id of the previewed group, base32 without padding.</param>
/// <param name="Origin">The origin stored with the consent request (compared in protocol 1.1, whose transaction details sign it).</param>
/// <param name="Decisions">The decisions that are acceptable on this path.</param>
/// <param name="ClientId">The OAuth <c>client_id</c> of the tenant: the schema defines the 1.1 <c>client_fingerprint</c> as the SHA-256 of it (null: use <paramref name="ClientEntityId"/>).</param>
/// <param name="ExpiryGraceSeconds">How long after its <c>expiry</c> an approval is still accepted: the approval happened inside the window, the poll that redeemed it may come one poll interval later. Applies to the receipt expiry only, never to the attestation.</param>
/// <param name="CheckExpiry">Whether a receipt whose <c>expiry</c> has passed is refused (approvals; a denial stays a denial).</param>
public sealed record ConsentReceiptExpectation(string Issuer, DateTimeOffset Now, string AuthReqId, string ClientEntityId, string Sub, string? BindingCode,
    string BindingMessage, string GroupHash, string Network, string Sender, string Origin, IReadOnlyCollection<string> Decisions, bool CheckExpiry, string? ClientId = null, int ExpiryGraceSeconds = 0);

/// <summary>
/// Verifies a Biatec consent receipt (spec section 8.1) in a way that never throws: any malformed input is a verdict. Both receipt versions are
/// understood: "1.1" (client named by <c>client_fingerprint</c>, two device keys, attested key use) and the frozen "1" (client named by
/// <c>client_entity_id</c>, no key use). <c>version</c> is not signed, so the signed object's shape must agree with it and an attested key
/// use is honoured whatever the version says. Port of <c>verifyReceipt</c> and <c>verifyApproval</c> in packages/consent-protocol/scripts/lib.mjs.
/// </summary>
public static class ConsentReceiptVerifier
{
    public const string AttestationTyp = "biatec-device-attestation+jwt";
    private const long MaxSafeInteger = 9007199254740991L;
    private const int ClockSkewSeconds = 300;
    private const int MaxReceiptChars = 64 * 1024;

    private static readonly string[] EnvelopeMembers = ["version", "signed", "alg", "signature", "device", "issued_at"];
    private static readonly string[] SignedMembers11 = ["authorization_details", "auth_req_id", "client_fingerprint", "binding_code", "decision", "expiry", "sub", "binding_message"];
    private static readonly string[] SignedMembers10 = ["authorization_details", "auth_req_id", "client_entity_id", "binding_code", "decision", "expiry", "sub", "binding_message"];
    private static readonly Regex Signature = new("^[A-Za-z0-9_-]{86}$", RegexOptions.CultureInvariant);
    private static readonly Regex Coordinate = new("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant);
    private static readonly Regex Jws = new("^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Fingerprint = new("^[0-9a-f]{16}$", RegexOptions.CultureInvariant);
    private static readonly Regex Code6 = new("^[0-9]{6}$", RegexOptions.CultureInvariant);

    /// <summary>The fingerprint a 1.1 receipt names the client by: the first 16 lowercase hex characters of the SHA-256 of the client id.</summary>
    public static string ClientFingerprint(string clientId) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(clientId)))[..16];

    /// <summary>The group id as Biatec OIDC writes it in <c>group_hash</c>: RFC 4648 base32, upper case, no padding.</summary>
    public static string GroupHash(ReadOnlySpan<byte> groupId) => Base32(groupId);

    /// <summary>Steps 1 to 3 only: shape, attestation and device signature. Says nothing about what the receipt is for.</summary>
    public static ReceiptVerdict VerifyAuthenticity(string receiptJson, IReadOnlyList<OidcSigningKey> keys, string issuer, DateTimeOffset now) =>
        Run(receiptJson, keys, issuer, now, null);

    /// <summary>All steps: authenticity, then the match against what the backend asked for.</summary>
    public static ReceiptVerdict Verify(string receiptJson, IReadOnlyList<OidcSigningKey> keys, ConsentReceiptExpectation expected) =>
        Run(receiptJson, keys, expected.Issuer, expected.Now, expected);

    private static ReceiptVerdict Run(string receiptJson, IReadOnlyList<OidcSigningKey> keys, string issuer, DateTimeOffset now, ConsentReceiptExpectation? expected)
    {
        try
        {
            if (string.IsNullOrEmpty(receiptJson) || receiptJson.Length > MaxReceiptChars) return ReceiptVerdict.Malformed;
            JsonNode? root;
            try { root = JsonNode.Parse(receiptJson); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException) { return ReceiptVerdict.Malformed; }
            if (root is not JsonObject receipt) return ReceiptVerdict.Malformed;

            var shape = CheckShape(receipt, out var is11);
            if (shape != ReceiptVerdict.Valid) return shape;
            var signed = (JsonObject)receipt["signed"]!;
            var device = (JsonObject)receipt["device"]!;
            var decision = Str(signed["decision"])!;

            var attestation = CheckAttestation(device, signed, is11, decision, keys, issuer, now);
            if (attestation != ReceiptVerdict.Valid) return attestation;

            var signature = Decode(Str(receipt["signature"])!);
            if (signature is null || signature.Length != 64) return ReceiptVerdict.BadSignatureEncoding;
            if (!VerifyEs256((JsonObject)device["public_key"]!, Encoding.UTF8.GetBytes(JsonCanonicalizer.Canonicalize(signed)), signature))
                return ReceiptVerdict.DeviceSignatureInvalid;

            return expected is null ? ReceiptVerdict.Valid : Match(signed, is11, expected);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never throws on malformed input: anything unforeseen is a refusal.
            return ReceiptVerdict.Malformed;
        }
    }

    // ---- Step 1: the shape the 1.1 and 1.0 schemas allow, exactly. ----

    private static ReceiptVerdict CheckShape(JsonObject receipt, out bool is11)
    {
        is11 = false;
        if (!SameMembers(receipt, EnvelopeMembers)) return ReceiptVerdict.Malformed;
        var version = Str(receipt["version"]);
        if (version is not ("1.1" or "1")) return ReceiptVerdict.UnsupportedVersion;
        is11 = version == "1.1";
        if (Str(receipt["alg"]) != "ES256") return ReceiptVerdict.Malformed;
        var sig = Str(receipt["signature"]);
        if (sig is null || !Signature.IsMatch(sig)) return ReceiptVerdict.BadSignatureEncoding;
        if (Int(receipt["issued_at"]) is not { } issued || issued < 0 || issued > MaxSafeInteger) return ReceiptVerdict.Malformed;

        if (receipt["device"] is not JsonObject device || receipt["signed"] is not JsonObject signed) return ReceiptVerdict.Malformed;
        // Device: kid, public_key, attestation, and key_use exactly for 1.1.
        if (!device.All(m => m.Key is "kid" or "key_use" or "public_key" or "attestation")) return ReceiptVerdict.Malformed;
        if (!device.ContainsKey("kid") || !device.ContainsKey("public_key") || !device.ContainsKey("attestation")) return ReceiptVerdict.Malformed;
        if (Str(device["kid"]) is not { Length: > 0 }) return ReceiptVerdict.Malformed;
        if (is11 != device.ContainsKey("key_use")) return ReceiptVerdict.Malformed;
        if (is11 && Str(device["key_use"]) is not ("approve" or "presence")) return ReceiptVerdict.Malformed;
        if (device["public_key"] is not JsonObject pk || !SameMembers(pk, ["kty", "crv", "x", "y"])
            || Str(pk["kty"]) != "EC" || Str(pk["crv"]) != "P-256"
            || Str(pk["x"]) is not { } x || !Coordinate.IsMatch(x) || Str(pk["y"]) is not { } y || !Coordinate.IsMatch(y)) return ReceiptVerdict.Malformed;
        if (Str(device["attestation"]) is not { } att || !Jws.IsMatch(att)) return ReceiptVerdict.Malformed;

        // Signed: the members of the version, plus `remember` on a 1.1 approval. `version` is unsigned, so the shape has to agree with it.
        var has11 = signed.ContainsKey("client_fingerprint");
        var has10 = signed.ContainsKey("client_entity_id");
        if (has11 == has10) return ReceiptVerdict.Malformed;
        if (has11 != is11) return ReceiptVerdict.VersionShapeMismatch;
        var members = is11 ? SignedMembers11 : SignedMembers10;
        if (signed.Count != members.Length + (signed.ContainsKey("remember") ? 1 : 0) || !members.All(signed.ContainsKey)) return ReceiptVerdict.Malformed;
        var decision = Str(signed["decision"]);
        if (decision is not ("approve" or "deny" or "code_failed")) return ReceiptVerdict.Malformed;
        if (signed["authorization_details"] is not JsonArray details || details.Count == 0 || details.Any(d => d is not JsonObject o || Str(o["type"]) is null)) return ReceiptVerdict.Malformed;
        if (Str(signed["auth_req_id"]) is not { Length: >= 16 }) return ReceiptVerdict.Malformed;
        if (is11 ? Str(signed["client_fingerprint"]) is not { } f || !Fingerprint.IsMatch(f) : Str(signed["client_entity_id"]) is not { Length: > 0 }) return ReceiptVerdict.Malformed;
        if (Str(signed["binding_code"]) is not { } code || !(decision == "deny" ? code.Length == 0 || Code6.IsMatch(code) : Code6.IsMatch(code))) return ReceiptVerdict.Malformed;
        if (Int(signed["expiry"]) is not { } expiry || expiry < 0 || expiry > MaxSafeInteger) return ReceiptVerdict.Malformed;
        if (Str(signed["sub"]) is not { Length: > 0 }) return ReceiptVerdict.Malformed;
        if (Str(signed["binding_message"]) is not { } message || CodePoints(message) > 140) return ReceiptVerdict.Malformed;
        if (signed.ContainsKey("remember"))
        {
            // Trust of applications rides only with a 1.1 approval, never with a denial.
            if (!is11 || decision != "approve") return ReceiptVerdict.Malformed;
            if (signed["remember"] is not JsonObject r || !SameMembers(r, ["applications"]) || r["applications"] is not JsonArray apps || apps.Count is < 1 or > 16) return ReceiptVerdict.Malformed;
            var seen = new HashSet<long>();
            foreach (var a in apps)
                if (Int(a) is not { } id || id < 1 || id > MaxSafeInteger || !seen.Add(id)) return ReceiptVerdict.Malformed;
        }
        return ReceiptVerdict.Valid;
    }

    // ---- Step 2: the attestation. ----

    private static ReceiptVerdict CheckAttestation(JsonObject device, JsonObject signed, bool is11, string decision, IReadOnlyList<OidcSigningKey> keys, string issuer, DateTimeOffset now)
    {
        var parts = Str(device["attestation"])!.Split('.');
        if (parts.Length != 3) return ReceiptVerdict.AttestationMalformed;
        JsonNode? header, payloadNode;
        try
        {
            var h = Decode(parts[0]); var p = Decode(parts[1]);
            if (h is null || p is null) return ReceiptVerdict.AttestationMalformed;
            header = JsonNode.Parse(h); payloadNode = JsonNode.Parse(p);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException) { return ReceiptVerdict.AttestationMalformed; }
        if (header is not JsonObject head || payloadNode is not JsonObject payload) return ReceiptVerdict.AttestationMalformed;
        if (Str(head["kid"]) is not { Length: > 0 } kid) return ReceiptVerdict.AttestationMalformed;
        if (Str(head["alg"]) != "ES256" || Str(head["typ"]) != AttestationTyp) return ReceiptVerdict.AttestationHeaderInvalid;

        // The signing key must be an EC P-256 signature key of the Biatec OIDC JWKS.
        var key = keys.FirstOrDefault(k => k.Kid == kid && k.Kty == "EC" && k.Crv == "P-256" && (k.Use is null || k.Use == "sig"));
        if (key is null) return ReceiptVerdict.AttestationUnknownKey;
        var sig = Decode(parts[2]);
        if (sig is null || sig.Length != 64 || !VerifyEs256(key.X, key.Y, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), sig)) return ReceiptVerdict.AttestationSignatureInvalid;

        if (Str(payload["iss"]) is not { } iss || !string.Equals(iss.TrimEnd('/'), issuer.TrimEnd('/'), StringComparison.Ordinal)) return ReceiptVerdict.AttestationIssuerMismatch;
        var nowSeconds = now.ToUnixTimeSeconds();
        if (payload["exp"] is not JsonValue expNode || !expNode.TryGetValue<double>(out var exp) || exp <= nowSeconds) return ReceiptVerdict.AttestationExpired;
        if (Str(payload["sub"]) is not { } sub) return ReceiptVerdict.AttestationMalformed;
        if (payload["iat"] is { } iatNode && (Int(iatNode) is not { } iat || iat > nowSeconds + ClockSkewSeconds)) return ReceiptVerdict.AttestationMalformed;

        // It binds this receipt's subject and this device key.
        if (!string.Equals(sub, Str(signed["sub"]), StringComparison.Ordinal)) return ReceiptVerdict.AttestationSubjectMismatch;
        if (Str(payload["device_kid"]) != Str(device["kid"])) return ReceiptVerdict.AttestationDeviceMismatch;
        if (Str(payload["device_jwk_thumbprint"]) != Thumbprint((JsonObject)device["public_key"]!)) return ReceiptVerdict.AttestationKeyMismatch;

        // Key use. 1.1 names the attested use in device.key_use; a 1.0 device has none. An attested use is honoured whatever the version says.
        string? use = null;
        if (payload.ContainsKey("biatec_key_use"))
        {
            use = Str(payload["biatec_key_use"]);
            if (use is not ("approve" or "presence")) return ReceiptVerdict.KeyUseInvalid;
        }
        if (is11 && (use is null || Str(device["key_use"]) != use)) return ReceiptVerdict.KeyUseInvalid;
        // An approval needs an approval key; only a 1.0 device, whose attestation carries no use, approves without one.
        if (decision == "approve" && use is not null && use != "approve") return ReceiptVerdict.ApprovalKeyRequired;
        return ReceiptVerdict.Valid;
    }

    // ---- Steps 4 and 5: the stored request. ----

    private static ReceiptVerdict Match(JsonObject signed, bool is11, ConsentReceiptExpectation e)
    {
        var decision = Str(signed["decision"])!;
        if (!e.Decisions.Contains(decision)) return ReceiptVerdict.DecisionNotAllowed;
        if (Str(signed["auth_req_id"]) != e.AuthReqId) return ReceiptVerdict.AuthReqIdMismatch;

        // The stored request decides what names the client, with the version agreeing with the signed shape: 1.1 a fingerprint, 1.0 the entity id.
        if (string.IsNullOrEmpty(e.ClientEntityId)) return ReceiptVerdict.ClientMismatch;
        var client = is11 ? Str(signed["client_fingerprint"]) : Str(signed["client_entity_id"]);
        if (client != (is11 ? ClientFingerprint(e.ClientId ?? e.ClientEntityId) : e.ClientEntityId)) return ReceiptVerdict.ClientMismatch;

        if (string.IsNullOrEmpty(e.Sub) || Str(signed["sub"]) != e.Sub) return ReceiptVerdict.SubjectMismatch;
        if (e.BindingCode is not null)
        {
            // Number matching in constant time; an unset stored code never matches.
            var expectedCode = Encoding.UTF8.GetBytes(e.BindingCode);
            if (expectedCode.Length == 0 || !CryptographicOperations.FixedTimeEquals(expectedCode, Encoding.UTF8.GetBytes(Str(signed["binding_code"])!))) return ReceiptVerdict.BindingCodeMismatch;
        }
        if (e.CheckExpiry && (Int(signed["expiry"]) is not { } expiry || expiry + e.ExpiryGraceSeconds <= e.Now.ToUnixTimeSeconds())) return ReceiptVerdict.ReceiptExpired;
        if (Str(signed["binding_message"]) != e.BindingMessage) return ReceiptVerdict.BindingMessageMismatch;

        // Exactly one detail, a biatec_transaction, for the group, network, sender (and, in 1.1, origin) that was previewed.
        var details = (JsonArray)signed["authorization_details"]!;
        if (details.Count != 1 || details[0] is not JsonObject d || Str(d["type"]) != "biatec_transaction") return ReceiptVerdict.DetailsMismatch;
        if (Str(d["group_hash"]) != e.GroupHash) return ReceiptVerdict.GroupHashMismatch;
        if (Str(d["network"]) != e.Network) return ReceiptVerdict.NetworkMismatch;
        if (Str(d["sender"]) != e.Sender) return ReceiptVerdict.SenderMismatch;
        // A 1.1 transaction signs the origin the user saw; the 1.0 shape has no such member.
        if (is11 && Str(d["origin"]) != e.Origin) return ReceiptVerdict.OriginMismatch;
        return ReceiptVerdict.Valid;
    }

    // ---- Helpers. ----

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>An integer JSON number; booleans, strings and fractions are not.</summary>
    private static long? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    private static bool SameMembers(JsonObject o, string[] members) => o.Count == members.Length && members.All(o.ContainsKey);

    private static int CodePoints(string s)
    {
        var n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }

    /// <summary>Strict unpadded base64url: any stray character or non-canonical trailing bits is refused (null).</summary>
    private static byte[]? Decode(string text)
    {
        foreach (var c in text)
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return null;
        if (text.Length % 4 == 1) return null;
        try
        {
            var padded = text.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(padded);
            return Encode(bytes) == text ? bytes : null;
        }
        catch (FormatException) { return null; }
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>RFC 7638 thumbprint of an EC P-256 public JWK, over the RFC 8785 form of its required members.</summary>
    private static string Thumbprint(JsonObject jwk)
    {
        var members = new JsonObject { ["crv"] = Str(jwk["crv"]), ["kty"] = Str(jwk["kty"]), ["x"] = Str(jwk["x"]), ["y"] = Str(jwk["y"]) };
        return Encode(SHA256.HashData(Encoding.UTF8.GetBytes(JsonCanonicalizer.Canonicalize(members))));
    }

    private static bool VerifyEs256(JsonObject jwk, byte[] data, byte[] signature) => VerifyEs256(Str(jwk["x"])!, Str(jwk["y"])!, data, signature);

    private static bool VerifyEs256(string x, string y, byte[] data, byte[] signature)
    {
        try
        {
            var xb = Decode(x); var yb = Decode(y);
            if (xb is null || yb is null || xb.Length != 32 || yb.Length != 32) return false;
            using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = xb, Y = yb } });
            return ec.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException) { return false; } // not a point on the curve
    }

    private static string Base32(ReadOnlySpan<byte> data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { sb.Append(alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
}
