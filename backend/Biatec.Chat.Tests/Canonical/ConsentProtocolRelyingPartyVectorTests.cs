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

    /// <summary>The canonical origin of spec sections 3.6 and 6 (the pattern of the request and details schemas): http or https, a lower-case host of well-formed labels, an optional port 1 to 65535 that is not the scheme's default.</summary>
    private static readonly Regex OriginPattern = new(
        @"^(?!https://[^/]*:443$)(?!http://[^/]*:80$)https?://[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*(:([1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5]))?$",
        RegexOptions.CultureInvariant);

    private static bool OriginValid(string origin) => origin.Length <= 253 && OriginPattern.IsMatch(origin);

    private static bool IsMark(Rune r) => Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary>Filler and invisible characters outside the general categories refused (spec section 15, rule 7): soft hyphen, combining grapheme joiner, Hangul and other fillers, invisible operators, variation selectors, musical invisibles, tags.</summary>
    private static bool IsInvisible(int c) =>
        c is 0x00AD or 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x2800 or 0x3164 or 0xFFA0
        || (c >= 0x180B && c <= 0x180E) || (c >= 0x2061 && c <= 0x2065) || (c >= 0x206A && c <= 0x206F) || (c >= 0xFE00 && c <= 0xFE0F) || (c >= 0xFFF9 && c <= 0xFFFC)
        || (c >= 0x1D173 && c <= 0x1D17A) || (c >= 0xE0000 && c <= 0xE007F) || (c >= 0xE0100 && c <= 0xE01EF);

    /// <summary>
    /// The relying-party text profile in full (spec chapter 15, <c>isRelyingPartyText</c>): NFC, well-formed UTF-16, 1..max code points, no padding and not blank, no control, format
    /// (bidirectional controls, zero-width characters and joiners, the emoji selector), private-use, line or paragraph separator characters, no white space but a plain space and no run of
    /// spaces, no filler or invisible characters, no combining mark without a base character (at the start or after a space) and no stack of more than three marks.
    /// </summary>
    private static bool RelyingPartyTextAccepted(string text, int max)
    {
        if (text.Length == 0) return false;
        var runes = new List<Rune>();
        for (var i = 0; i < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i), out var r, out var used) != System.Buffers.OperationStatus.Done) return false; // a lone surrogate
            runes.Add(r);
            i += used;
        }
        if (!text.IsNormalized(NormalizationForm.FormC) || text.Trim() != text || text.Trim().Length == 0 || text.Contains("  ")) return false;
        if (runes.Count > max) return false;
        var marks = 0;
        for (var i = 0; i < runes.Count; i++)
        {
            var r = runes[i];
            var cat = Rune.GetUnicodeCategory(r);
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return false;
            if (Rune.IsWhiteSpace(r) && r.Value != ' ') return false;
            if (IsInvisible(r.Value)) return false;
            if (IsMark(r))
            {
                if (i == 0 || runes[i - 1].Value == ' ' || ++marks > 3) return false;
            }
            else marks = 0;
        }
        return true;
    }

    private static bool DetailsWellFormed(JsonNode d)
    {
        var o = d.AsObject();
        var type = (string)o["type"]!;
        bool Text(JsonNode? n, int max) => n is JsonValue v && v.TryGetValue<string>(out var s) && RelyingPartyTextAccepted(s, max);
        if (o.ContainsKey("origin") && !(o["origin"] is JsonValue ov && ov.TryGetValue<string>(out var originText) && OriginValid(originText))) return false;
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

    [Fact]
    public void TextRuleVectors_RelyingPartyProfileIsDecidedInFull()
    {
        var vectors = Load("text-rules.json")["vectors"]!.AsArray().Where(v => (string)v!["profile"]! == "relying_party").ToList();
        Assert.True(vectors.Count >= 40);
        foreach (var v in vectors)
            Assert.True((bool)v!["accepted"]! == RelyingPartyTextAccepted((string)v["text"]!, (int)v["max"]!), "relying-party text " + (string)v["name"]!);
        // the strings the core rules alone let through (joiners are Cf and so refused by the core too; the rest is the strict set)
        foreach (var text in new[] { "a  b", "a\u00ADb", "a\u034Fb", "e\u0301\u0301\u0301\u0301", " pad", "pad ", "\u0301a", "a \u0301", "a\u200Db", "a\u200Cb", "a\uFE0Fb", "a\uE000b", "a\u00A0b", "a\u3000b", "a\u2800b", "a\U000E0041b", "a\uD800b", "   " })
            Assert.False(RelyingPartyTextAccepted(text, 80), System.Text.Json.JsonSerializer.Serialize(text));
        Assert.True(RelyingPartyTextAccepted("Zo\u00eb M\u00fcller", 80));
        Assert.True(RelyingPartyTextAccepted("e\u0301".Normalize(NormalizationForm.FormC), 80));
    }

    [Fact]
    public void OriginPattern_IsTheSpecsCanonicalOrigin()
    {
        foreach (var ok in new[] { "https://netbanking.bank.example", "http://localhost", "http://bank.example:8080", "https://bank.example:8443", "https://a-b.example", "http://bank.example:443", "https://bank.example:80" })
            Assert.True(OriginValid(ok), ok);
        foreach (var bad in new[] { "https://bank.example:443", "http://bank.example:80", "https://Bank.example", "https://bank.example/", "https://bank.example/path", "https://bank.example:0", "https://bank.example:65536", "https://bank.example:08080", "ftp://bank.example", "https://-bank.example", "https://bank..example", "https://bank.example?x=1", "https://user@bank.example", "https://" + new string('a', 64) + ".example" })
            Assert.False(OriginValid(bad), bad);
    }
}
