using System.Text.Json.Nodes;
using Biatec.Chat.Tools.Consent;

namespace Biatec.Chat.Tests.Consent;

/// <summary>
/// The receipt verifier (T-238, spec section 8.1) against every vector of packages/consent-protocol/vectors (protocol 1.1 and the frozen 1.0)
/// and against alterations of a freshly built, really signed receipt. Each alteration has its own failure code.
/// </summary>
public class ConsentReceiptVerifierTests
{
    private const string VectorIssuer = "https://oidc.biatec.test";
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static long Unix => Now.ToUnixTimeSeconds();

    // ---- The vectors. ----

    private static IEnumerable<(string File, JsonNode Vector)> Vectors() =>
        new[] { "receipts.json", "receipts-1.0.json" }.SelectMany(f => ConsentTestKit.Load(f)["vectors"]!.AsArray().Select(v => (f, v!)));

    private static string ReceiptText(JsonNode v) => v["receipt"]!.ToJsonString();

    /// <summary>The verifier's trusted clock for a vector: just after the device signed.</summary>
    private static DateTimeOffset VectorNow(JsonNode v) => DateTimeOffset.FromUnixTimeSeconds((long)v["receipt"]!["issued_at"]! + 60);

    [Fact]
    public void EveryVector_GetsItsExpectedVerdict_FromTheProductionVerifier()
    {
        var keys = ConsentTestKit.OidcKeys();
        var refused = 0;
        Assert.True(Vectors().Count() >= 18);
        foreach (var (file, v) in Vectors())
        {
            var name = file + " " + (string)v["name"]!;
            var accepted = v["expected"]?["accepted"]?.GetValue<bool>() ?? true;
            var verdict = ConsentReceiptVerifier.VerifyAuthenticity(ReceiptText(v), keys, VectorIssuer, VectorNow(v));
            if (accepted) Assert.True(ReceiptVerdict.Valid == verdict, name + ": " + verdict.Code());
            else
            {
                Assert.True(ReceiptVerdict.ApprovalKeyRequired == verdict, name + ": " + verdict.Code());
                Assert.Equal("approve_requires_approval_key", (string)v["expected"]!["reason"]!);
                refused++;
            }
        }
        Assert.True(refused >= 1);
    }

    [Fact]
    public void EveryTransactionVector_MatchesItsOwnStoredRequest()
    {
        var keys = ConsentTestKit.OidcKeys();
        var matched = 0;
        foreach (var (file, v) in Vectors())
        {
            var name = file + " " + (string)v["name"]!;
            var receipt = v["receipt"]!;
            var signed = receipt["signed"]!;
            var req = v["request"]!;
            var d = req["authorization_details"]![0]!;
            var is11 = (string)receipt["version"]! == "1.1";
            var decision = (string)signed["decision"]!;
            var expected = new ConsentReceiptExpectation(VectorIssuer, VectorNow(v), (string)req["auth_req_id"]!,
                is11 ? (string)v["client_id"]! : (string)req["client"]!["entity_id"]!, (string)req["sub"]!,
                decision == "approve" ? (string)signed["binding_code"]! : null, (string)req["binding_message"]!,
                (string)req["bound_hash"]!, (string?)d["network"] ?? "", (string?)d["sender"] ?? "", (string)req["origin"]!, [decision], CheckExpiry: decision == "approve");
            var verdict = ConsentReceiptVerifier.Verify(ReceiptText(v), keys, expected);

            if ((string)d["type"]! == "biatec_transaction")
            {
                var accepted = v["expected"]?["accepted"]?.GetValue<bool>() ?? true;
                Assert.True((accepted ? ReceiptVerdict.Valid : ReceiptVerdict.ApprovalKeyRequired) == verdict, name + ": " + verdict.Code());
                matched++;
            }
            else
                Assert.True(ReceiptVerdict.DetailsMismatch == verdict, name + ": a limits change is not a transaction: " + verdict.Code()); // exactly one biatec_transaction is required
        }
        Assert.True(matched >= 14);
    }

    [Fact]
    public void GroupHash_IsTheBase32NoPaddingOfTheGroupId()
    {
        // The vector's group_hash is 52 characters of RFC 4648 base32 (32 bytes, no padding).
        var hash = (string)ConsentTestKit.Load("receipts.json")["vectors"]![0]!["request"]!["bound_hash"]!;
        Assert.Equal(52, hash.Length);
        var bytes = new byte[32];
        var acc = 0; var bits = 0; var i = 0;
        foreach (var c in hash)
        {
            acc = (acc << 5) | "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(c);
            bits += 5;
            if (bits >= 8) { bits -= 8; bytes[i++] = (byte)((acc >> bits) & 0xFF); }
        }
        Assert.Equal(32, i);
        Assert.Equal(hash, ConsentReceiptVerifier.GroupHash(bytes));
    }

    // ---- Alterations of a valid receipt, each with its own failure code. ----

    private static ReceiptSpec Spec() => new() { Expiry = Unix + 300, IssuedAt = Unix - 60 };

    private static ConsentReceiptExpectation Expect(ReceiptSpec altered) => ExpectFor(Spec());

    /// <summary>What the backend asked for: always the genuine request, whatever the receipt under test was altered to say.</summary>
    private static ConsentReceiptExpectation ExpectFor(ReceiptSpec s) => new(
        s.Issuer, Now, s.AuthReqId, s.ClientEntityId, s.Sub, s.BindingCode, s.BindingMessage, s.GroupHash, s.Network, s.Sender, s.Origin, ["approve"], CheckExpiry: true);

    private static ReceiptVerdict Check(ReceiptSpec s, Func<ConsentReceiptExpectation, ConsentReceiptExpectation>? expectation = null, string? receipt = null)
    {
        var e = Expect(s);
        return ConsentReceiptVerifier.Verify(receipt ?? ConsentTestKit.Build(s), ConsentTestKit.OidcKeys(), expectation is null ? e : expectation(e));
    }

    [Theory]
    [InlineData("1.1")]
    [InlineData("1")]
    public void AValidReceipt_OfEitherVersion_IsAccepted(string version) =>
        Assert.Equal(ReceiptVerdict.Valid, Check(Spec().With(s => s.Version = version)));

    [Fact]
    public void ChangedSignedMember_BreaksTheDeviceSignature() =>
        Assert.Equal(ReceiptVerdict.DeviceSignatureInvalid, Check(Spec().With(s => s.TamperSigned = o => o["expiry"] = Unix + 301)));

    [Fact]
    public void SignatureByAnotherKey_IsRefused()
    {
        // Signed by another key while device.public_key stays the attested device key: the device signature does not verify.
        Assert.Equal(ReceiptVerdict.DeviceSignatureInvalid, Check(Spec().With(s => s.SigningKey = "new_approval")));
        // The other key swapped in as device.public_key: the attestation names the device key, so its thumbprint does not fit.
        var node = ConsentTestKit.BuildNode(Spec().With(s => s.SigningKey = "new_approval"));
        node["device"]!["public_key"] = ConsentTestKit.PublicJwk("new_approval");
        Assert.Equal(ReceiptVerdict.AttestationKeyMismatch, Check(Spec(), receipt: node.ToJsonString()));
    }

    [Fact]
    public void AttestationForAnotherSub_IsRefused() =>
        Assert.Equal(ReceiptVerdict.AttestationSubjectMismatch, Check(Spec().With(s => s.MutateAttestation = c => c["sub"] = "someone-else")));

    [Fact]
    public void ExpiredAttestation_IsRefused() =>
        Assert.Equal(ReceiptVerdict.AttestationExpired, Check(Spec().With(s => s.MutateAttestation = c => c["exp"] = Unix - 10)));

    [Fact]
    public void AttestationSignedByAKeyNotInTheJwks_IsRefused()
    {
        // Another key under a kid the JWKS does not list ...
        Assert.Equal(ReceiptVerdict.AttestationUnknownKey, Check(Spec().With(s => { s.AttestationKid = "rogue-key-1"; s.AttestationSigningKey = "new_presence"; })));
        // ... and another key under the kid of the real one.
        Assert.Equal(ReceiptVerdict.AttestationSignatureInvalid, Check(Spec().With(s => s.AttestationSigningKey = "new_presence")));
    }

    [Fact]
    public void PresenceKeyOnAnApproval_IsRefused_EvenRelabelledAsVersion1()
    {
        Assert.Equal(ReceiptVerdict.ApprovalKeyRequired, Check(Spec().With(s => s.DeviceKey = "presence")));
        // `version` is unsigned: a 1.1 presence receipt relabelled "1" changes the shape rule, not the attested use.
        var node = ConsentTestKit.BuildNode(Spec().With(s => s.DeviceKey = "presence"));
        node["version"] = "1";
        node["device"]!.AsObject().Remove("key_use");
        Assert.Equal(ReceiptVerdict.VersionShapeMismatch, Check(Spec(), receipt: node.ToJsonString()));
        // A genuine 1.0 receipt whose device was attested for a presence use is refused too.
        Assert.Equal(ReceiptVerdict.ApprovalKeyRequired, Check(Spec().With(s => { s.Version = "1"; s.DeviceKey = "presence"; s.KeyUse = "presence"; })));
    }

    [Fact]
    public void Denial_IsAcceptedWithEitherKey_OnTheDenialPath()
    {
        foreach (var key in new[] { "device", "presence" })
        {
            var s = Spec().With(x => { x.Decision = "deny"; x.BindingCode = ""; x.DeviceKey = key; });
            Assert.Equal(ReceiptVerdict.Valid, Check(s, e => e with { Decisions = ["deny", "code_failed"], BindingCode = null, CheckExpiry = false }));
        }
    }

    [Fact]
    public void OtherAuthReqId_IsRefused() =>
        Assert.Equal(ReceiptVerdict.AuthReqIdMismatch, Check(Spec(), e => e with { AuthReqId = "ciba_00000000000000000000000000000000" }));

    [Fact]
    public void OtherGroupHash_IsRefused() =>
        Assert.Equal(ReceiptVerdict.GroupHashMismatch, Check(Spec(), e => e with { GroupHash = ConsentReceiptVerifier.GroupHash(new byte[32]) }));

    [Fact]
    public void OtherSender_IsRefused() =>
        Assert.Equal(ReceiptVerdict.SenderMismatch, Check(Spec(), e => e with { Sender = "WSNJGAWN52IJMHAEFZIOPCS3IXSHS4C46QDNSKWDEWQ3E6M4HUNLYTHJXE" }));

    [Fact]
    public void OtherNetwork_IsRefused() =>
        Assert.Equal(ReceiptVerdict.NetworkMismatch, Check(Spec(), e => e with { Network = "algorand-mainnet" }));

    [Fact]
    public void OtherOrigin_IsRefused_InVersion11AndNotLookedForIn10()
    {
        Assert.Equal(ReceiptVerdict.OriginMismatch, Check(Spec(), e => e with { Origin = "https://evil.example" }));
        // A 1.0 transaction carries no origin: nothing to compare.
        Assert.Equal(ReceiptVerdict.Valid, Check(Spec().With(s => s.Version = "1"), e => e with { Origin = "https://evil.example" }));
    }

    [Fact]
    public void WrongBindingCode_IsRefused() =>
        Assert.Equal(ReceiptVerdict.BindingCodeMismatch, Check(Spec().With(s => s.BindingCode = "000000")));

    [Fact]
    public void DenyOnTheApprovalPath_IsRefused() =>
        Assert.Equal(ReceiptVerdict.DecisionNotAllowed, Check(Spec().With(s => { s.Decision = "deny"; s.BindingCode = ""; })));

    [Fact]
    public void CodeFailedOnTheApprovalPath_IsRefused() =>
        Assert.Equal(ReceiptVerdict.DecisionNotAllowed, Check(Spec().With(s => { s.Decision = "code_failed"; s.BindingCode = "000000"; })));

    [Fact]
    public void ExpiredReceipt_IsRefused() =>
        Assert.Equal(ReceiptVerdict.ReceiptExpired, Check(Spec().With(s => s.Expiry = Unix - 1)));

    [Fact]
    public void OtherClient_OtherSubject_AndAMessageThatWasShown_AreRefused()
    {
        Assert.Equal(ReceiptVerdict.ClientMismatch, Check(Spec(), e => e with { ClientEntityId = "another-client" }));
        Assert.Equal(ReceiptVerdict.ClientMismatch, Check(Spec().With(s => s.Version = "1"), e => e with { ClientEntityId = "another-client" }));
        Assert.Equal(ReceiptVerdict.SubjectMismatch, Check(Spec(), e => e with { Sub = "user-2" }));
        // A step-up entry shows no text: a receipt that signed one is not the receipt of this request.
        Assert.Equal(ReceiptVerdict.BindingMessageMismatch, Check(Spec().With(s => s.BindingMessage = "Pay now")));
    }

    [Fact]
    public void OtherIssuer_IsRefused() =>
        Assert.Equal(ReceiptVerdict.AttestationIssuerMismatch, Check(Spec(), e => e with { Issuer = "https://evil.example" }));

    [Fact]
    public void AnUnsetStoredBindingCode_NeverMatches() =>
        Assert.Equal(ReceiptVerdict.BindingCodeMismatch, Check(Spec(), e => e with { BindingCode = "" }));

    [Fact]
    public void VersionAndSignedShape_MustAgree()
    {
        // A 1.1 receipt (fingerprint) relabelled "1", and a 1.0 receipt (entity id) relabelled "1.1".
        var as10 = ConsentTestKit.BuildNode(Spec()); as10["version"] = "1"; as10["device"]!.AsObject().Remove("key_use");
        Assert.Equal(ReceiptVerdict.VersionShapeMismatch, Check(Spec(), receipt: as10.ToJsonString()));
        var as11 = ConsentTestKit.BuildNode(Spec().With(s => s.Version = "1")); as11["version"] = "1.1"; as11["device"]!["key_use"] = "approve";
        Assert.Equal(ReceiptVerdict.VersionShapeMismatch, Check(Spec(), receipt: as11.ToJsonString()));
        Assert.Equal(ReceiptVerdict.UnsupportedVersion, Check(Spec(), receipt: Edit(n => n["version"] = "2")));
    }

    [Fact]
    public void Version11_NeedsAnAttestedKeyUse()
    {
        Assert.Equal(ReceiptVerdict.KeyUseInvalid, Check(Spec(), receipt: Edit(n => { }, Spec().With(s => s.OmitKeyUse = true), keepKeyUse: true)));
        Assert.Equal(ReceiptVerdict.KeyUseInvalid, Check(Spec().With(s => s.MutateAttestation = c => c["biatec_key_use"] = "admin")));
        // device.key_use is informational but must agree with the attested use.
        Assert.Equal(ReceiptVerdict.KeyUseInvalid, Check(Spec(), receipt: Edit(n => n["device"]!["key_use"] = "presence")));
    }

    private static string Edit(Action<JsonObject> change, ReceiptSpec? spec = null, bool keepKeyUse = false)
    {
        var n = ConsentTestKit.BuildNode(spec ?? Spec());
        if (keepKeyUse) n["device"]!["key_use"] = "approve";
        change(n);
        return n.ToJsonString();
    }

    [Fact]
    public void StrictShape_ExtraMissingOrMistypedMembers_AreRefused()
    {
        Assert.Equal(ReceiptVerdict.Malformed, Check(Spec(), receipt: Edit(n => n["extra"] = 1)));
        Assert.Equal(ReceiptVerdict.Malformed, Check(Spec(), receipt: Edit(n => n.Remove("issued_at"))));
        Assert.Equal(ReceiptVerdict.Malformed, Check(Spec(), receipt: Edit(n => n["alg"] = "ES384")));
        Assert.Equal(ReceiptVerdict.Malformed, Check(Spec(), receipt: Edit(n => n["signed"]!["extra"] = true)));
        Assert.Equal(ReceiptVerdict.Malformed, Check(Spec(), receipt: Edit(n => n["signed"]!.AsObject().Remove("binding_message"))));
        Assert.Equal(ReceiptVerdict.Malformed, Check(Spec(), receipt: Edit(n => n["signed"]!["expiry"] = "soon")));
        Assert.Equal(ReceiptVerdict.Malformed, Check(Spec(), receipt: Edit(n => n["signed"]!["decision"] = "maybe")));
        Assert.Equal(ReceiptVerdict.Malformed, Check(Spec(), receipt: Edit(n => n["signed"]!["binding_code"] = "12")));
        Assert.Equal(ReceiptVerdict.Malformed, Check(Spec(), receipt: Edit(n => n["device"]!["public_key"]!["d"] = "leaked-private-member")));
        Assert.Equal(ReceiptVerdict.Malformed, Check(Spec(), receipt: Edit(n => n["device"]!["public_key"]!["crv"] = "P-384")));
        // `remember` rides only with a 1.1 approval.
        Assert.Equal(ReceiptVerdict.Malformed, Check(Spec().With(s => { s.Version = "1"; s.MutateSigned = o => o["remember"] = new JsonObject { ["applications"] = new JsonArray(7) }; })));
    }

    [Fact]
    public void SignatureEncoding_MustBeCanonical64Bytes()
    {
        Assert.Equal(ReceiptVerdict.BadSignatureEncoding, Check(Spec(), receipt: Edit(n => n["signature"] = "AAAA")));
        Assert.Equal(ReceiptVerdict.BadSignatureEncoding, Check(Spec(), receipt: Edit(n => n["signature"] = new string('A', 85) + "=")));
        // Non-canonical trailing bits: the last character of 86 may only carry two bits.
        var sig = (string)ConsentTestKit.BuildNode(Spec())["signature"]!;
        var last = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".IndexOf(sig[^1]);
        var twin = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"[last ^ 1];
        Assert.Equal(ReceiptVerdict.BadSignatureEncoding, Check(Spec(), receipt: Edit(n => n["signature"] = sig[..^1] + twin)));
    }

    [Fact]
    public void AttestationMalformed_Variants_AreRefused()
    {
        Assert.Equal(ReceiptVerdict.Malformed, Check(Spec(), receipt: Edit(n => n["device"]!["attestation"] = "not-a-jws")));
        Assert.Equal(ReceiptVerdict.AttestationMalformed, Check(Spec(), receipt: Edit(n => n["device"]!["attestation"] = "AAAA.BBBB.CCCC")));
        Assert.Equal(ReceiptVerdict.AttestationHeaderInvalid, Check(Spec().With(s => s.AttestationKid = "oidc-test-key-1"), receipt: Edit(n =>
        {
            var parts = ((string)n["device"]!["attestation"]!).Split('.');
            var header = ConsentTestKit.B64UrlEncode(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"ES256\",\"kid\":\"oidc-test-key-1\",\"typ\":\"JWT\"}"));
            n["device"]!["attestation"] = header + "." + parts[1] + "." + parts[2];
        })));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"receipt\"")]
    [InlineData("{")]
    [InlineData("{\"version\":1.1}")]
    [InlineData("{\"a\":1,\"a\":2}")]
    public void MalformedInput_IsAVerdict_NeverAnException(string text)
    {
        Assert.NotEqual(ReceiptVerdict.Valid, ConsentReceiptVerifier.Verify(text, ConsentTestKit.OidcKeys(), Expect(Spec())));
        Assert.NotEqual(ReceiptVerdict.Valid, ConsentReceiptVerifier.VerifyAuthenticity(text, [], "", Now));
    }

    [Fact]
    public void NoKeys_MeansTheAttestationCannotBeVerified() =>
        Assert.Equal(ReceiptVerdict.AttestationUnknownKey, ConsentReceiptVerifier.Verify(ConsentTestKit.Build(Spec()), [], Expect(Spec())));

    [Fact]
    public void FailureCodes_AreDistinctSnakeCase()
    {
        var codes = Enum.GetValues<ReceiptVerdict>().Select(v => v.Code()).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Equal("group_hash_mismatch", ReceiptVerdict.GroupHashMismatch.Code());
        Assert.Equal("valid", ReceiptVerdict.Valid.Code());
    }
}

internal static class ReceiptSpecExtensions
{
    public static ReceiptSpec With(this ReceiptSpec s, Action<ReceiptSpec> change) { change(s); return s; }
}
