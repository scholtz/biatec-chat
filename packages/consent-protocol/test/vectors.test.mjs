import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import {
  ENROLL_PROOF_TYP,
  attestedKeyUse,
  isDisplaySafe,
  jcs,
  pairingCode,
  sha256Hex,
  signCompactJws,
  signEs256,
  thumbprint,
  verifyApproval,
  verifyAttestation,
  verifyDeviceSigned,
  verifyEnrollmentProof,
  verifyReceipt,
  verifyReceiptSignature,
} from "../scripts/lib.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const load = (...p) => JSON.parse(readFileSync(join(root, ...p), "utf8"));
const clone = (v) => structuredClone(v);

const ajv = new Ajv2020({ allErrors: true, strict: true });
for (const f of readdirSync(join(root, "schemas")).filter((n) => n.endsWith(".schema.json"))) ajv.addSchema(load("schemas", f));
const schema = (name) => {
  const v = ajv.getSchema(`https://schemas.biatec.io/consent/v1/${name}.schema.json`);
  assert.ok(v, "schema " + name);
  return v;
};
const valid = (name, data) => schema(name)(data);

const jwks = load("vectors", "jwks.json");
const keys = load("vectors", "keys.json");
const O = { now: 1792425700, issuer: "https://oidc.biatec.test", deviceActive: true };
const v10 = load("vectors", "receipts-1.0.json").vectors; // frozen protocol 1.0 set
const v11 = load("vectors", "receipts.json").vectors; // protocol 1.1 set
const canon = load("vectors", "canonicalization.json").vectors;
const device = load("vectors", "device-signed.json");
const accepted = (v) => (v.expected ?? { accepted: true }).accepted;

test("every schema compiles", () => {
  for (const f of readdirSync(join(root, "schemas"))) assert.ok(ajv.getSchema(load("schemas", f).$id), f);
});

// ---- per-vector checks, for the frozen 1.0 set and the 1.1 set alike
for (const [label, vectors] of [["1.0", v10], ["1.1", v11]]) {
  const is11 = label === "1.1";
  for (const v of vectors) {
    test(`[${label}] vector ${v.name}: schemas validate`, () => {
      assert.equal(v.receipt.version, is11 ? "1.1" : "1");
      assert.ok(valid("consent-request", v.request), JSON.stringify(schema("consent-request").errors));
      assert.ok(valid("consent-receipt", v.receipt), JSON.stringify(schema("consent-receipt").errors));
      assert.ok(valid("consent-receipt-signed", v.receipt.signed));
      assert.ok(valid("authorization-details", v.request.authorization_details));
      assert.ok(valid("token-response", { access_token: "x", token_type: "Bearer", expires_in: 300, consent_receipt: v.receipt }));
    });

    test(`[${label}] vector ${v.name}: canonical form, hash and signatures verify as the vector says`, () => {
      assert.equal(jcs(v.receipt.signed), v.canonical);
      assert.equal(sha256Hex(v.canonical), v.canonical_sha256);
      assert.ok(verifyReceiptSignature(v.receipt), "device signature is valid, also for a vector that must be refused");
      assert.ok(verifyAttestation(v.receipt, jwks, O), "attestation");
      assert.equal(verifyReceipt(v.receipt, jwks, O), accepted(v));
    });

    test(`[${label}] vector ${v.name}: receipt matches its request (what you see is what you sign)`, () => {
      const s = v.receipt.signed;
      assert.deepEqual(s.authorization_details, v.request.authorization_details);
      assert.equal(s.auth_req_id, v.request.auth_req_id);
      if (is11) assert.equal(s.client_fingerprint, v.request.client.fingerprint);
      else assert.equal(s.client_entity_id, v.request.client.entity_id);
      assert.equal(s.binding_message, v.request.binding_message);
      assert.equal(s.expiry, v.request.expiry);
      assert.equal(s.sub, v.request.sub);
      const d = s.authorization_details[0];
      assert.equal(v.request.bound_hash, d.group_hash ?? d.request_hash);
      if (is11 && d.type === "biatec_transaction") assert.equal(d.origin, v.request.origin, "the signed details name the site that asked");
    });

    test(`[${label}] vector ${v.name}: any changed signed member invalidates the signature`, () => {
      const mutations = {
        authorization_details: (s) => {
          const d = s.authorization_details[0];
          if (d.transfers) d.transfers[0] ? (d.transfers[0].amount = "5000.000001") : (d.origin = "https://evil.example");
          else if (d.amount) d.amount = "5000.000001";
          else d.requested_limits.per_day_eur = "50000";
        },
        auth_req_id: (s) => (s.auth_req_id += "0"),
        client_entity_id: (s) => (s.client_entity_id = "https://evil.example"),
        client_fingerprint: (s) => (s.client_fingerprint = "0000000000000000"),
        binding_code: (s) => (s.binding_code = "000000"),
        decision: (s) => (s.decision = s.decision === "approve" ? "deny" : "approve"),
        expiry: (s) => (s.expiry += 1),
        sub: (s) => (s.sub += "x"),
        binding_message: (s) => (s.binding_message += "!"),
        remember: (s) => (s.remember.applications.push(1)),
      };
      const members = Object.keys(v.receipt.signed);
      for (const k of members) assert.ok(mutations[k], "a mutation exists for " + k);
      for (const k of members) {
        const r = clone(v.receipt);
        mutations[k](r.signed);
        assert.equal(verifyReceiptSignature(r), false, k);
      }
    });
  }
}

// ---- 1.1 specifics
test("1.1 receipt vectors cover both entries, all decisions, both keys and every shape of details", () => {
  assert.deepEqual([...new Set(v11.map((v) => v.request.entry))].sort(), ["direct", "step-up"]);
  assert.deepEqual([...new Set(v11.map((v) => v.receipt.signed.decision))].sort(), ["approve", "code_failed", "deny"]);
  assert.deepEqual([...new Set(v11.map((v) => v.receipt.device.key_use))].sort(), ["approve", "presence"]);
  assert.deepEqual([...new Set(v11.map((v) => v.request.authorization_details[0].type))].sort(), ["biatec_limits_change", "biatec_transaction"]);
  const tx = v11.map((v) => v.request.authorization_details[0]).filter((d) => d.type === "biatec_transaction");
  assert.ok(tx.some((d) => d.transfers.length === 2), "a two-transfer group");
  assert.ok(tx.some((d) => d.transfers.length === 1 && d.applications.some((a) => a.trusted)), "a transfer plus a trusted application");
  assert.ok(tx.some((d) => d.applications.some((a) => !a.trusted)), "an untrusted application");
  assert.ok(tx.some((d) => d.value_known === false && !("fiat_value" in d)), "a value-unknown group");
  assert.ok(v11.some((v) => v.receipt.signed.remember), "an approval that remembers an application");
  assert.ok(v11.some((v) => !accepted(v)), "a negative vector");
});

test("a group's fiat_value is present exactly when its value is known; the total is the sum of the priced transfers", () => {
  for (const v of v11) {
    const d = v.request.authorization_details[0];
    if (d.type !== "biatec_transaction") continue;
    assert.equal("fiat_value" in d, d.value_known, v.name);
    if (d.value_known && d.transfers.every((t) => t.fiat_value)) {
      const cents = d.transfers.reduce((n, t) => n + Math.round(Number(t.fiat_value.amount) * 100), 0);
      assert.equal(Math.round(Number(d.fiat_value.amount) * 100), cents, v.name);
    }
    if (d.applications.some((a) => !a.trusted)) assert.equal(d.value_known, false, v.name + ": an untrusted application makes the value unknown");
  }
});

test("the approval-key rule: an approve receipt needs biatec_key_use approve; deny and code_failed accept either key", () => {
  for (const v of v11) {
    const use = attestedKeyUse(v.receipt, jwks, O);
    assert.equal(use, v.receipt.device.key_use, v.name);
    if (v.receipt.signed.decision === "approve") assert.equal(verifyReceipt(v.receipt, jwks, O), use === "approve", v.name);
    else assert.equal(verifyReceipt(v.receipt, jwks, O), true, v.name);
  }
  const negative = v11.find((v) => !accepted(v));
  assert.equal(negative.receipt.signed.decision, "approve");
  assert.equal(negative.receipt.device.key_use, "presence");
  assert.ok(verifyReceiptSignature(negative.receipt) && verifyAttestation(negative.receipt, jwks, O), "everything but the key use is valid");
  assert.equal(verifyApproval(negative.receipt, jwks, negative.request, negative.receipt.signed.binding_code, new Set(), { ...O, now: negative.receipt.signed.expiry - 100 }), false);
  // The informational member cannot be used to talk a verifier into it, nor can a missing attestation claim.
  const lie = clone(negative.receipt);
  lie.device.key_use = "approve";
  assert.equal(verifyReceipt(lie, jwks, O), false, "device.key_use must agree with the attested claim");
});

test("a 1.1 receipt whose attestation has no biatec_key_use is refused", () => {
  const v = clone(v11[0]);
  const [h, p] = v.receipt.device.attestation.split(".");
  const payload = JSON.parse(Buffer.from(p, "base64url").toString());
  delete payload.biatec_key_use;
  v.receipt.device.attestation = signCompactJws(keys.oidc, JSON.parse(Buffer.from(h, "base64url").toString()), payload);
  assert.equal(verifyReceipt(v.receipt, jwks, O), false);
});

test("the client is its fingerprint: 16 hex characters of SHA-256(client_id); the domain appears only when proven", () => {
  for (const v of v11) {
    const c = v.request.client;
    assert.equal(c.fingerprint, createHash("sha256").update(v.client_id).digest("hex").slice(0, 16), v.name);
    assert.equal("domain" in c, c.domain_proven, v.name);
    if (!c.domain_proven) assert.equal(v.request.binding_message, "", v.name + ": no text channel without a proven domain");
    if (v.request.entry === "step-up") assert.equal(v.receipt.signed.binding_message, "", v.name);
  }
  const unproven = v11.find((v) => v.unsigned_client_binding_message);
  assert.ok(unproven && unproven.receipt.signed.binding_message === "" && !unproven.request.client.domain_proven);
  assert.ok(v11.some((v) => v.request.entry === "direct" && v.request.client.domain_proven && v.receipt.signed.binding_message !== ""));
});

test("limits change request_hash is SHA-256 of the JCS request body (1.0 and 1.1)", () => {
  for (const set of [v10, v11]) {
    const v = set.find((x) => x.limits_request_body);
    assert.equal(v.request.authorization_details[0].request_hash, sha256Hex(jcs(v.limits_request_body)));
  }
});

// ---- 1.0 specifics (frozen)
test("1.0: attestation is bound to sub and device key", () => {
  const v = v10[0];
  const otherSub = clone(v.receipt);
  otherSub.signed.sub = "pw_other";
  assert.equal(verifyAttestation(otherSub, jwks, O), false);
  const otherKid = clone(v.receipt);
  otherKid.device.kid = "device-other";
  assert.equal(verifyAttestation(otherKid, jwks, O), false);
  assert.equal(verifyAttestation(v.receipt, { keys: [] }, O), false);
});

test("a signature by a different device key is rejected", () => {
  for (const v0 of [v10[0], v11[0]]) {
    const v = clone(v0);
    v.receipt.signature = signEs256({ ...keys.oidc }, Buffer.from(v.canonical, "utf8"));
    assert.equal(verifyReceiptSignature(v.receipt), false);
  }
});

test("1.0 receipt vectors cover both entries, all decisions and both detail types; unverified clients sign an empty message", () => {
  assert.deepEqual([...new Set(v10.map((v) => v.request.entry))].sort(), ["direct", "step-up"]);
  assert.deepEqual([...new Set(v10.map((v) => v.receipt.signed.decision))].sort(), ["approve", "code_failed", "deny"]);
  assert.deepEqual([...new Set(v10.map((v) => v.request.authorization_details[0].type))].sort(), ["biatec_limits_change", "biatec_transaction"]);
  for (const v of v10) if (v.request.entry === "step-up" || !v.request.client.verified) assert.equal(v.receipt.signed.binding_message, "", v.name);
  assert.ok(v10.some((v) => v.request.entry === "direct" && v.request.client.verified && v.receipt.signed.binding_message !== ""));
});

test("canonicalization vectors (RFC 8785)", () => {
  for (const c of canon) assert.equal(jcs(c.input), c.canonical, c.name);
  // RFC 8785 section 3.2.2 sample
  assert.equal(jcs({ numbers: [333333333.33333329, 1e30, 4.5, 2e-3, 1e-27] }), '{"numbers":[333333333.3333333,1e+30,4.5,0.002,1e-27]}');
});

test("schemas reject malformed objects", () => {
  const bad = (name, mutate, target) => {
    const d = clone(target);
    mutate(d);
    assert.equal(valid(name, d), false, name);
  };
  const r10 = v10[0];
  bad("consent-receipt", (r) => (r.signed.amount = 1), r10.receipt);
  bad("consent-receipt", (r) => (r.signed.binding_code = "12345"), r10.receipt);
  bad("consent-receipt", (r) => (r.signed.decision = "maybe"), r10.receipt);
  bad("consent-receipt", (r) => delete r.signed.binding_message, r10.receipt);
  bad("consent-receipt", (r) => (r.alg = "none"), r10.receipt);
  bad("consent-receipt", (r) => (r.signed.authorization_details[0].amount = 5000), r10.receipt);
  bad("consent-receipt", (r) => (r.signed.authorization_details[0].type = "biatec_other"), r10.receipt);
  bad("consent-receipt", (r) => (r.device.key_use = "approve"), r10.receipt); // a 1.0 receipt has no key use
  bad("consent-request", (r) => (r.binding_code = "483920"), r10.request);
  const unverified = v10.find((x) => !x.request.client.verified).request;
  bad("consent-request", (r) => (r.client.display_name = "Example Bank"), unverified);
  bad("consent-request", (r) => delete r.client.fingerprint, unverified);

  const r11 = v11[0];
  bad("consent-receipt", (r) => (r.version = "2"), r11.receipt);
  bad("consent-receipt", (r) => delete r.device.key_use, r11.receipt); // 1.1 says which key signed
  bad("consent-receipt", (r) => (r.signed.client_entity_id = "https://chat.biatec.io"), r11.receipt); // 1.0 member in a 1.1 receipt
  bad("consent-receipt", (r) => (r.signed.client_fingerprint = "3F9A-1C77"), r11.receipt); // wire form is 16 lowercase hex
  bad("consent-receipt", (r) => (r.signed.authorization_details[0].amount = "1"), r11.receipt); // single-transfer members are 1.0
  bad("consent-receipt", (r) => (r.signed.authorization_details[0].origin = "https://x.example/path"), r11.receipt);
  bad("consent-receipt", (r) => delete r.signed.authorization_details[0].origin, r11.receipt);
  bad("consent-receipt", (r) => delete r.signed.authorization_details[0].applications, r11.receipt);
  bad("consent-receipt", (r) => delete r.signed.authorization_details[0].fiat_value, r11.receipt); // known value needs a total
  bad("consent-receipt", (r) => (r.signed.authorization_details[0].transfers = Array.from({ length: 17 }, () => r.signed.authorization_details[0].transfers[0])), r11.receipt);
  const deny = v11.find((v) => v.receipt.signed.decision === "deny").receipt;
  bad("consent-receipt", (r) => (r.signed.remember = { applications: [1] }), deny); // never on a deny
  const unknown = v11.find((v) => v.request.authorization_details[0].value_known === false && !v.request.authorization_details[0].applications.length);
  bad("consent-receipt", (r) => (r.signed.authorization_details[0].fiat_value = { amount: "1", currency: "EUR" }), unknown.receipt); // unknown value has no total
  bad("consent-request", (r) => (r.client.display_name = "Example Bank"), r11.request); // no names in 1.1
  bad("consent-request", (r) => (r.client.verified = true), r11.request);
  const unproven = v11.find((x) => !x.request.client.domain_proven).request;
  bad("consent-request", (r) => (r.client.domain = "bank.example"), unproven);
  bad("consent-request", (r) => (r.binding_message = "Example Bank refund"), unproven);
  bad("consent-request", (r) => delete r.client.domain, r11.request);
});

test("verifyApproval accepts the matching approve once and rejects deny, code_failed, expiry, replay and mismatches (1.0 and 1.1)", () => {
  for (const set of [v10, v11]) {
    const o = { ...O, now: set[0].receipt.signed.expiry - 100 };
    const v = set.find((x) => x.receipt.signed.decision === "approve" && accepted(x));
    const code = v.receipt.signed.binding_code;
    const consumed = new Set();
    assert.equal(verifyApproval(v.receipt, jwks, v.request, code, consumed, o), true);
    assert.equal(verifyApproval(v.receipt, jwks, v.request, code, consumed, o), true, "still unspent: a failed signing call must not burn the consent");
    consumed.add(v.receipt.signed.auth_req_id);
    assert.equal(verifyApproval(v.receipt, jwks, v.request, code, consumed, o), false, "replay after spending");
    assert.equal(verifyApproval(v.receipt, jwks, v.request, code, new Set(), { ...o, now: v.receipt.signed.expiry }), false, "expired");
    for (const d of ["deny", "code_failed"]) {
      const x = set.find((y) => y.receipt.signed.decision === d);
      assert.ok(verifyReceipt(x.receipt, jwks, o), d + " signatures are valid");
      assert.equal(verifyApproval(x.receipt, jwks, x.request, x.receipt.signed.binding_code || "123456", new Set(), o), false, d);
    }
    const other = set.find((x) => x !== v && x.receipt.signed.decision === "approve" && accepted(x));
    assert.equal(verifyApproval(v.receipt, jwks, other.request, code, new Set(), o), false, "different request");
    assert.equal(verifyApproval(v.receipt, jwks, v.request, "000000", new Set(), o), false, "number matching: wrong code");
    assert.equal(verifyApproval(v.receipt, jwks, {}, code, new Set(), o), false, "malformed request does not throw");
    const changed = clone(v.request);
    changed.bound_hash = other.request.bound_hash;
    assert.equal(verifyApproval(v.receipt, jwks, changed, code, new Set(), o), false, "different bound hash");
  }
  // 1.1: the client named in the receipt must be the one that asked, and the origin is part of what was approved
  const v = v11[0];
  const o = { ...O, now: v.receipt.signed.expiry - 100 };
  const otherClient = clone(v.request);
  otherClient.client.fingerprint = "0000000000000000";
  assert.equal(verifyApproval(v.receipt, jwks, otherClient, v.receipt.signed.binding_code, new Set(), o), false, "another client");
  const loggedOrigin = clone(v.request);
  loggedOrigin.origin = "https://evil.example";
  assert.equal(verifyApproval(v.receipt, jwks, loggedOrigin, v.receipt.signed.binding_code, new Set(), o), false, "the recorded origin must be the signed one");
  const otherOrigin = clone(v.request);
  otherOrigin.authorization_details[0].origin = "https://evil.example";
  assert.equal(verifyApproval(v.receipt, jwks, otherOrigin, v.receipt.signed.binding_code, new Set(), o), false, "another origin");
});

test("attestation checks fail closed", () => {
  for (const r of [v10[0].receipt, v11[0].receipt]) {
    assert.equal(verifyReceipt(r, jwks, { ...O, now: 4102444800 }), false, "expired attestation");
    assert.equal(verifyReceipt(r, jwks, { ...O, issuer: "https://evil.example" }), false, "issuer");
    assert.equal(verifyReceipt(r, jwks, { now: O.now }), false, "issuer is required");
    const enc = { keys: [{ ...jwks.keys[0], use: "enc" }] };
    assert.equal(verifyAttestation(r, enc, O), false, "key must be a signature key");
  }
});

test("non-canonical base64url signatures are rejected", () => {
  const r = clone(v11[0].receipt);
  const last = r.signature.slice(-1);
  const A = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
  const alt = A[(A.indexOf(last) & ~15) | ((A.indexOf(last) + 1) & 15)];
  assert.notEqual(alt, last);
  r.signature = r.signature.slice(0, -1) + alt;
  assert.equal(verifyReceiptSignature(r), false);
  assert.equal(verifyReceiptSignature({ ...v11[0].receipt, signature: v11[0].receipt.signature + "=" }), false);
});

test("verifiers return false instead of throwing on malformed input", () => {
  const r = clone(v11[0].receipt);
  r.device.attestation = "garbage";
  assert.equal(verifyAttestation(r, jwks, O), false);
  r.device.attestation = "a.b.c";
  assert.equal(verifyReceipt(r, jwks, O), false);
  assert.equal(verifyReceipt({}, jwks, O), false);
  assert.equal(verifyReceipt(v11[0].receipt, { keys: [{ kid: "oidc-test-key-1", kty: "oct" }] }, O), false);
  assert.equal(attestedKeyUse({}, jwks, O), null);
  assert.equal(verifyDeviceSigned({}, jwks, O), false);
  assert.equal(verifyDeviceSigned(null, jwks, O), false);
  assert.equal(verifyEnrollmentProof("a.b", {}), false);
  assert.equal(verifyEnrollmentProof(undefined, {}), false);
});

test("schemas enforce cross-field rules", () => {
  const approve = v10[0].receipt;
  const r = clone(approve);
  r.signed.binding_code = "";
  assert.equal(valid("consent-receipt", r), false, "approve needs a code");
  const stepUp = clone(v10[0].request);
  stepUp.binding_message = "Pay me";
  assert.equal(valid("consent-request", stepUp), false, "step-up has no client text");
  const unverified = clone(v10.find((v) => !v.request.client.verified).request);
  unverified.binding_message = "Example Bank refund";
  assert.equal(valid("consent-request", unverified), false, "unverified client has no text channel");
  const big = clone(approve);
  big.signed.expiry = 2 ** 53;
  assert.equal(valid("consent-receipt", big), false);
});

test("jcs rejects lone surrogates and undefined members", () => {
  assert.throws(() => jcs({ a: undefined }));
  assert.throws(() => jcs({ a: "\ud800" }));
});

test("backchannel request schema ties scope to the request kind and forbids client authorization_details", () => {
  const ok = { scope: "sign", resource: "https://api.biatec.io", login_hint: "u", origin: "https://bank.example", network: "algorand-mainnet", address: "A", unsigned_group: ["AA"] };
  assert.ok(valid("backchannel-authentication-request", ok), JSON.stringify(schema("backchannel-authentication-request").errors));
  assert.ok(valid("backchannel-authentication-request", { scope: "manage-limits", resource: "r", login_hint: "u", origin: "o", limits_request: { a: 1 } }));
  assert.equal(valid("backchannel-authentication-request", { ...ok, limits_request: {} }), false, "both kinds");
  assert.equal(valid("backchannel-authentication-request", { scope: "sign", resource: "r", login_hint: "u", origin: "o", limits_request: {} }), false, "sign needs a group");
  assert.equal(valid("backchannel-authentication-request", { ...ok, authorization_details: [] }), false, "client details");
});

// ---- device-signed statements: settings changes, device approvals, enrollment proofs, pairing code
const DO = { now: device.now, issuer: device.issuer, usedNonces: new Set() };
const ACTIVE = { approverActive: true, sub: device.enrollment_sub };

for (const s of device.settings_changes) {
  test(`device-signed ${s.name}: schema, canonical form and verdict`, () => {
    assert.ok(valid("device-signed", s.envelope), JSON.stringify(schema("device-signed").errors));
    assert.ok(valid("settings-change-signed", s.envelope.signed));
    assert.equal(jcs(s.envelope.signed), s.canonical);
    assert.equal(sha256Hex(s.canonical), s.canonical_sha256);
    assert.equal(verifyDeviceSigned(s.envelope, jwks, DO, ACTIVE), s.expected.accepted);
    assert.equal(verifyDeviceSigned(s.envelope, jwks, DO), false, "a settings change is refused unless the caller says the device is active");
  });
}

for (const a of device.device_approvals) {
  test(`device-signed ${a.name}: schema, canonical form and verdict`, () => {
    assert.ok(valid("device-signed", a.envelope), JSON.stringify(schema("device-signed").errors));
    assert.ok(valid("device-approval-signed", a.envelope.signed));
    assert.equal(jcs(a.envelope.signed), a.canonical);
    assert.equal(sha256Hex(a.canonical), a.canonical_sha256);
    assert.equal(verifyDeviceSigned(a.envelope, jwks, DO, { approval: a.expected_inputs, sub: device.enrollment_sub, approverActive: true }), a.expected.accepted);
    assert.equal(verifyDeviceSigned(a.envelope, jwks, DO, { approval: a.expected_inputs }), false, "without whose approval it accepts, a device approval is refused");
    assert.equal(verifyDeviceSigned(a.envelope, jwks, DO, { sub: device.enrollment_sub, approverActive: true }), false, "without the enrollment it covers, a device approval is refused");
    assert.equal(verifyDeviceSigned(a.envelope, jwks, DO, { approval: a.expected_inputs, sub: device.enrollment_sub }), false, "the caller must say the approver is an active device");
  });
}

test("any changed member of a device-signed statement invalidates its signature", () => {
  for (const s of [...device.settings_changes, ...device.device_approvals].filter((x) => x.expected.accepted)) {
    for (const k of Object.keys(s.envelope.signed)) {
      const e = clone(s.envelope);
      if (k === "change") e.signed.change = { threshold_eur: "999999" };
      else if (typeof e.signed[k] === "number") e.signed[k] += 1;
      else e.signed[k] += "x";
      assert.equal(verifyDeviceSigned(e, jwks, DO, { approval: s.expected_inputs, sub: device.enrollment_sub, approverActive: true }), false, s.name + " " + k);
    }
  }
});

test("a device-signed statement is refused when stale, replayed with other expectations, or signed for another subject", () => {
  const s = device.device_approvals.find((x) => x.expected.accepted);
  const sub = device.enrollment_sub;
  assert.equal(verifyDeviceSigned(s.envelope, jwks, { ...DO, now: DO.now + 301 }, { approval: s.expected_inputs, sub, approverActive: true }), false, "older than 300 s");
  assert.equal(verifyDeviceSigned(s.envelope, jwks, { ...DO, now: s.envelope.signed.issued_at - 301 }, { approval: s.expected_inputs, sub, approverActive: true }), false, "from the future");
  assert.equal(verifyDeviceSigned(s.envelope, jwks, DO, { approval: { ...s.expected_inputs, nonce: "another-enrollment-nonce-0000" }, sub, approverActive: true }), false, "another enrollment");
  assert.equal(verifyDeviceSigned(s.envelope, jwks, DO, { approval: { ...s.expected_inputs, new_presence_jwk_thumbprint: thumbprint(keys.presence) }, sub, approverActive: true }), false, "another presence key");
  // Single use: the caller's set of accepted nonces makes a replay refused.
  assert.equal(verifyDeviceSigned(s.envelope, jwks, { ...DO, usedNonces: new Set() }, { approval: s.expected_inputs, sub, approverActive: true }), true);
  assert.equal(verifyDeviceSigned(s.envelope, jwks, { ...DO, usedNonces: new Set([s.envelope.signed.nonce]) }, { approval: s.expected_inputs, sub, approverActive: true }), false, "replayed approval");
  assert.equal(verifyDeviceSigned(s.envelope, jwks, { now: DO.now, issuer: DO.issuer }, { approval: s.expected_inputs, sub, approverActive: true }), false, "without the set of used nonces a replay cannot be told");
  const settingsVec = device.settings_changes.find((x) => x.expected.accepted);
  assert.equal(verifyDeviceSigned(settingsVec.envelope, jwks, { ...DO, usedNonces: new Set([settingsVec.envelope.signed.nonce]) }, ACTIVE), false, "replayed settings change");
  assert.equal(verifyDeviceSigned(settingsVec.envelope, jwks, DO, ACTIVE), true);
  const settings = device.settings_changes.find((x) => x.expected.accepted);
  const otherSub = clone(settings.envelope);
  otherSub.signed.sub = "pw_other";
  assert.equal(verifyDeviceSigned(otherSub, jwks, DO, ACTIVE), false);
  const otherKid = clone(settings.envelope);
  otherKid.signed.kid = "someone-else";
  assert.equal(verifyDeviceSigned(otherKid, jwks, DO, ACTIVE), false, "the statement names the key that signs it");
});

test("a settings change without a subject, an approval for another new_kid and a 1.1 receipt without device.key_use are refused", () => {
  const settings = device.settings_changes.find((x) => x.expected.accepted);
  const noSub = clone(settings.envelope);
  delete noSub.signed.sub;
  noSub.signature = signEs256(keys.device, Buffer.from(jcs(noSub.signed), "utf8"));
  assert.equal(verifyDeviceSigned(noSub, jwks, DO, ACTIVE), false);
  const a = device.device_approvals.find((x) => x.expected.accepted);
  const ok = { approval: a.expected_inputs, sub: device.enrollment_sub, approverActive: true };
  assert.equal(verifyDeviceSigned(a.envelope, jwks, DO, { ...ok, approval: { ...a.expected_inputs, new_kid: "another-device" } }), false, "the kid being activated is the kid the approver signed");
  assert.equal(verifyDeviceSigned(a.envelope, jwks, DO, { ...ok, approval: { ...a.expected_inputs, new_kid: undefined } }), false);
  const r = clone(v11[0].receipt);
  delete r.device.key_use;
  assert.equal(verifyReceipt(r, jwks, O), false, "1.1 names the key use");
});

test("a settings label is display-safe (NFC, no bidirectional or zero-width characters), and an unlink stands alone", () => {
  assert.equal(isDisplaySafe("My bank"), true);
  assert.equal(isDisplaySafe("My very own bank"), true);
  assert.equal(isDisplaySafe("Caf\u00e9"), true);
  for (const bad of ["Bank\u202Eevil", "Bank\u200B", "Cafe\u0301", "two\nlines", "", "x".repeat(41), "tab\t", "\ufeffBank", "lone\ud800", "   ", "\u3164", " padded", "padded ", "soft\u00ADhyphen", "tag\u{E0041}", "vs\ufe0f", "invisible\u2063sep", "braille\u2800", "format\u206F", "obj\ufffc", "music\u{1D173}", "wide\u3000\u3000gap", "\u0301lone mark", "stack" + "\u0301".repeat(4), "no\u00A0break", "thin\u2009space", "double  space", "bcd\u{1BCA0}", "egy\u{13430}"]) assert.equal(isDisplaySafe(bad), false, JSON.stringify(bad));
  const base = device.settings_changes.find((x) => x.expected.accepted);
  const resign = (change) => {
    const e = clone(base.envelope);
    e.signed.change = change;
    e.signature = signEs256(keys.device, Buffer.from(jcs(e.signed), "utf8"));
    return verifyDeviceSigned(e, jwks, DO, ACTIVE);
  };
  const c = (m) => ({ clients: { "client-x": m } });
  assert.equal(resign(c({ label: "My bank" })), true);
  assert.equal(resign(c({ label: "Bank\u202Eevil" })), false, "bidirectional control");
  assert.equal(resign(c({ label: "Cafe\u0301" })), false, "not NFC");
  assert.equal(resign(c({ unlink: true, trusted_applications_add: [{ network: "algorand-mainnet", app_id: 1 }] })), false, "unlink with another change");
  assert.equal(resign(c({ unlink: true })), true);
  assert.equal(resign(c({ paused: false })), false, "a pause without its origin");
  assert.equal(resign(c({ mystery: 1 })), false, "unknown member");
  assert.equal(resign(c({})), false, "empty client change");
  assert.equal(resign({}), false, "empty change");
  assert.equal(resign({ mystery: true }), false, "unknown top-level member");
  assert.equal(resign({ threshold_eur: 5000 }), false, "a number is not an amount string");
  assert.equal(resign({ threshold_eur: "5000.123" }), false, "at most two decimals");
  assert.equal(resign({ threshold_eur: "5000.50" }), true);
  assert.equal(resign({ ask_on_test_networks: "no" }), false, "flags are booleans");
  assert.equal(resign({ clients: [] }), false, "clients is an object");
  assert.equal(resign(c({ unlink: false })), false, "unlink is true or absent");
  assert.equal(resign(c({ paused: "yes", origin: "https://bank.example" })), false);
  assert.equal(resign(c({ per_client_threshold_eur: null })), true);
  assert.equal(resign(c({ per_client_threshold_eur: "12" })), true);
  assert.equal(resign(c({ per_client_threshold_eur: 12 })), false);
  assert.equal(resign(c({ paused: true, origin: "HTTPS://Evil.Example:443/" })), false, "a non-canonical origin");
  assert.equal(resign(c({ paused: true, origin: {} })), false);
  assert.equal(resign(c({ trusted_applications_add: "x" })), false, "an application list");
  assert.equal(resign(c({ trusted_applications_add: [{ network: "algorand-mainnet", app_id: 1.5 }] })), false);
  assert.equal(resign(c({ trusted_applications_remove: [{ network: "bitcoin-mainnet", app_id: 1 }] })), false);
  assert.equal(resign(c({ trusted_applications_add: [{ network: "algorand-testnet", app_id: 7 }] })), true);
  const app = { network: "algorand-testnet", app_id: 7 };
  assert.equal(resign(c({ trusted_applications_add: [app, app] })), false, "a duplicate in one list");
  assert.equal(resign(c({ trusted_applications_add: [app], trusted_applications_remove: [app] })), false, "added and removed in one change");
  assert.equal(resign(c({ trusted_applications_add: [app], trusted_applications_remove: [{ ...app, app_id: 8 }] })), true);
  assert.equal(resign({ clients: { "": { always_ask: true } } }), false, "an empty client id");
  assert.equal(resign({ clients: { ["x".repeat(201)]: { always_ask: true } } }), false, "an oversized client id");
  assert.equal(resign({ clients: { "bad\nid": { always_ask: true } } }), false, "a control character in a client id");
  assert.equal(resign({ clients: { ["\u{1F600}".repeat(150)]: { always_ask: true } } }), true, "200 code points, not code units");
  assert.equal(resign({ clients: { ["\u{1F600}".repeat(201)]: { always_ask: true } } }), false);
  assert.equal(valid("settings-change-signed", { ...clone(device.settings_changes[0].envelope.signed), change: { clients: { x: { unlink: true, always_ask: false } } } }), false, "the schema also refuses a mixed unlink");
});

test("a settings change is refused for another account, and a receipt of a device known not to be active is not authorized", () => {
  const s = device.settings_changes.find((x) => x.expected.accepted);
  assert.equal(verifyDeviceSigned(s.envelope, jwks, DO, { approverActive: true, sub: "pw_another_account" }), false);
  assert.equal(verifyDeviceSigned(s.envelope, jwks, DO, { approverActive: true }), false, "the account being changed is required");
  const v = v11.find((x) => accepted(x) && x.receipt.signed.decision === "approve");
  const o = { ...O, now: v.receipt.signed.expiry - 100 };
  assert.equal(verifyApproval(v.receipt, jwks, v.request, v.receipt.signed.binding_code, new Set(), o), true);
  assert.equal(verifyApproval(v.receipt, jwks, v.request, v.receipt.signed.binding_code, new Set(), { ...o, deviceActive: false }), false);
  for (const missing of [undefined, null, "yes", 1]) assert.equal(verifyApproval(v.receipt, jwks, v.request, v.receipt.signed.binding_code, new Set(), { ...o, deviceActive: missing }), false, "fails closed for " + String(missing));
  const old = v10.find((x) => x.receipt.signed.decision === "approve");
  assert.equal(verifyApproval(old.receipt, jwks, old.request, old.receipt.signed.binding_code, new Set(), { ...o, deviceActive: undefined, now: old.receipt.signed.expiry - 100 }), true, "1.0 receipts carry no such lookup");
});

test("the canonical-origin pattern is the same in the three schemas and in the verifier", () => {
  const fromSchema = (f, pick) => pick(load("schemas", f));
  const a = fromSchema("authorization-details-transaction.schema.json", (s) => s.$defs.origin.pattern);
  const b = fromSchema("consent-request.schema.json", (s) => s.allOf.find((x) => x.if?.required?.includes("client") && x.then?.properties?.origin).then.properties.origin.pattern);
  const c = fromSchema("settings-change-signed.schema.json", (s) => s.properties.change.properties.clients.additionalProperties.properties.origin.pattern);
  assert.equal(b, a);
  assert.equal(c, a);
  const lib = readFileSync(join(root, "scripts", "lib.mjs"), "utf8");
  const src = lib.match(/const ORIGIN = \/(.*)\/;/)[1].replaceAll("\\/", "/");
  assert.equal(src, a);
});

test("an enrollment proof needs the presence key's own signature over the same payload", () => {
  const e = device.enrollment_proofs.find((x) => x.expected.accepted);
  const ok = { approval_public_key: keys.new_approval, kid: keys.new_approval.kid, presence_kid: keys.new_presence.kid, usedNonces: new Set(), nonce: e.payload.nonce, presence_public_key: e.presence_public_key, presence_proof: e.presence_proof, now: e.payload.iat + 5 };
  assert.equal(verifyEnrollmentProof(e.jws, ok), true);
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, presence_proof: undefined }), false, "required");
  // Naming a key that the device does not hold: the second signature is made by another key.
  const other = signCompactJws(keys.presence, { ...e.header, typ: "biatec-device-enroll-presence+jwt", jwk: { kty: "EC", crv: "P-256", x: keys.presence.x, y: keys.presence.y } }, e.payload);
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, presence_proof: other }), false, "signed by a key that is not the presence key");
  // The approval key cannot sign the presence proof, and the proof must cover the same payload.
  const byApproval = signCompactJws(keys.new_approval, { alg: "ES256", typ: "biatec-device-enroll-presence+jwt", jwk: e.presence_public_key }, e.payload);
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, presence_proof: byApproval }), false);
  const otherPayload = signCompactJws(keys.new_presence, { alg: "ES256", typ: "biatec-device-enroll-presence+jwt", jwk: e.presence_public_key }, { ...e.payload, iat: e.payload.iat + 1 });
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, presence_proof: otherPayload }), false);
  const wrongTyp = signCompactJws(keys.new_presence, { alg: "ES256", typ: "JWT", jwk: e.presence_public_key }, e.payload);
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, presence_proof: wrongTyp }), false);
});

test("an enrollment proof with extra members (a private key in the header jwk, an extra claim) is refused", () => {
  const e = device.enrollment_proofs.find((x) => x.expected.accepted);
  const ok = { approval_public_key: keys.new_approval, kid: keys.new_approval.kid, presence_kid: keys.new_presence.kid, usedNonces: new Set(), nonce: e.payload.nonce, presence_public_key: e.presence_public_key, presence_proof: e.presence_proof, now: e.payload.iat + 5 };
  const withD = signCompactJws(keys.new_approval, { ...e.header, jwk: { ...e.header.jwk, d: keys.new_approval.d } }, e.payload);
  assert.equal(verifyEnrollmentProof(withD, ok), false);
  const extra = signCompactJws(keys.new_approval, e.header, { ...e.payload, note: "x" });
  assert.equal(verifyEnrollmentProof(extra, ok), false);
});

test("DNS-valid hosts only: labels up to 63 characters", () => {
  const base = clone(v11[0].request.authorization_details[0]);
  const withOrigin = (origin) => valid("authorization-details-transaction", { ...clone(base), origin });
  assert.equal(withOrigin("https://" + "a".repeat(63) + ".example"), true);
  assert.equal(withOrigin("https://" + "a".repeat(64) + ".example"), false);
});

test("ZWNJ, ZWJ and VS16 are allowed in context, refused at the ends and in runs", () => {
  assert.equal(isDisplaySafe("\u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645"), true, "Persian with ZWNJ");
  assert.equal(isDisplaySafe("family \u{1F468}\u200D\u{1F469}\u200D\u{1F467}"), true, "emoji ZWJ sequence");
  assert.equal(isDisplaySafe("heart \u2764\uFE0F"), true, "emoji presentation");
  assert.equal(isDisplaySafe("\u200Cbank"), false, "at the start");
  assert.equal(isDisplaySafe("bank\u200D"), false, "at the end");
  assert.equal(isDisplaySafe("a\u200C\u200Db"), false, "two in a row");
  assert.equal(isDisplaySafe("a \u200Db"), false, "next to white space");
  assert.equal(isDisplaySafe("x\uFE0F"), false, "VS16 after a letter");
});

test("the signed origin is bounded at the approval too", () => {
  const v = v11[0];
  const o = { ...O, now: v.receipt.signed.expiry - 100 };
  const long = "https://" + Array.from({ length: 40 }, () => "abcdefgh").join(".");
  assert.ok(long.length > 253);
  const req = clone(v.request);
  req.origin = long;
  req.authorization_details[0].origin = long;
  const r = clone(v.receipt);
  r.signed.authorization_details[0].origin = long;
  r.signature = signEs256(keys.device, Buffer.from(jcs(r.signed), "utf8"));
  assert.equal(verifyApproval(r, jwks, req, r.signed.binding_code, new Set(), o), false);
});

test("the settings schema accepts a label with ZWNJ, ZWJ and VS16 (the verifier decides the context)", () => {
  const base = clone(device.settings_changes[0].envelope.signed);
  base.change = { clients: { x: { label: "heart ❤️ می‌خ" } } };
  assert.equal(valid("settings-change-signed", base), true);
});

test("a pairing code that is not a string is refused even if its digits match", () => {
  const a = device.device_approvals.find((x) => x.expected.accepted);
  const e = clone(a.envelope);
  e.signed.pairing_code = Number(e.signed.pairing_code);
  e.signature = signEs256(keys.device, Buffer.from(jcs(e.signed), "utf8"));
  assert.equal(verifyDeviceSigned(e, jwks, DO, { approval: a.expected_inputs, sub: device.enrollment_sub, approverActive: true }), false);
});

test("the proven domain is a well-formed host", () => {
  const r = clone(v11[0].request);
  for (const d of ["..", "-", ".", "a..b", "-a.example"]) {
    r.client.domain = d;
    assert.equal(valid("consent-request", r), false, d);
  }
  r.client.domain = "chat.biatec.io";
  assert.equal(valid("consent-request", r), true);
});

test("canonical origins only, the transaction invariants are in the schema, and a pause names its origin", () => {
  const base = clone(v11[0].request.authorization_details[0]);
  const withOrigin = (origin) => ({ ...clone(base), origin });
  const okOrigin = (origin) => valid("authorization-details-transaction", withOrigin(origin));
  assert.equal(okOrigin("https://app.biatec.io"), true);
  assert.equal(okOrigin("http://localhost:5173"), true);
  for (const bad of ["https://App.Biatec.io", "https://app.biatec.io:99999", "https://app.biatec.io:0", "https://.", "https://-", "https://app.biatec.io/", "https://app.biatec.io:08", "https://a..b", "https://a.-b", "https://bank.example:443", "http://x:80"]) assert.equal(okOrigin(bad), false, bad);
  assert.equal(okOrigin("https://bank.example:8443"), true);
  assert.equal(okOrigin("http://x:443"), true);
  const unpriced = clone(base);
  delete unpriced.transfers[0].fiat_value;
  assert.equal(valid("authorization-details-transaction", unpriced), false, "a known value needs every transfer priced");
  const untrusted = clone(base);
  untrusted.applications = [{ id: 1, trusted: false }];
  assert.equal(valid("authorization-details-transaction", untrusted), false, "an untrusted application makes the value unknown");
  const empty = { ...clone(base), transfers: [], applications: [], value_known: false };
  delete empty.fiat_value;
  assert.equal(valid("authorization-details-transaction", empty), false, "an empty group");
  const change = (c) => valid("settings-change-signed", { ...clone(device.settings_changes[0].envelope.signed), change: { clients: { x: c } } });
  assert.equal(change({ paused: false }), false, "a pause needs its origin");
  assert.equal(change({ origin: "https://bank.example", always_ask: false }), false, "an origin without a pause");
  assert.equal(change({ paused: false, origin: "https://bank.example" }), true);
});

test("a pending 1.1 device cannot approve by relabelling its receipt version 1", () => {
  const v = v11.find((x) => accepted(x) && x.receipt.signed.decision === "approve");
  const old = v10.find((x) => x.receipt.signed.decision === "approve");
  const signed = { ...clone(old.receipt.signed), auth_req_id: v.receipt.signed.auth_req_id, sub: v.receipt.signed.sub };
  const relabelled = { ...clone(v.receipt), version: "1", signed, signature: signEs256(keys.device, Buffer.from(jcs(signed), "utf8")) };
  delete relabelled.device.key_use;
  const request = { ...clone(old.request), auth_req_id: signed.auth_req_id, sub: signed.sub, expiry: signed.expiry };
  const o = { ...O, now: signed.expiry - 100 };
  assert.equal(verifyReceipt(relabelled, jwks, o), true, "the receipt itself verifies: key use approve, shape 1.0");
  assert.equal(verifyApproval(relabelled, jwks, request, signed.binding_code, new Set(), { ...o, deviceActive: undefined }), false, "no active lookup: refused");
  assert.equal(verifyApproval(relabelled, jwks, request, signed.binding_code, new Set(), { ...o, deviceActive: true }), true);
});

test("the signed object of a device statement has exactly its members, and a settings change touches at least one client", () => {
  const base = device.settings_changes.find((x) => x.expected.accepted);
  const extra = clone(base.envelope);
  extra.signed.approver_note = "x";
  extra.signature = signEs256(keys.device, Buffer.from(jcs(extra.signed), "utf8"));
  assert.equal(verifyDeviceSigned(extra, jwks, DO, ACTIVE), false);
  const empty = clone(base.envelope);
  empty.signed.change = { clients: {} };
  empty.signature = signEs256(keys.device, Buffer.from(jcs(empty.signed), "utf8"));
  assert.equal(verifyDeviceSigned(empty, jwks, DO, ACTIVE), false);
  const a = device.device_approvals.find((x) => x.expected.accepted);
  const same = clone(a.envelope);
  same.signed.new_presence_jwk_thumbprint = same.signed.new_approval_jwk_thumbprint;
  same.signed.pairing_code = pairingCode(same.signed.new_approval_jwk_thumbprint, same.signed.new_presence_jwk_thumbprint, same.signed.nonce);
  same.signature = signEs256(keys.device, Buffer.from(jcs(same.signed), "utf8"));
  assert.equal(verifyDeviceSigned(same, jwks, DO, { approval: { ...a.expected_inputs, new_presence_jwk_thumbprint: same.signed.new_presence_jwk_thumbprint }, sub: device.enrollment_sub, approverActive: true }), false, "two keys, not one");
});

test("remember names only applications the card offered to trust (not already trusted ones)", () => {
  const v = v11.find((x) => x.request.authorization_details[0].applications?.some((a) => a.trusted) && accepted(x) && x.receipt.signed.decision === "approve");
  const o = { ...O, now: v.receipt.signed.expiry - 100 };
  const forged = clone(v.receipt);
  forged.signed.remember = { applications: [v.request.authorization_details[0].applications.find((a) => a.trusted).id] };
  forged.signature = signEs256(keys.device, Buffer.from(jcs(forged.signed), "utf8"));
  assert.equal(verifyApproval(forged, jwks, v.request, forged.signed.binding_code, new Set(), o), false);
});

test("remember: only on an approve of a 1.1 receipt, non-empty and without duplicates", () => {
  const v = v11.find((x) => x.receipt.signed.remember);
  const o = { ...O, now: v.receipt.signed.expiry - 100 };
  const resign = (r, key = keys.device) => ((r.signature = signEs256(key, Buffer.from(jcs(r.signed), "utf8"))), r);
  const dup = resign(clone(v.receipt));
  dup.signed.remember = { applications: [v.receipt.signed.remember.applications[0], v.receipt.signed.remember.applications[0]] };
  assert.equal(verifyApproval(resign(dup), jwks, v.request, dup.signed.binding_code, new Set(), o), false, "duplicates");
  const empty = clone(v.receipt);
  empty.signed.remember = { applications: [] };
  assert.equal(verifyApproval(resign(empty), jwks, v.request, empty.signed.binding_code, new Set(), o), false, "empty");
  const onDeny = clone(v11.find((x) => x.receipt.signed.decision === "deny").receipt);
  onDeny.signed.remember = { applications: [1] };
  assert.equal(verifyReceipt(resign(onDeny), jwks, O), false, "never on a denial");
});

test("a 1.1 transaction approval whose stored request carries no origin is refused", () => {
  const v = v11[0];
  const o = { ...O, now: v.receipt.signed.expiry - 100 };
  const bare = clone(v.request);
  delete bare.origin;
  delete bare.authorization_details[0].origin;
  const r = clone(v.receipt);
  delete r.signed.authorization_details[0].origin;
  r.signature = signEs256(keys.device, Buffer.from(jcs(r.signed), "utf8"));
  assert.equal(verifyApproval(r, jwks, bare, r.signed.binding_code, new Set(), o), false);
});

test("a device cannot approve itself, and a nonce of the wrong shape is refused", () => {
  const a = device.device_approvals.find((x) => x.expected.accepted);
  const ok = { approval: a.expected_inputs, sub: device.enrollment_sub, approverActive: true };
  assert.equal(verifyDeviceSigned(a.envelope, jwks, DO, ok), true);
  // The approver names itself as the new device: the approval key being enrolled signs its own approval.
  const self = clone(a.envelope);
  self.signed.new_approval_jwk_thumbprint = thumbprint(self.device.public_key);
  self.signed.pairing_code = pairingCode(self.signed.new_approval_jwk_thumbprint, self.signed.new_presence_jwk_thumbprint, self.signed.nonce);
  self.signature = signEs256(keys.device, Buffer.from(jcs(self.signed), "utf8"));
  assert.equal(verifyDeviceSigned(self, jwks, DO, { ...ok, approval: { ...a.expected_inputs, new_approval_jwk_thumbprint: self.signed.new_approval_jwk_thumbprint } }), false);
  const shortNonce = clone(a.envelope);
  shortNonce.signed.nonce = "short";
  shortNonce.signature = signEs256(keys.device, Buffer.from(jcs(shortNonce.signed), "utf8"));
  assert.equal(verifyDeviceSigned(shortNonce, jwks, DO, { ...ok, approval: { ...a.expected_inputs, nonce: "short" } }), false);
});

test("an enrollment proof is refused for keys that are not P-256 public keys", () => {
  const e = device.enrollment_proofs.find((x) => x.expected.accepted);
  const ok = { approval_public_key: keys.new_approval, kid: keys.new_approval.kid, presence_kid: keys.new_presence.kid, usedNonces: new Set(), nonce: e.payload.nonce, presence_public_key: e.presence_public_key, presence_proof: e.presence_proof, now: e.payload.iat + 5 };
  assert.equal(verifyEnrollmentProof(e.jws, ok), true);
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, presence_public_key: { kty: "EC", crv: "P-256", x: e.presence_public_key.x } }), false, "missing y");
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, presence_public_key: { ...e.presence_public_key, y: e.presence_public_key.x } }), false, "not on the curve");
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, approval_public_key: { kty: "EC", crv: "P-256", x: "AA", y: "AA" } }), false);
});

test("an enrollment proof whose approval and presence key are the same key is refused", () => {
  const e = device.enrollment_proofs.find((x) => x.expected.accepted);
  const payload = { ...e.payload, presence_jwk_thumbprint: thumbprint(keys.new_approval), presence_kid: e.payload.kid };
  const jws = signCompactJws(keys.new_approval, e.header, payload);
  assert.equal(verifyEnrollmentProof(jws, { approval_public_key: keys.new_approval, kid: e.payload.kid, presence_kid: e.payload.kid, usedNonces: new Set(), nonce: payload.nonce, presence_public_key: keys.new_approval, presence_proof: jws, now: payload.iat + 5 }), false);
});

test("pairing code: first four bytes of SHA-256(approval thumbprint . presence thumbprint . nonce), big-endian, mod 10^6, six digits", () => {
  const p = device.pairing_code;
  assert.equal(p.new_approval_jwk_thumbprint, thumbprint(keys.new_approval));
  assert.equal(p.new_presence_jwk_thumbprint, thumbprint(keys.new_presence));
  assert.equal(p.input, `${p.new_approval_jwk_thumbprint}.${p.new_presence_jwk_thumbprint}.${p.nonce}`);
  const digest = createHash("sha256").update(p.input, "utf8").digest();
  assert.equal(digest.toString("hex"), p.sha256_hex);
  assert.equal(digest.readUInt32BE(0), p.first_four_bytes_uint32);
  assert.equal(String(p.first_four_bytes_uint32 % 1_000_000).padStart(6, "0"), p.code);
  assert.equal(pairingCode(p.new_approval_jwk_thumbprint, p.new_presence_jwk_thumbprint, p.nonce), p.code);
  assert.match(p.code, /^[0-9]{6}$/);
  // Different keys or nonce give another code (not guaranteed for every input, but for this vector).
  assert.notEqual(pairingCode(p.new_presence_jwk_thumbprint, p.new_approval_jwk_thumbprint, p.nonce), p.code);
  // Zero padding.
  assert.equal(String(42 % 1_000_000).padStart(6, "0"), "000042");
});

for (const e of device.enrollment_proofs) {
  test(`enrollment proof ${e.name}`, () => {
    assert.ok(valid("device-enroll-proof", { header: e.header, payload: e.payload }), JSON.stringify(schema("device-enroll-proof").errors));
    assert.equal(e.header.typ, ENROLL_PROOF_TYP);
    const parts = e.jws.split(".");
    assert.deepEqual(JSON.parse(Buffer.from(parts[0], "base64url").toString()), e.header);
    assert.deepEqual(JSON.parse(Buffer.from(parts[1], "base64url").toString()), e.payload);
    assert.equal(verifyEnrollmentProof(e.jws, { approval_public_key: keys.new_approval, kid: keys.new_approval.kid, presence_kid: keys.new_presence.kid, usedNonces: new Set(), nonce: e.payload.nonce, presence_public_key: e.presence_public_key, presence_proof: e.presence_proof, now: e.payload.iat + 5 }), e.expected.accepted);
  });
}

test("an enrollment proof is refused for another nonce, another presence key or a stale iat", () => {
  const e = device.enrollment_proofs.find((x) => x.expected.accepted);
  const ok = { approval_public_key: keys.new_approval, kid: keys.new_approval.kid, presence_kid: keys.new_presence.kid, usedNonces: new Set(), nonce: e.payload.nonce, presence_public_key: e.presence_public_key, presence_proof: e.presence_proof, now: e.payload.iat + 5 };
  assert.equal(verifyEnrollmentProof(e.jws, ok), true);
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, approval_public_key: undefined }), false, "the approval key the server received is required");
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, approval_public_key: keys.new_presence }), false, "the proof must be signed by the approval key that was sent");
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, kid: "another-kid" }), false);
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, kid: undefined }), false, "the key id is required");
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, usedNonces: new Set([e.payload.nonce]) }), false, "a proof is single use");
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, usedNonces: undefined }), false, "the set of consumed nonces is required");
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, presence_kid: "another-presence-kid" }), false);
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, presence_kid: undefined }), false, "the presence key id is required");
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, nonce: "x".repeat(24) }), false);
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, presence_public_key: keys.presence }), false);
  assert.equal(verifyEnrollmentProof(e.jws, { ...ok, now: e.payload.iat + 301 }), false);
  const wrongTyp = signCompactJws(keys.new_approval, { ...e.header, typ: "JWT" }, e.payload);
  assert.equal(verifyEnrollmentProof(wrongTyp, ok), false, "typ");
});

test("test-only keys are the only private keys in the vectors", () => {
  const text = readFileSync(join(root, "vectors", "keys.json"), "utf8");
  assert.ok(JSON.parse(text)._warning.includes("TEST-ONLY"));
  for (const f of readdirSync(join(root, "vectors")).filter((n) => n !== "keys.json")) assert.equal(/"d"\s*:/.test(readFileSync(join(root, "vectors", f), "utf8")), false, f + " holds no private key");
});

// ---- the unsigned `version` cannot be used to get around a rule

/** A receipt the presence key signs over `signed`, with the attestation of the REFUSED vector (which says "presence"). */
function presenceApprove(signed, version) {
  const base = v11.find((v) => !accepted(v)).receipt;
  return { ...clone(base), version, signed, signature: signEs256(keys.presence, Buffer.from(jcs(signed), "utf8")) };
}

test("a presence-key approve cannot pass as a 1.0 receipt: version is not signed, the attested key use is honoured regardless", () => {
  const neg = v11.find((v) => !accepted(v));
  const old = v10.find((v) => v.receipt.signed.decision === "approve").receipt.signed;
  // The same approval re-labelled "1", with a 1.0-shaped signed object: the signature is valid, the attestation is the real one.
  const signed10 = { ...clone(old), auth_req_id: neg.receipt.signed.auth_req_id, sub: neg.receipt.signed.sub, binding_code: neg.receipt.signed.binding_code };
  const relabelled = presenceApprove(signed10, "1");
  delete relabelled.device.key_use;
  assert.ok(verifyReceiptSignature(relabelled), "the signature is valid");
  assert.equal(verifyReceipt(relabelled, jwks, O), false, "refused: the attestation says presence");
  // Relabelling the 1.1 receipt itself (signed object unchanged) is refused too: the version must agree with the signed shape.
  const asV1 = { ...clone(neg.receipt), version: "1" };
  assert.equal(verifyReceipt(asV1, jwks, O), false);
  // And a 1.0 receipt relabelled "1.1" (no key use attested, wrong shape) is refused.
  const asV11 = { ...clone(v10[0].receipt), version: "1.1" };
  assert.equal(verifyReceipt(asV11, jwks, O), false);
  assert.equal(verifyReceipt({ ...clone(v10[0].receipt), version: "2" }, jwks, O), false, "unknown version");
});

test("verifyApproval: the stored request decides the version; a receipt of the other version is refused", () => {
  const v = v11.find((x) => accepted(x) && x.receipt.signed.decision === "approve");
  const o = { ...O, now: v.receipt.signed.expiry - 100 };
  assert.equal(verifyApproval(v.receipt, jwks, v.request, v.receipt.signed.binding_code, new Set(), o), true);
  // A 1.1 request and a receipt wearing version "1": refused (also: both sides' undefined client_entity_id must not "match").
  const relabelled = { ...clone(v.receipt), version: "1" };
  assert.equal(verifyApproval(relabelled, jwks, v.request, v.receipt.signed.binding_code, new Set(), o), false);
  // A 1.0 request answered by a 1.1 receipt is refused as well.
  const old = v10.find((x) => x.receipt.signed.decision === "approve");
  assert.equal(verifyApproval(v.receipt, jwks, old.request, v.receipt.signed.binding_code, new Set(), o), false);
  // A request with no client at all matches nothing.
  const bare = clone(v.request);
  delete bare.client;
  assert.equal(verifyApproval(v.receipt, jwks, bare, v.receipt.signed.binding_code, new Set(), o), false);
});

test("verifyApproval: remember may only name applications of the request", () => {
  const v = v11.find((x) => x.receipt.signed.remember);
  const o = { ...O, now: v.receipt.signed.expiry - 100 };
  assert.equal(verifyApproval(v.receipt, jwks, v.request, v.receipt.signed.binding_code, new Set(), o), true, "the vector remembers an application it showed");
  const forged = clone(v.receipt);
  forged.signed.remember = { applications: [v.receipt.signed.remember.applications[0], 999999] };
  forged.signature = signEs256(keys.device, Buffer.from(jcs(forged.signed), "utf8")); // validly signed by the approval key
  assert.ok(verifyReceipt(forged, jwks, O), "the receipt itself verifies");
  assert.equal(verifyApproval(forged, jwks, v.request, forged.signed.binding_code, new Set(), o), false, "an application the card never showed");
});

test("a 1.1 document cannot carry the single-transfer details of 1.0, nor a 1.0 document the 1.1 details", () => {
  const r11 = clone(v11[0]);
  const old = v10[0].request.authorization_details[0];
  r11.receipt.signed.authorization_details = [old];
  assert.equal(valid("consent-receipt", r11.receipt), false, "1.1 receipt with 1.0 details");
  const req11 = clone(v11[0].request);
  req11.authorization_details = [old];
  assert.equal(valid("consent-request", req11), false, "1.1 request with 1.0 details");
  const req10 = clone(v10[0].request);
  req10.authorization_details = clone(v11[0].request.authorization_details);
  assert.equal(valid("consent-request", req10), false, "1.0 request with 1.1 details");
  const rec10 = clone(v10[0].receipt);
  rec10.signed.authorization_details = clone(v11[0].request.authorization_details);
  assert.equal(valid("consent-receipt", rec10), false, "1.0 receipt with 1.1 details");
});

test("a device approval is bound to the subject of the account being enrolled", () => {
  const a = device.device_approvals.find((x) => x.expected.accepted);
  assert.equal(verifyDeviceSigned(a.envelope, jwks, DO, { approval: a.expected_inputs, sub: device.enrollment_sub, approverActive: true }), true);
  assert.equal(verifyDeviceSigned(a.envelope, jwks, DO, { approval: a.expected_inputs, sub: "pw_another_account", approverActive: true }), false, "an active device of another account cannot approve");
});
