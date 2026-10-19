import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import { jcs, sha256Hex, verifyApproval, verifyReceipt, verifyReceiptSignature, verifyAttestation, signEs256 } from "../scripts/lib.mjs";

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
const O = { now: 1792425700, issuer: "https://oidc.biatec.test" };
const { vectors } = load("vectors", "receipts.json");
const canon = load("vectors", "canonicalization.json").vectors;

test("every schema compiles", () => {
  for (const f of readdirSync(join(root, "schemas"))) assert.ok(ajv.getSchema(load("schemas", f).$id), f);
});

for (const v of vectors) {
  test(`vector ${v.name}: schemas validate`, () => {
    assert.ok(valid("consent-request", v.request), JSON.stringify(schema("consent-request").errors));
    assert.ok(valid("consent-receipt", v.receipt), JSON.stringify(schema("consent-receipt").errors));
    assert.ok(valid("consent-receipt-signed", v.receipt.signed));
    assert.ok(valid("authorization-details", v.request.authorization_details));
    assert.ok(valid("token-response", { access_token: "x", token_type: "Bearer", expires_in: 300, consent_receipt: v.receipt }));
  });

  test(`vector ${v.name}: canonical form, hash and signatures verify`, () => {
    assert.equal(jcs(v.receipt.signed), v.canonical);
    assert.equal(sha256Hex(v.canonical), v.canonical_sha256);
    assert.ok(verifyReceiptSignature(v.receipt), "device signature");
    assert.ok(verifyAttestation(v.receipt, jwks, O), "attestation");
    assert.ok(verifyReceipt(v.receipt, jwks, O));
  });

  test(`vector ${v.name}: receipt matches its request (what you see is what you sign)`, () => {
    const s = v.receipt.signed;
    assert.deepEqual(s.authorization_details, v.request.authorization_details);
    assert.equal(s.auth_req_id, v.request.auth_req_id);
    assert.equal(s.client_entity_id, v.request.client.entity_id);
    assert.equal(s.binding_message, v.request.binding_message);
    assert.equal(s.expiry, v.request.expiry);
    assert.equal(s.sub, v.request.sub);
    const d = s.authorization_details[0];
    assert.equal(v.request.bound_hash, d.group_hash ?? d.request_hash);
  });

  test(`vector ${v.name}: any changed signed member invalidates the signature`, () => {
    const mutations = {
      authorization_details: (s) => {
        const d = s.authorization_details[0];
        if (d.amount) d.amount = "5000.000001";
        else d.requested_limits.per_day_eur = "50000";
      },
      auth_req_id: (s) => (s.auth_req_id += "0"),
      client_entity_id: (s) => (s.client_entity_id = "https://evil.example"),
      binding_code: (s) => (s.binding_code = "000000"),
      decision: (s) => (s.decision = s.decision === "approve" ? "deny" : "approve"),
      expiry: (s) => (s.expiry += 1),
      sub: (s) => (s.sub += "x"),
      binding_message: (s) => (s.binding_message += "!"),
    };
    assert.deepEqual(Object.keys(mutations).sort(), Object.keys(v.receipt.signed).sort());
    for (const [k, m] of Object.entries(mutations)) {
      const r = clone(v.receipt);
      m(r.signed);
      assert.equal(verifyReceiptSignature(r), false, k);
    }
  });
}

test("attestation is bound to sub and device key", () => {
  const v = vectors[0];
  const otherSub = clone(v.receipt);
  otherSub.signed.sub = "pw_other";
  assert.equal(verifyAttestation(otherSub, jwks, O), false);
  const otherKid = clone(v.receipt);
  otherKid.device.kid = "device-other";
  assert.equal(verifyAttestation(otherKid, jwks, O), false);
  assert.equal(verifyAttestation(v.receipt, { keys: [] }, O), false);
});

test("a signature by a different device key is rejected", () => {
  const keys = load("vectors", "keys.json");
  const v = clone(vectors[0]);
  v.receipt.signature = signEs256({ ...keys.oidc }, Buffer.from(v.canonical, "utf8"));
  assert.equal(verifyReceiptSignature(v.receipt), false);
});

test("receipt vectors cover both entries, all decisions and both detail types", () => {
  const entries = new Set(vectors.map((v) => v.request.entry));
  assert.deepEqual([...entries].sort(), ["direct", "step-up"]);
  assert.deepEqual([...new Set(vectors.map((v) => v.receipt.signed.decision))].sort(), ["approve", "code_failed", "deny"]);
  assert.deepEqual(
    [...new Set(vectors.map((v) => v.request.authorization_details[0].type))].sort(),
    ["biatec_limits_change", "biatec_transaction"],
  );
});

test("step-up and unverified-client receipts sign an empty binding_message; verified direct text is signed", () => {
  for (const v of vectors) {
    if (v.request.entry === "step-up" || !v.request.client.verified) assert.equal(v.receipt.signed.binding_message, "", v.name);
  }
  const unverified = vectors.find((v) => v.unsigned_client_binding_message);
  assert.ok(unverified && unverified.receipt.signed.binding_message === "");
  assert.ok(vectors.some((v) => v.request.entry === "direct" && v.request.client.verified && v.receipt.signed.binding_message !== ""));
});

test("limits change request_hash is SHA-256 of the JCS request body", () => {
  const v = vectors.find((x) => x.limits_request_body);
  assert.equal(v.request.authorization_details[0].request_hash, sha256Hex(jcs(v.limits_request_body)));
});

test("canonicalization vectors (RFC 8785)", () => {
  for (const c of canon) assert.equal(jcs(c.input), c.canonical, c.name);
  // RFC 8785 section 3.2.2 sample
  assert.equal(jcs({ numbers: [333333333.33333329, 1e30, 4.5, 2e-3, 1e-27] }), '{"numbers":[333333333.3333333,1e+30,4.5,0.002,1e-27]}');
});

test("schemas reject malformed objects", () => {
  const v = vectors[0];
  const bad = (name, mutate, target) => {
    const d = clone(target);
    mutate(d);
    assert.equal(valid(name, d), false, name);
  };
  bad("consent-receipt", (r) => (r.signed.amount = 1), v.receipt);
  bad("consent-receipt", (r) => (r.signed.binding_code = "12345"), v.receipt);
  bad("consent-receipt", (r) => (r.signed.decision = "maybe"), v.receipt);
  bad("consent-receipt", (r) => delete r.signed.binding_message, v.receipt);
  bad("consent-receipt", (r) => (r.alg = "none"), v.receipt);
  bad("consent-receipt", (r) => (r.signed.authorization_details[0].amount = 5000), v.receipt);
  bad("consent-receipt", (r) => (r.signed.authorization_details[0].type = "biatec_other"), v.receipt);
  bad("consent-request", (r) => (r.binding_code = "483920"), v.request);
  const unverified = vectors.find((x) => !x.request.client.verified).request;
  bad("consent-request", (r) => (r.client.display_name = "Example Bank"), unverified);
  bad("consent-request", (r) => delete r.client.fingerprint, unverified);
});

test("verifyApproval accepts the matching approve once and rejects deny, code_failed, expiry, replay and mismatches", () => {
  const o = { ...O, now: vectors[0].receipt.signed.expiry - 100 };
  const v = vectors.find((x) => x.receipt.signed.decision === "approve");
  const consumed = new Set();
  assert.equal(verifyApproval(v.receipt, jwks, v.request, v.receipt.signed.binding_code, consumed, o), true);
  assert.equal(verifyApproval(v.receipt, jwks, v.request, v.receipt.signed.binding_code, consumed, o), false, "replay");
  assert.equal(verifyApproval(v.receipt, jwks, v.request, v.receipt.signed.binding_code, new Set(), { ...o, now: v.receipt.signed.expiry }), false, "expired");
  for (const d of ["deny", "code_failed"]) {
    const x = vectors.find((y) => y.receipt.signed.decision === d);
    assert.ok(verifyReceipt(x.receipt, jwks, o), d + " signatures are valid");
    assert.equal(verifyApproval(x.receipt, jwks, x.request, x.receipt.signed.binding_code || "123456", new Set(), o), false, d);
  }
  // A genuine approval for another request must not satisfy this one.
  const other = vectors.find((x) => x !== v && x.receipt.signed.decision === "approve");
  assert.equal(verifyApproval(v.receipt, jwks, other.request, v.receipt.signed.binding_code, new Set(), o), false, "different request");
  assert.equal(verifyApproval(v.receipt, jwks, v.request, "000000", new Set(), o), false, "number matching: wrong code");
  assert.equal(verifyApproval(v.receipt, jwks, {}, v.receipt.signed.binding_code, new Set(), o), false, "malformed request does not throw");
  const changed = clone(v.request);
  changed.bound_hash = other.request.bound_hash;
  assert.equal(verifyApproval(v.receipt, jwks, changed, v.receipt.signed.binding_code, new Set(), o), false, "different bound hash");
});

test("attestation checks fail closed", () => {
  const r = vectors[0].receipt;
  assert.equal(verifyReceipt(r, jwks, { ...O, now: 4102444800 }), false, "expired attestation");
  assert.equal(verifyReceipt(r, jwks, { ...O, issuer: "https://evil.example" }), false, "issuer");
  assert.equal(verifyReceipt(r, jwks, { now: O.now }), false, "issuer is required");
  const enc = { keys: [{ ...jwks.keys[0], use: "enc" }] };
  assert.equal(verifyAttestation(r, enc, O), false, "key must be a signature key");
});

test("non-canonical base64url signatures are rejected", () => {
  const r = clone(vectors[0].receipt);
  const last = r.signature.slice(-1);
  const A = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
  const alt = A[(A.indexOf(last) & ~15) | ((A.indexOf(last) + 1) & 15)];
  assert.notEqual(alt, last);
  r.signature = r.signature.slice(0, -1) + alt;
  assert.equal(verifyReceiptSignature(r), false);
  assert.equal(verifyReceiptSignature({ ...vectors[0].receipt, signature: vectors[0].receipt.signature + "=" }), false);
});

test("verifiers return false instead of throwing on malformed input", () => {
  const r = clone(vectors[0].receipt);
  r.device.attestation = "garbage";
  assert.equal(verifyAttestation(r, jwks, O), false);
  r.device.attestation = "a.b.c";
  assert.equal(verifyReceipt(r, jwks, O), false);
  assert.equal(verifyReceipt({}, jwks, O), false);
  assert.equal(verifyReceipt(vectors[0].receipt, { keys: [{ kid: "oidc-test-key-1", kty: "oct" }] }, O), false);
});

test("schemas enforce cross-field rules", () => {
  const approve = vectors[0].receipt;
  const r = clone(approve);
  r.signed.binding_code = "";
  assert.equal(valid("consent-receipt", r), false, "approve needs a code");
  const stepUp = clone(vectors[0].request);
  stepUp.binding_message = "Pay me";
  assert.equal(valid("consent-request", stepUp), false, "step-up has no client text");
  const unverified = clone(vectors.find((v) => !v.request.client.verified).request);
  unverified.binding_message = "Example Bank refund";
  assert.equal(valid("consent-request", unverified), false, "unverified client has no text channel");
  const big = clone(approve);
  big.signed.expiry = 2 ** 53;
  assert.equal(valid("consent-receipt", big), false);
});

test("jcs rejects lone surrogates", () => {
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
