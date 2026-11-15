using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Biatec.Chat.Core.Canonical;

namespace Biatec.Chat.Tests.Canonical;

/// <summary>
/// Protocol 1.2 (relying parties): the receipt vectors (payment, login, action), the link statements and the text rules of
/// packages/consent-protocol/vectors, checked independently of the JavaScript reference verifier with the backend RFC 8785
/// implementation. Both verification modes are re-implemented here from the spec (sections 8.4, 8.5 and 14.3): attestation mode and
/// pinned mode, and every vector must get its <c>expected</c> verdict (the pinned one is <c>expected_pinned</c> when present).
/// </summary>
public partial class ConsentProtocolVectorTests
{
    private static string DetailsHash(JsonNode details) =>
        B64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(JsonCanonicalizer.Canonicalize(details))));

    /// <summary>The mod-97-10 check of an IBAN in electronic form (spec section 3.3).</summary>
    private static bool IbanValid(string iban)
    {
        if (!Regex.IsMatch(iban, "^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$")) return false;
        var rearranged = iban[4..] + iban[..4];
        var rem = 0;
        foreach (var ch in rearranged)
            rem = ch >= 'A' ? (rem * 100 + ch - 55) % 97 : (rem * 10 + (ch - '0')) % 97;
        return rem == 1;
    }

    /// <summary>The core text rules every implementation applies (spec section 15, rules 1 to 6): NFC, 1..max code points, no control, format (bidirectional controls, zero-width characters) or line-break characters.</summary>
    private static bool CoreTextAccepted(string text, int max)
    {
        if (text.Length == 0 || !text.IsNormalized(NormalizationForm.FormC)) return false;
        var runes = text.EnumerateRunes().ToList();
        if (runes.Count > max) return false;
        return runes.All(r => Rune.GetUnicodeCategory(r) is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator));
    }

    private static bool DetailsWellFormed(JsonNode d)
    {
        var o = d.AsObject();
        var type = (string)o["type"]!;
        bool Text(JsonNode? n, int max) => n is JsonValue v && v.TryGetValue<string>(out var s) && CoreTextAccepted(s, max);
        if (o.ContainsKey("origin") && !Regex.IsMatch((string)o["origin"]!, "^https://[a-z0-9.-]+$")) return false;
        switch (type)
        {
            case "biatec_payment":
                var amount = o["instructed_amount"]!;
                if (!Regex.IsMatch((string)amount["amount"]!, @"^(0|[1-9][0-9]{0,14})(\.[0-9]{1,2})?$") || !Regex.IsMatch((string)amount["amount"]!, "[1-9]")) return false;
                if (!Regex.IsMatch((string)amount["currency"]!, "^[A-Z]{3}$") || !Text(o["creditor_name"], 70)) return false;
                var account = o["creditor_account"]!.AsObject();
                if (account.ContainsKey("iban") ? !IbanValid((string)account["iban"]!) : !(Text(account["identification"], 34) && Text(account["scheme"], 20))) return false;
                if (o.ContainsKey("remittance") && !Text(o["remittance"], 140)) return false;
                return !o.ContainsKey("execution_date") || DateOnly.TryParseExact((string)o["execution_date"]!, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            case "biatec_login":
                return !o.ContainsKey("context") || o["context"]!.AsObject().All(kv => Text(kv.Value, 80));
            case "biatec_action":
                var lines = o["lines"]!.AsArray();
                return Text(o["title"], 60) && lines.Count is >= 1 and <= 8 && lines.All(l => Text(l!["label"], 30) && Text(l["value"], 80));
            default:
                return false;
        }
    }

    private static bool SamePins(JsonNode pins, JsonNode jwk, bool approve, string signature, byte[] data, string? kid = null) =>
        pins.AsArray().Any(p => (kid is null || (string)p!["kid"]! == kid) && (!approve || (string)p!["key_use"]! == "approve") && VerifyEs256(p!["jwk"] ?? p["public_key"]!, data, signature));

    [Fact]
    public void RelyingPartyReceiptVectors_CanonicalFormHashesAndDetailsHash()
    {
        var file = Load("receipts-1.2.json");
        var vectors = file["vectors"]!.AsArray();
        Assert.True(vectors.Count >= 10);
        foreach (var v in vectors)
        {
            var name = (string)v!["name"]!;
            var receipt = v["receipt"]!;
            var signed = receipt["signed"]!;
            Assert.Equal("1.2", (string)receipt["version"]!);
            var canonical = JsonCanonicalizer.Canonicalize(signed);
            Assert.True((string)v["canonical"]! == canonical, name + ": canonical form");
            Assert.Equal((string)v["canonical_sha256"]!, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
            Assert.True(VerifyEs256(receipt["device"]!["public_key"]!, Encoding.UTF8.GetBytes(canonical), (string)receipt["signature"]!), name + ": device signature");
            Assert.False(VerifyEs256(receipt["device"]!["public_key"]!, Encoding.UTF8.GetBytes(canonical.Replace("\"sub\":\"", "\"sub\":\"x")), (string)receipt["signature"]!));
            // exactly the nine members of a 1.2 signed object, no `remember`, an empty binding_message
            var members = signed.AsObject().Select(kv => kv.Key).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "auth_req_id", "authorization_details", "binding_code", "binding_message", "client_fingerprint", "decision", "details_hash", "expiry", "sub" }, members);
            Assert.Equal("", (string)signed["binding_message"]!);
            // details_hash: the stored request carries the hash of the stored object, which is the sent object plus the origin the server had
            var stored = v["request"]!["authorization_details"]![0]!;
            Assert.Equal((string)v["details_hash"]!, DetailsHash(stored));
            Assert.Equal((string)v["details_hash"]!, (string)v["request"]!["bound_hash"]!);
            var sent = JsonNode.Parse(v["sent_details"]!.ToJsonString())!.AsObject();
            if (v["server_origin"] is JsonValue origin) sent["origin"] = origin.GetValue<string>();
            Assert.Equal(JsonCanonicalizer.Canonicalize(stored), JsonCanonicalizer.Canonicalize(sent));
            // the hash signed with the details is the hash of the signed details: except in the vector that swaps a payee after the hash was made
            var recomputed = DetailsHash(signed["authorization_details"]![0]!);
            Assert.Equal(!name.Contains("iban-altered"), (string)signed["details_hash"]! == recomputed);
        }
    }

    [Fact]
    public void RelyingPartyReceiptVectors_BothModesGiveTheExpectedVerdict()
    {
        var jwks = Load("jwks.json")["keys"]!.AsArray();
        var file = Load("receipts-1.2.json");
        var pins = file["pinned_devices"]!;
        foreach (var v in file["vectors"]!.AsArray())
        {
            var name = (string)v!["name"]!;
            var receipt = v["receipt"]!;
            var signed = receipt["signed"]!;
            var approve = (string)signed["decision"]! == "approve";
            var data = Encoding.UTF8.GetBytes(JsonCanonicalizer.Canonicalize(signed));
            var details = signed["authorization_details"]![0]!;
            var hashOk = (string)signed["details_hash"]! == DetailsHash(details);
            var wellFormed = DetailsWellFormed(details);

            // attestation mode (section 8.4): signature, attestation bound to sub and key, key use, hash, details
            var dev = receipt["device"]!;
            var payload = VerifiedAttestation((string)dev["attestation"]!, jwks);
            var bound = VerifyEs256(dev["public_key"]!, data, (string)receipt["signature"]!)
                && (string)payload["sub"]! == (string)signed["sub"]! && (string)payload["device_kid"]! == (string)dev["kid"]!
                && Thumbprint(dev["public_key"]!) == (string)payload["device_jwk_thumbprint"]!
                && (string)payload["biatec_key_use"]! == (string)dev["key_use"]!;
            var attested = bound && hashOk && wellFormed && (!approve || (string)payload["biatec_key_use"]! == "approve");
            Assert.True((bool)v["expected"]!["accepted"]! == attested, name + ": attestation mode");

            // pinned mode (section 8.5): no attestation; the party's own object, hash, and a pinned key of the right use
            var sent = JsonNode.Parse(v["sent_details"]!.ToJsonString())!.AsObject();
            var expectedDetails = JsonCanonicalizer.Canonicalize(sent);
            var signedWithoutOrigin = JsonNode.Parse(details.ToJsonString())!.AsObject();
            signedWithoutOrigin.Remove("origin");
            var originMatches = (string?)details["origin"] == (string?)v["server_origin"];
            var pinned = hashOk && wellFormed && originMatches && JsonCanonicalizer.Canonicalize(signedWithoutOrigin) == expectedDetails
                && SamePins(pins, dev["public_key"]!, approve, (string)receipt["signature"]!, data);
            var expectedPinned = (v["expected_pinned"] ?? v["expected"])!["accepted"]!.GetValue<bool>();
            Assert.True(expectedPinned == pinned, name + ": pinned mode");
        }
    }

    [Fact]
    public void RelyingPartyReceiptVectors_AlteringAnySignedMemberBreaksTheSignature()
    {
        foreach (var v in Load("receipts-1.2.json")["vectors"]!.AsArray())
        {
            var receipt = v!["receipt"]!;
            var jwk = receipt["device"]!["public_key"]!;
            var signature = (string)receipt["signature"]!;
            foreach (var member in receipt["signed"]!.AsObject().Select(kv => kv.Key).ToList())
            {
                var copy = JsonNode.Parse(receipt["signed"]!.ToJsonString())!.AsObject();
                copy[member] = copy[member] switch
                {
                    JsonValue val when val.TryGetValue<long>(out var n) => n + 1,
                    JsonValue val when val.TryGetValue<string>(out var s) => s + "0",
                    JsonArray => new JsonArray(JsonNode.Parse("{\"type\":\"biatec_login\"}")),
                    _ => null,
                };
                Assert.False(VerifyEs256(jwk, Encoding.UTF8.GetBytes(JsonCanonicalizer.Canonicalize(copy)), signature), (string)v["name"]! + ": " + member);
            }
        }
    }

    [Fact]
    public void IbanVectors_OneChangedCharacterChangesTheHashAndFailsTheChecksum()
    {
        var file = Load("receipts-1.2.json");
        var a = file["iban_alteration"]!;
        var original = (string)a["original_iban"]!;
        var altered = (string)a["altered_iban"]!;
        Assert.True(IbanValid(original));
        Assert.False(IbanValid(altered));
        Assert.Equal(1, original.Zip(altered).Count(p => p.First != p.Second));
        var payment = file["vectors"]!.AsArray().Single(v => (string)v!["name"]! == "payment-approve")!["request"]!["authorization_details"]![0]!;
        var changed = JsonNode.Parse(payment.ToJsonString())!;
        changed["creditor_account"]!["iban"] = altered;
        Assert.Equal((string)a["original_details_hash"]!, DetailsHash(payment));
        Assert.Equal((string)a["altered_details_hash"]!, DetailsHash(changed));
        Assert.NotEqual(DetailsHash(payment), DetailsHash(changed));
        foreach (var good in new[] { "DE89370400440532013000", "GB82WEST12345698765432", "FR1420041010050500013M02606", "SK3112000000198742637541", "NO9386011117947" }) Assert.True(IbanValid(good), good);
        foreach (var bad in new[] { "DE89370400440532013001", "de89370400440532013000", "DE8937040044053201300", "DE00370400440532013000" }) Assert.False(IbanValid(bad), bad);
    }

    [Fact]
    public void LinkVectors_CanonicalFormSignatureAndBothModes()
    {
        var jwks = Load("jwks.json")["keys"]!.AsArray();
        var file = Load("link.json");
        var now = (long)file["now"]!;
        var pins = file["pinned_devices"]!;
        var vectors = file["vectors"]!.AsArray();
        Assert.True(vectors.Count >= 5);
        foreach (var v in vectors)
        {
            var name = (string)v!["name"]!;
            var envelope = v["envelope"]!;
            var signed = envelope["signed"]!;
            var canonical = JsonCanonicalizer.Canonicalize(signed);
            Assert.True((string)v["canonical"]! == canonical, name + ": canonical form");
            Assert.Equal((string)v["canonical_sha256"]!, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
            Assert.Equal(new[] { "binding_code", "client_fingerprint", "decision", "domain", "issued_at", "kid", "link_id", "nonce", "type" }, signed.AsObject().Select(kv => kv.Key).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal("biatec_link", (string)signed["type"]!);
            var data = Encoding.UTF8.GetBytes(canonical);
            var signature = (string)envelope["signature"]!;
            var dev = envelope["device"]!;
            Assert.True(VerifyEs256(dev["public_key"]!, data, signature), name + ": device signature");
            // altering any member breaks it (spec: "any one signed member")
            foreach (var member in signed.AsObject().Select(kv => kv.Key).ToList())
            {
                var copy = JsonNode.Parse(signed.ToJsonString())!.AsObject();
                copy[member] = copy[member] switch
                {
                    JsonValue val when val.TryGetValue<long>(out var n) => n + 1,
                    JsonValue val when val.TryGetValue<string>(out var s) => s + "0",
                    _ => "evil.example", // the null domain
                };
                Assert.False(VerifyEs256(dev["public_key"]!, Encoding.UTF8.GetBytes(JsonCanonicalizer.Canonicalize(copy)), signature), name + ": " + member);
            }

            var approve = (string)signed["decision"]! == "approve";
            var expected = v["expected_inputs"]!;
            // what the device was shown is what it signed: link, fingerprint, domain (null is a value), nonce, code, and a fresh statement
            var matches = (string)signed["link_id"]! == (string)expected["link_id"]! && (string)signed["client_fingerprint"]! == (string)expected["client_fingerprint"]!
                && (string?)signed["domain"] == (string?)expected["domain"] && (string)signed["nonce"]! == (string)expected["nonce"]!
                && (!approve || (string)signed["binding_code"]! == (string)expected["binding_code"]!)
                && Math.Abs(now - (long)signed["issued_at"]!) <= 300;
            Assert.Equal((string?)v["link_request"]!["client"]!["domain"], (string?)expected["domain"]);
            Assert.Equal((string)v["link_request"]!["link_id"]!, (string)signed["link_id"]!);

            var payload = VerifiedAttestation((string)dev["attestation"]!, jwks);
            var bound = (string)payload["sub"]! == (string)expected["sub"]! && (string)payload["device_kid"]! == (string)dev["kid"]!
                && Thumbprint(dev["public_key"]!) == (string)payload["device_jwk_thumbprint"]! && (string)payload["biatec_key_use"]! == (string)dev["key_use"]!
                && (string)signed["kid"]! == (string)dev["kid"]!;
            var attested = matches && bound && (!approve || (string)payload["biatec_key_use"]! == "approve");
            Assert.True((bool)v["expected"]!["accepted"]! == attested, name + ": attestation mode");
            var pinned = matches && SamePins(pins, dev["public_key"]!, approve, signature, data, (string)signed["kid"]!);
            Assert.True((bool)v["expected"]!["accepted"]! == pinned, name + ": pinned mode");

            // the link code the party displays: biatec-authenticator://link?iss=<issuer>&id=<link_id>&s=<link_secret>
            var uri = (string)v["link_created"]!["link_uri"]!;
            Assert.StartsWith("biatec-authenticator://link?", uri);
            var query = uri["biatec-authenticator://link?".Length..].Split('&').Select(p => p.Split('=', 2)).ToList();
            Assert.Equal(new[] { "iss", "id", "s" }, query.Select(p => p[0]).ToArray());
            Assert.Equal(Issuer, Uri.UnescapeDataString(query[0][1]));
            Assert.Equal((string)signed["link_id"]!, query[1][1]);
            Assert.Equal(43, query[2][1].Length);
        }
        // approve (with and without a proven domain), deny, and the negative cases
        Assert.Contains(vectors, v => (string)v!["envelope"]!["signed"]!["decision"]! == "approve" && (bool)v["expected"]!["accepted"]! && v["envelope"]!["signed"]!["domain"] is null);
        Assert.Contains(vectors, v => (string)v!["envelope"]!["signed"]!["decision"]! == "approve" && (bool)v["expected"]!["accepted"]! && v["envelope"]!["signed"]!["domain"] is not null);
        Assert.Contains(vectors, v => (string)v!["envelope"]!["signed"]!["decision"]! == "deny" && (bool)v["expected"]!["accepted"]!);
        Assert.Contains(vectors, v => ((string)v!["name"]!).Contains("wrong-domain") && !(bool)v["expected"]!["accepted"]!);
    }

    [Fact]
    public void TextRuleVectors_CoreSubsetIsDecidedByTheMinimumRules()
    {
        var vectors = Load("text-rules.json")["vectors"]!.AsArray();
        var core = vectors.Where(v => (bool)v!["core"]!).ToList();
        Assert.True(core.Count >= 35);
        foreach (var v in core)
            Assert.True((bool)v!["accepted"]! == CoreTextAccepted((string)v["text"]!, (int)v["max"]!), "text rule " + (string)v["name"]!);
        // the non-core refusals (padding, invisible characters, marks) are the strict set; the core rules alone accept some of them
        Assert.Contains(vectors, v => !(bool)v!["core"]! && !(bool)v["accepted"]! && CoreTextAccepted((string)v["text"]!, (int)v["max"]!));
        Assert.Equal(12, vectors.Count(v => (string)v!["reason"]! == "bidirectional_control" && (string)v["profile"]! == "relying_party"));
        Assert.Equal(5, vectors.Count(v => (string)v!["reason"]! == "zero_width" && (string)v["profile"]! == "relying_party"));
    }
}
