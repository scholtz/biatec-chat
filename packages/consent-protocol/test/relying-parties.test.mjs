// Protocol 1.2: relying parties. Schemas, vectors, details_hash, the text rules, IBAN, pinned verification and links.
import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import {
  RP_TYPES,
  clientFingerprint,
  detailsHash,
  isDisplaySafe,
  isRelyingPartyText,
  isValidDate,
  isValidIban,
  isValidLinkSigned,
  isValidPaymentAmount,
  isValidRelyingPartyDetails,
  jcs,
  sha256Hex,
  signEs256,
  verifyApproval,
  verifyAttestation,
  verifyLink,
  verifyLinkPinned,
  verifyReceipt,
  verifyReceiptPinned,
  verifyReceiptSignature,
} from "../scripts/lib.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const load = (...p) => JSON.parse(readFileSync(join(root, ...p), "utf8"));
const clone = (v) => structuredClone(v);

const ajv = new Ajv2020({ allErrors: true, strict: true });
for (const f of readdirSync(join(root, "schemas")).filter((n) => n.endsWith(".schema.json"))) ajv.addSchema(load("schemas", f));
const valid = (name, data) => {
  const v = ajv.getSchema(`https://schemas.biatec.io/consent/v1/${name}.schema.json`);
  assert.ok(v, "schema " + name);
  return v(data);
};

const jwks = load("vectors", "jwks.json");
const keys = load("vectors", "keys.json");
const file = load("vectors", "receipts-1.2.json");
const vectors = file.vectors;
const links = load("vectors", "link.json");
const textRules = load("vectors", "text-rules.json");
const v11 = load("vectors", "receipts.json").vectors;
const O = { now: file.now, issuer: file.issuer, deviceActive: true };
const pins = file.pinned_devices.map((p) => ({ key_use: p.key_use, jwk: p.jwk }));
const accepted = (v) => v.expected.accepted;
const pinnedVerdict = (v) => (v.expected_pinned ?? v.expected).accepted;
const pinnedOpts = (v, extra = {}) => ({
  now: file.now,
  origin: v.server_origin,
  clientFingerprint: v.request.client.fingerprint,
  sub: file.subject,
  auth_req_id: v.receipt.signed.auth_req_id,
  bindingCode: /^[0-9]{6}$/.test(v.receipt.signed.binding_code) ? v.receipt.signed.binding_code : "000000", // what the party displayed (a deny may carry none)
  consumed: new Set(),
  ...extra,
});
const byName = (n) => vectors.find((v) => v.name === n);
const resign = (receipt, key = keys.device) => {
  receipt.signature = signEs256(key, Buffer.from(jcs(receipt.signed), "utf8"));
  return receipt;
};

test("every 1.2 schema compiles, strictly", () => {
  for (const n of ["authorization-details-payment", "authorization-details-login", "authorization-details-action", "link-request", "link-signed", "link-created", "link-status"]) {
    assert.ok(ajv.getSchema(`https://schemas.biatec.io/consent/v1/${n}.schema.json`), n);
  }
});

// ---- the receipt vectors
test("1.2 receipt vectors cover approve and deny for each type and the negative cases", () => {
  const cell = (type, decision) => vectors.some((v) => v.request.authorization_details[0].type === type && v.receipt.signed.decision === decision && accepted(v));
  for (const t of RP_TYPES) for (const d of ["approve", "deny"]) assert.ok(cell(t, d), `${t} ${d}`);
  assert.ok(vectors.some((v) => v.request.authorization_details[0].creditor_account?.iban));
  assert.ok(vectors.some((v) => v.request.authorization_details[0].creditor_account?.identification));
  assert.ok(vectors.some((v) => v.server_origin === null) && vectors.some((v) => v.server_origin !== null), "with and without an origin");
  assert.ok(vectors.some((v) => v.name.includes("presence-key-REFUSED") && !accepted(v)));
  assert.ok(vectors.some((v) => v.name.includes("iban-altered") && !accepted(v)));
  assert.ok(vectors.some((v) => v.expected_pinned && v.expected.accepted && !v.expected_pinned.accepted), "an unpinned device");
  assert.ok(vectors.some((v) => v.receipt.device.key_use === "presence" && v.receipt.signed.decision === "deny" && accepted(v)), "a deny by the presence key");
});

for (const v of vectors) {
  test(`[1.2] ${v.name}: schemas validate`, () => {
    assert.equal(v.receipt.version, "1.2");
    assert.ok(valid("consent-request", v.request), JSON.stringify(ajv.errors));
    assert.ok(valid("consent-receipt", v.receipt), JSON.stringify(ajv.errors));
    assert.ok(valid("consent-receipt-signed", v.receipt.signed));
    assert.ok(valid("authorization-details", v.request.authorization_details));
    assert.ok(valid("authorization-details", v.receipt.signed.authorization_details));
    assert.ok(valid("token-response", { access_token: "x", token_type: "Bearer", expires_in: 300, consent_receipt: v.receipt }));
  });

  test(`[1.2] ${v.name}: canonical form, hashes and the verdict in both modes`, () => {
    assert.equal(jcs(v.receipt.signed), v.canonical);
    assert.equal(sha256Hex(v.canonical), v.canonical_sha256);
    assert.ok(verifyReceiptSignature(v.receipt), "the device signature is valid, also for a vector that must be refused");
    assert.ok(verifyAttestation(v.receipt, jwks, O), "the attestation is valid, also for a vector that must be refused");
    assert.equal(verifyReceipt(v.receipt, jwks, O), accepted(v), "attestation mode");
    assert.equal(verifyReceiptPinned(v.receipt, pins, v.sent_details, pinnedOpts(v)), pinnedVerdict(v), "pinned mode");
  });

  test(`[1.2] ${v.name}: details_hash is computed from the details object, and the request binds it`, () => {
    const sent = v.request.authorization_details[0];
    assert.equal(v.details_hash, detailsHash(sent));
    assert.equal(v.request.bound_hash, v.details_hash);
    assert.deepEqual(sent, v.server_origin ? { ...v.sent_details, origin: v.server_origin } : v.sent_details);
    assert.equal(v.request.origin, v.server_origin ?? undefined);
    assert.equal(v.request.binding_message, "");
    assert.equal(v.receipt.signed.binding_message, "");
    assert.equal(v.receipt.signed.client_fingerprint, v.request.client.fingerprint);
    assert.equal(v.request.client.fingerprint, clientFingerprint(v.client_id));
    if (!v.name.includes("iban-altered")) {
      assert.equal(v.receipt.signed.details_hash, detailsHash(v.receipt.signed.authorization_details[0]));
      assert.deepEqual(v.receipt.signed.authorization_details, v.request.authorization_details);
    }
  });

  test(`[1.2] ${v.name}: any changed signed member invalidates the signature`, () => {
    const mutations = {
      authorization_details: (s) => {
        const d = s.authorization_details[0];
        if (d.type === "biatec_payment") d.creditor_name += "!";
        else if (d.type === "biatec_login") d.context = { ...d.context, ip: "198.51.100.1" };
        else d.title += "!";
      },
      auth_req_id: (s) => (s.auth_req_id += "0"),
      client_fingerprint: (s) => (s.client_fingerprint = "0000000000000000"),
      binding_code: (s) => (s.binding_code = "000000"),
      decision: (s) => (s.decision = s.decision === "approve" ? "deny" : "approve"),
      expiry: (s) => (s.expiry += 1),
      sub: (s) => (s.sub += "x"),
      binding_message: (s) => (s.binding_message += "!"),
      details_hash: (s) => (s.details_hash = s.details_hash.slice(0, -1) + (s.details_hash.endsWith("A") ? "B" : "A")),
    };
    const members = Object.keys(v.receipt.signed);
    assert.deepEqual(members.sort(), Object.keys(mutations).sort());
    for (const k of members) {
      const r = clone(v.receipt);
      mutations[k](r.signed);
      assert.equal(verifyReceiptSignature(r), false, k);
    }
  });
}

test("verifyApproval accepts a 1.2 approval once and refuses deny, replay, expiry, a wrong code, another request and an unlinked client", () => {
  const v = byName("payment-approve");
  const o = { ...O, now: v.receipt.signed.expiry - 100 };
  const code = v.receipt.signed.binding_code;
  assert.equal(verifyApproval(v.receipt, jwks, v.request, code, new Set(), o), true);
  assert.equal(verifyApproval(v.receipt, jwks, v.request, code, new Set([v.receipt.signed.auth_req_id]), o), false, "replay");
  assert.equal(verifyApproval(v.receipt, jwks, v.request, code, new Set(), { ...o, now: v.receipt.signed.expiry }), false, "expired");
  assert.equal(verifyApproval(v.receipt, jwks, v.request, "000000", new Set(), o), false, "number matching");
  assert.equal(verifyApproval(v.receipt, jwks, v.request, code, new Set(), { ...o, deviceActive: false }), false, "a device that is not active");
  assert.equal(verifyApproval(byName("payment-deny").receipt, jwks, byName("payment-deny").request, "123456", new Set(), o), false, "a deny is not consent");
  const other = byName("login-approve");
  assert.equal(verifyApproval(v.receipt, jwks, other.request, code, new Set(), o), false, "another request");
  const unlinked = clone(v.request);
  unlinked.client.linked = false;
  assert.equal(verifyApproval(v.receipt, jwks, unlinked, code, new Set(), o), false, "a client that was not linked");
  const stepUp = clone(v.request);
  stepUp.entry = "step-up";
  assert.equal(verifyApproval(v.receipt, jwks, stepUp, code, new Set(), o), false, "only direct requests");
  const origin = clone(v.request);
  origin.origin = "https://evil.example";
  assert.equal(verifyApproval(v.receipt, jwks, origin, code, new Set(), o), false, "the recorded origin is the signed one");
  const noOrigin = clone(v.request);
  delete noOrigin.origin;
  assert.equal(verifyApproval(v.receipt, jwks, noOrigin, code, new Set(), o), false, "an origin in the details needs the same origin in the request");
  const bound = clone(v.request);
  bound.bound_hash = other.details_hash;
  assert.equal(verifyApproval(v.receipt, jwks, bound, code, new Set(), o), false, "another bound hash");
  assert.equal(verifyApproval(byName("payment-approve-signed-by-presence-key-REFUSED").receipt, jwks, byName("payment-approve-signed-by-presence-key-REFUSED").request, "208846", new Set(), o), false);
  const altered = byName("payment-approve-iban-altered-REFUSED");
  assert.equal(verifyApproval(altered.receipt, jwks, altered.request, "731950", new Set(), o), false);
});

test("the version cannot be relabelled: 1.2 content is refused as 1.1 and the reverse", () => {
  const r = clone(byName("payment-approve").receipt);
  r.version = "1.1";
  assert.equal(verifyReceipt(r, jwks, O), false);
  assert.equal(valid("consent-receipt", r), false);
  const old = clone(v11[0].receipt);
  old.version = "1.2";
  assert.equal(verifyReceipt(old, jwks, O), false);
  assert.equal(valid("consent-receipt", old), false);
  assert.equal(verifyReceiptPinned(r, pins, byName("payment-approve").sent_details, pinnedOpts(byName("payment-approve"))), false);
});

// ---- the details hash
test("details_hash: base64url, no padding, SHA-256 over RFC 8785 of the details object, origin included when the server added one", () => {
  const v = byName("payment-approve");
  const d = v.request.authorization_details[0];
  assert.equal(v.details_hash, Buffer.from(sha256Bytes(jcs(d))).toString("base64url"));
  assert.equal(v.details_hash.length, 43);
  assert.doesNotMatch(v.details_hash, /[=+/]/);
  assert.notEqual(detailsHash(v.sent_details), v.details_hash, "the origin is part of the hashed object");
  assert.notEqual(detailsHash({ ...d, origin: "https://other.example" }), v.details_hash);
  // member order does not matter, the values do
  const shuffled = { origin: d.origin, execution_date: d.execution_date, remittance: d.remittance, creditor_account: d.creditor_account, creditor_name: d.creditor_name, instructed_amount: { currency: "EUR", amount: "1250.00" }, type: d.type };
  assert.equal(detailsHash(shuffled), v.details_hash);
  assert.notEqual(detailsHash({ ...d, instructed_amount: { amount: "1250.01", currency: "EUR" } }), v.details_hash);
});

function sha256Bytes(text) {
  return Buffer.from(sha256Hex(text), "hex");
}

test("an IBAN altered by one character changes the details hash and fails the checksum", () => {
  const a = file.iban_alteration;
  assert.equal(a.original_iban.length, a.altered_iban.length);
  assert.equal([...a.original_iban].filter((c, i) => c !== a.altered_iban[i]).length, 1);
  assert.notEqual(a.original_details_hash, a.altered_details_hash);
  assert.equal(a.original_details_hash, byName("payment-approve").details_hash);
  assert.equal(isValidIban(a.original_iban), true);
  assert.equal(isValidIban(a.altered_iban), false);
  const altered = byName("payment-approve-iban-altered-REFUSED");
  const signedDetails = altered.receipt.signed.authorization_details[0];
  assert.equal(signedDetails.creditor_account.iban, a.altered_iban);
  assert.notEqual(detailsHash(signedDetails), altered.receipt.signed.details_hash, "the hash computed from what was signed is not the hash that was signed");
  assert.ok(verifyReceiptSignature(altered.receipt));
});

test("a re-signed receipt whose details_hash is copied, not computed, is refused (even with a valid IBAN)", () => {
  const v = byName("payment-approve");
  // a different payment (the amount changed) signed under the ORIGINAL hash
  const r = clone(v.receipt);
  r.signed.authorization_details[0].instructed_amount.amount = "9999.00";
  resign(r);
  assert.equal(verifyReceiptSignature(r), true);
  assert.equal(verifyReceipt(r, jwks, O), false);
  assert.equal(verifyReceiptPinned(r, pins, v.sent_details, pinnedOpts(v)), false);
  // consistent but different from what the party sent
  const s = clone(r);
  s.signed.details_hash = detailsHash(s.signed.authorization_details[0]);
  resign(s);
  assert.equal(verifyReceipt(s, jwks, O), true, "an authentic receipt of ANOTHER payment is valid in itself");
  assert.equal(verifyReceiptPinned(s, pins, v.sent_details, pinnedOpts(v)), false, "the party compares with what it sent");
});

// ---- pinned verification
test("verifyReceiptPinned: every member of what the party sent must match", () => {
  const v = byName("payment-approve");
  const ok = () => verifyReceiptPinned(v.receipt, pins, v.sent_details, pinnedOpts(v));
  assert.equal(ok(), true);
  for (const mutate of [
    (d) => (d.instructed_amount.amount = "1250.01"),
    (d) => (d.instructed_amount.currency = "USD"),
    (d) => (d.creditor_name = "Muster Energie GmbH."),
    (d) => (d.creditor_account.iban = "DE02120300000000202051"),
    (d) => (d.remittance = "Invoice 2026-0043"),
    (d) => delete d.remittance,
    (d) => (d.execution_date = "2026-10-21"),
    (d) => (d.extra = "x"),
  ]) {
    const sent = clone(v.sent_details);
    mutate(sent);
    assert.equal(verifyReceiptPinned(v.receipt, pins, sent, pinnedOpts(v)), false);
  }
  assert.equal(verifyReceiptPinned(v.receipt, pins, null, pinnedOpts(v)), false);
  assert.equal(verifyReceiptPinned(v.receipt, pins, [v.sent_details], pinnedOpts(v)), false);
});

test("verifyReceiptPinned: origin, decision, ids, fingerprint, code, expiry and replay", () => {
  const v = byName("payment-approve");
  const run = (extra, receipt = v.receipt) => verifyReceiptPinned(receipt, pins, v.sent_details, pinnedOpts(v, extra));
  assert.equal(run({ origin: "https://evil.example" }), false);
  assert.equal(run({ origin: null }), false, "an origin was signed but none expected");
  assert.equal(run({ origin: undefined }), false, "the origin is explicit: absent is refused");
  assert.equal(run({ origin: 5 }), false, "a string or null");
  assert.equal(verifyReceiptPinned(v.receipt, pins, v.sent_details, { now: file.now }), false, "no opts.origin at all");
  assert.equal(run({ decision: "approve" }), true);
  assert.equal(run({ decision: "deny" }), false);
  assert.equal(run({ auth_req_id: "ciba_other" }), false);
  assert.equal(run({ sub: "pw_other" }), false);
  assert.equal(run({ clientFingerprint: "0000000000000000" }), false);
  assert.equal(run({ bindingCode: "000000" }), false);
  assert.equal(run({ now: v.receipt.signed.expiry }), false, "expired");
  assert.equal(run({ now: v.receipt.signed.expiry, checkExpiry: false }), true, "archived re-verification");
  assert.equal(run({ consumed: new Set([v.receipt.signed.auth_req_id]) }), false, "replay");
  assert.equal(run({ consumed: new Set() }), true);
  assert.equal(run({ consumed: [] }), false, "a Set is required");
  // a deny does not expire
  const d = byName("payment-deny");
  assert.equal(verifyReceiptPinned(d.receipt, pins, d.sent_details, pinnedOpts(d, { now: d.receipt.signed.expiry + 1000 })), true);
  // the tag of the sent object: with its own origin it must be the signed one
  assert.equal(verifyReceiptPinned(v.receipt, pins, { ...v.sent_details, origin: v.server_origin }, pinnedOpts(v, { origin: v.server_origin })), true);
  assert.equal(verifyReceiptPinned(v.receipt, pins, { ...v.sent_details, origin: "https://evil.example" }, pinnedOpts(v, { origin: v.server_origin })), false, "expectedDetails.origin conflicts with opts.origin");
  assert.equal(verifyReceiptPinned(v.receipt, pins, { ...v.sent_details, origin: v.server_origin }, pinnedOpts(v, { origin: null })), false);
  // a receipt signed without an origin needs origin null, and a string is refused
  const nv = vectors.find((x) => x.server_origin === null && x.expected.accepted && x.receipt.signed.decision === "approve");
  if (nv) {
    assert.equal(verifyReceiptPinned(nv.receipt, pins, nv.sent_details, pinnedOpts(nv, { now: nv.receipt.signed.expiry - 1 })), true);
    assert.equal(verifyReceiptPinned(nv.receipt, pins, nv.sent_details, pinnedOpts(nv, { origin: "https://netbanking.bank.example", now: nv.receipt.signed.expiry - 1 })), false);
  }
});

test("verifyReceiptPinned: pins decide, the receipt's own device block does not", () => {
  const v = byName("payment-approve");
  const sent = v.sent_details;
  const o = pinnedOpts(v);
  assert.equal(verifyReceiptPinned(v.receipt, [pins[0]], sent, o), true);
  assert.equal(verifyReceiptPinned(v.receipt, pins[0].jwk, sent, o), true, "a bare JWK is an approval key");
  assert.equal(verifyReceiptPinned(v.receipt, [pins[1]], sent, o), false, "the presence pin never approves");
  assert.equal(verifyReceiptPinned(v.receipt, [{ key_use: "presence", jwk: pins[0].jwk }], sent, o), false, "an approval key pinned as a presence key never approves");
  assert.equal(verifyReceiptPinned(v.receipt, [], sent, o), false);
  assert.equal(verifyReceiptPinned(v.receipt, undefined, sent, o), false);
  assert.equal(verifyReceiptPinned(v.receipt, [{ key_use: "approve", jwk: { ...pins[0].jwk, d: "x" } }], sent, o), true, "extra members of a pin are ignored, the key is what counts");
  assert.equal(verifyReceiptPinned(v.receipt, [{ key_use: "approve", jwk: { kty: "EC", crv: "P-256", x: "x", y: "y" } }], sent, o), false);
  // the receipt carries a lying device block: it does not matter
  const lie = clone(v.receipt);
  lie.device = { kid: "x", key_use: "approve", public_key: { kty: "EC", crv: "P-256", x: "A".repeat(43), y: "A".repeat(43) }, attestation: "a.b.c" };
  assert.equal(verifyReceiptPinned(lie, pins, sent, o), true);
  // a deny verifies under either pin
  const d = byName("login-deny");
  assert.equal(d.receipt.device.key_use, "presence");
  assert.equal(verifyReceiptPinned(d.receipt, [pins[1]], d.sent_details, pinnedOpts(d)), true);
  assert.equal(verifyReceiptPinned(d.receipt, [pins[0]], d.sent_details, pinnedOpts(d)), false, "the key that did not sign");
  // the unpinned second phone
  const n = byName("payment-approve-new-device-unpinned");
  assert.equal(verifyReceiptPinned(n.receipt, pins, n.sent_details, pinnedOpts(n)), false);
  const newPin = { key_use: "approve", jwk: n.receipt.device.public_key };
  assert.equal(verifyReceiptPinned(n.receipt, [...pins, newPin], n.sent_details, pinnedOpts(n)), true, "once the party decided to pin it");
});

test("verifyReceiptPinned: shape and text rules are enforced on the signed object", () => {
  const v = byName("payment-approve");
  const forge = (mutate) => {
    const r = clone(v.receipt);
    mutate(r.signed);
    r.signed.details_hash = detailsHash(r.signed.authorization_details[0]);
    return resign(r);
  };
  const check = (r, sent = v.sent_details) => verifyReceiptPinned(r, pins, sent, pinnedOpts(v));
  assert.equal(check(forge(() => {})), true, "the forging helper itself is faithful");
  assert.equal(check(forge((s) => (s.binding_message = "Pay now"))), false, "a relying party has no text channel besides the details");
  assert.equal(check(forge((s) => (s.remember = { applications: [1] }))), false);
  assert.equal(check(forge((s) => (s.authorization_details[0].creditor_name = "Muster\u202eesrever"))), false, "text rules");
  assert.equal(check(forge((s) => (s.authorization_details[0].creditor_account.iban = "DE89370400440532013001"))), false, "IBAN checksum");
  assert.equal(check(forge((s) => (s.authorization_details[0].instructed_amount.amount = "0.00"))), false, "amount must be greater than zero");
  const wrongVersion = clone(v.receipt);
  wrongVersion.version = "1.1";
  assert.equal(check(wrongVersion), false);
  const noAlg = clone(v.receipt);
  noAlg.alg = "none";
  assert.equal(check(noAlg), false);
  const b64 = clone(v.receipt);
  b64.signature += "=";
  assert.equal(check(b64), false, "non-canonical signature encoding");
  for (const bad of [null, {}, { version: "1.2" }, "x"]) assert.equal(verifyReceiptPinned(bad, pins, v.sent_details, {}), false);
});

// ---- the schemas of the three types
const payment = () => clone(byName("payment-approve").sent_details);
const login = () => clone(byName("login-approve").sent_details);
const action = () => clone(byName("action-approve").sent_details);

test("payment schema: amount, currency, creditor, remittance, date", () => {
  const name = "authorization-details-payment";
  assert.equal(valid(name, payment()), true);
  const bad = (mutate) => {
    const d = payment();
    mutate(d);
    assert.equal(valid(name, d), false);
  };
  bad((d) => (d.instructed_amount.amount = 1250)); // never a number
  bad((d) => (d.instructed_amount.amount = "0"));
  bad((d) => (d.instructed_amount.amount = "0.00"));
  bad((d) => (d.instructed_amount.amount = "1.234"));
  bad((d) => (d.instructed_amount.amount = "-5.00"));
  bad((d) => (d.instructed_amount.amount = "1,50"));
  bad((d) => (d.instructed_amount.amount = "01.50"));
  bad((d) => (d.instructed_amount.amount = ".50"));
  bad((d) => (d.instructed_amount.currency = "eur"));
  bad((d) => (d.instructed_amount.currency = "EURO"));
  bad((d) => (d.instructed_amount.extra = 1));
  bad((d) => delete d.instructed_amount);
  bad((d) => (d.creditor_name = ""));
  bad((d) => (d.creditor_name = "x".repeat(71)));
  bad((d) => (d.creditor_account = {}));
  bad((d) => (d.creditor_account = { iban: "de89370400440532013000" }));
  bad((d) => (d.creditor_account = { iban: "DE89 3704 0044 0532 0130 00" }));
  bad((d) => (d.creditor_account = { iban: "DE89370400440532013000", identification: "1", scheme: "BBAN" }));
  bad((d) => (d.creditor_account = { identification: "1" }));
  bad((d) => (d.creditor_account = { identification: "x".repeat(35), scheme: "BBAN" }));
  bad((d) => (d.creditor_account = { identification: "1", scheme: "x".repeat(21) }));
  bad((d) => (d.remittance = "x".repeat(141)));
  bad((d) => (d.remittance = ""));
  bad((d) => (d.execution_date = "20.10.2026"));
  bad((d) => (d.origin = "https://x.example/path"));
  bad((d) => (d.type = "biatec_login"));
  bad((d) => (d.extra = true));
  const edge = payment();
  edge.instructed_amount.amount = "0.01";
  edge.creditor_name = "x".repeat(70);
  edge.remittance = "x".repeat(140);
  assert.equal(valid(name, edge), true);
  edge.creditor_account = { identification: "x".repeat(34), scheme: "x".repeat(20) };
  assert.equal(valid(name, edge), true);
});

test("login and action schemas", () => {
  assert.equal(valid("authorization-details-login", login()), true);
  assert.equal(valid("authorization-details-login", { type: "biatec_login" }), true);
  for (const bad of [{ type: "biatec_login", context: {} }, { type: "biatec_login", context: { ip: "" } }, { type: "biatec_login", context: { ip: "x".repeat(81) } }, { type: "biatec_login", context: { host: "x" } }, { type: "biatec_login", extra: 1 }])
    assert.equal(valid("authorization-details-login", bad), false);
  assert.equal(valid("authorization-details-login", { type: "biatec_login", context: { ip: "x".repeat(80), location: "x".repeat(80), user_agent: "x".repeat(80) } }), true);

  assert.equal(valid("authorization-details-action", action()), true);
  const line = { label: "a", value: "b" };
  assert.equal(valid("authorization-details-action", { type: "biatec_action", title: "x".repeat(60), lines: Array.from({ length: 8 }, () => ({ label: "x".repeat(30), value: "y".repeat(80) })) }), true);
  for (const bad of [
    { type: "biatec_action", title: "", lines: [line] },
    { type: "biatec_action", title: "x".repeat(61), lines: [line] },
    { type: "biatec_action", title: "t", lines: [] },
    { type: "biatec_action", title: "t", lines: Array.from({ length: 9 }, () => line) },
    { type: "biatec_action", title: "t", lines: [{ label: "x".repeat(31), value: "v" }] },
    { type: "biatec_action", title: "t", lines: [{ label: "l", value: "x".repeat(81) }] },
    { type: "biatec_action", title: "t", lines: [{ label: "l" }] },
    { type: "biatec_action", title: "t", lines: [{ label: "l", value: "v", extra: 1 }] },
    { type: "biatec_action", title: "t" },
  ])
    assert.equal(valid("authorization-details-action", bad), false);
});

test("the consent request and receipt accept the three types and keep the hash rule: exactly one of group_hash and details_hash", () => {
  const v = byName("payment-approve");
  // a transaction receipt carries group_hash inside its details and no details_hash
  const tx = clone(v11[0].receipt);
  tx.signed.details_hash = v.details_hash;
  assert.equal(valid("consent-receipt", tx), false, "details_hash in a 1.1 receipt");
  // a 1.2 receipt needs details_hash
  const noHash = clone(v.receipt);
  delete noHash.signed.details_hash;
  assert.equal(valid("consent-receipt", noHash), false);
  // a 1.2 receipt cannot carry a transaction, and the other way round
  const txIn12 = clone(v.receipt);
  txIn12.signed.authorization_details = clone(v11[0].receipt.signed.authorization_details);
  assert.equal(valid("consent-receipt", txIn12), false);
  const paymentIn11 = clone(v11[0].receipt);
  paymentIn11.signed.authorization_details = clone(v.receipt.signed.authorization_details);
  assert.equal(valid("consent-receipt", paymentIn11), false);
  // a payment detail cannot carry group_hash
  const gh = clone(v.receipt);
  gh.signed.authorization_details[0].group_hash = v11[0].request.bound_hash;
  assert.equal(valid("consent-receipt", gh), false);
  const badHash = clone(v.receipt);
  badHash.signed.details_hash = "not-a-hash";
  assert.equal(valid("consent-receipt", badHash), false);
  const padded = clone(v.receipt);
  padded.signed.details_hash += "=";
  assert.equal(valid("consent-receipt", padded), false);
  // 1.2: no remember, no client text, the key use is named
  const remember = clone(v.receipt);
  remember.signed.remember = { applications: [1] };
  assert.equal(valid("consent-receipt", remember), false);
  const msg = clone(v.receipt);
  msg.signed.binding_message = "Pay";
  assert.equal(valid("consent-receipt", msg), false);
  const noUse = clone(v.receipt);
  delete noUse.device.key_use;
  assert.equal(valid("consent-receipt", noUse), false);

  // requests: direct entry only, linked clients only, no client text, origin optional
  const req = (mutate) => {
    const r = clone(v.request);
    mutate(r);
    return valid("consent-request", r);
  };
  assert.equal(req(() => {}), true);
  assert.equal(req((r) => (r.entry = "step-up")), false);
  assert.equal(req((r) => (r.client.linked = false)), false);
  assert.equal(req((r) => (r.binding_message = "Pay me")), false);
  assert.equal(req((r) => delete r.origin), true, "the schema does not tie the two origins; verifyApproval does");
  assert.equal(req((r) => (r.origin = "https://x.example/path")), false);
  assert.equal(req((r) => (r.client = { entity_id: "x", verified: true, display_name: "n", domain: "d.example" })), false, "a 1.2 request names its client as 1.1 does");
  assert.equal(req((r) => (r.authorization_details[0].type = "biatec_other")), false);
  const txReq = clone(v11[0].request);
  delete txReq.origin;
  assert.equal(valid("consent-request", txReq), false, "a transaction request still needs its origin");
  assert.equal(valid("consent-request", Object.fromEntries(Object.entries(v.request).filter(([k]) => k !== "sub"))), false);
  const unproven = clone(byName("payment-account-identification-approve").request);
  assert.equal(valid("consent-request", unproven), true);
  assert.equal(unproven.client.domain_proven, false);
});

// ---- IBAN, amounts and dates
test("IBAN mod-97", () => {
  const good = ["DE89370400440532013000", "GB82WEST12345698765432", "FR1420041010050500013M02606", "SK3112000000198742637541", "NO9386011117947", "CH9300762011623852957", "AT611904300234573201", "NL91ABNA0417164300", "BE68539007547034"];
  for (const i of good) {
    assert.equal(isValidIban(i), true, i);
    // any single changed character fails: the check digits detect every single-character error
    for (let p = 0; p < i.length; p++) {
      for (const c of /[0-9]/.test(i[p]) ? "0123456789" : "ABCDEFGHIJKLMNOPQRSTUVWXYZ") {
        if (c === i[p]) continue; // (a digit swapped for a letter changes the length of the numeric string, which mod-97 does not promise to detect)
        const alt = i.slice(0, p) + c + i.slice(p + 1);
        if (/^[A-Z]{2}[0-9]{2}/.test(alt)) assert.equal(isValidIban(alt), false, alt);
      }
    }
  }
  for (const bad of ["", "DE89 3704 0044 0532 0130 00", "de89370400440532013000", "DE8937040044053201300", "DE893704004405320130000000000000000000", "1234567890123456", "DE00370400440532013000", 5, null, undefined])
    assert.equal(isValidIban(bad), false, String(bad));
});

test("amounts are decimal strings, at most two decimals, greater than zero", () => {
  for (const a of ["0.01", "1", "10", "1250.00", "89.9", "999999999999999.99"]) assert.equal(isValidPaymentAmount(a), true, a);
  for (const a of ["0", "0.0", "0.00", "00.10", "1.001", "-1", "+1", "1e3", "1,5", " 1", "1 ", ".5", "5.", "", "1000000000000000", 1, 1.5, null, undefined]) assert.equal(isValidPaymentAmount(a), false, String(a));
});

test("execution dates are real calendar dates", () => {
  for (const d of ["2026-10-20", "2028-02-29", "2026-12-31"]) assert.equal(isValidDate(d), true, d);
  for (const d of ["2026-02-29", "2026-13-01", "2026-00-10", "2026-04-31", "2026-1-1", "26-10-20", "2026-10-20T00:00:00Z", "", 20261020, null]) assert.equal(isValidDate(d), false, String(d));
});

test("isValidRelyingPartyDetails is the schema plus text rules, IBAN and date", () => {
  for (const v of vectors) if (!v.name.includes("iban-altered")) assert.equal(isValidRelyingPartyDetails(v.request.authorization_details[0]), true, v.name);
  const p = payment();
  assert.equal(isValidRelyingPartyDetails(p), true);
  assert.equal(isValidRelyingPartyDetails({ ...p, creditor_name: "Muster\nEnergie" }), false);
  assert.equal(isValidRelyingPartyDetails({ ...p, remittance: "Pay\u200bment" }), false);
  assert.equal(isValidRelyingPartyDetails({ ...p, execution_date: "2026-02-30" }), false);
  assert.equal(isValidRelyingPartyDetails({ ...p, creditor_account: { iban: "DE89370400440532013001" } }), false);
  assert.equal(isValidRelyingPartyDetails({ ...p, origin: "HTTPS://Bank.example" }), false);
  assert.equal(isValidRelyingPartyDetails({ type: "biatec_login", context: { ip: "1.2.3.4\u202e" } }), false);
  assert.equal(isValidRelyingPartyDetails({ type: "biatec_action", title: "T", lines: [{ label: "L", value: "V\r" }] }), false);
  assert.equal(isValidRelyingPartyDetails({ type: "biatec_transaction" }), false);
  assert.equal(isValidRelyingPartyDetails(null), false);
  assert.equal(isValidRelyingPartyDetails([p]), false);
  // every string member is under the text rules
  const strings = [
    [payment(), (d, t) => (d.creditor_name = t)],
    [payment(), (d, t) => (d.remittance = t)],
    [{ ...payment(), creditor_account: { identification: "1", scheme: "BBAN" } }, (d, t) => (d.creditor_account.identification = t)],
    [{ ...payment(), creditor_account: { identification: "1", scheme: "BBAN" } }, (d, t) => (d.creditor_account.scheme = t)],
    [login(), (d, t) => (d.context.ip = t)],
    [login(), (d, t) => (d.context.location = t)],
    [login(), (d, t) => (d.context.user_agent = t)],
    [action(), (d, t) => (d.title = t)],
    [action(), (d, t) => (d.lines[0].label = t)],
    [action(), (d, t) => (d.lines[2].value = t)],
  ];
  for (const [base, set] of strings) {
    assert.equal(isValidRelyingPartyDetails(base), true);
    for (const t of ["a\u202eb", "a\u200bb", "a\u200db", "a\nb", "a\u0000b", "e\u0301", " a"]) {
      const d = clone(base);
      set(d, t);
      assert.equal(isValidRelyingPartyDetails(d), false, JSON.stringify(t));
    }
  }
});

// ---- text rules
for (const t of textRules.vectors) {
  test(`text rule ${t.profile} ${t.name}: ${t.accepted ? "accepted" : "refused"} (${t.reason})`, () => {
    const verdict = t.profile === "label" ? isDisplaySafe(t.text, t.max) : isRelyingPartyText(t.text, t.max);
    assert.equal(verdict, t.accepted);
  });
}

test("text rule vectors cover every refusal class of the spec, both profiles and the core subset", () => {
  const reasons = new Set(textRules.vectors.map((t) => t.reason));
  for (const r of ["bidirectional_control", "zero_width", "line_break", "control_character", "not_nfc", "too_long", "empty", "padding", "run_of_spaces", "blank", "invisible_character", "relying_party_profile_refuses_joiners"]) assert.ok(reasons.has(r), r);
  assert.deepEqual([...new Set(textRules.vectors.map((t) => t.profile))].sort(), ["label", "relying_party"]);
  // all twelve bidirectional controls and all five zero-width characters are listed one by one
  assert.equal(textRules.vectors.filter((t) => t.reason === "bidirectional_control" && t.profile === "relying_party").length, 12);
  assert.equal(textRules.vectors.filter((t) => t.reason === "zero_width" && t.profile === "relying_party").length, 5);
  assert.ok(textRules.vectors.filter((t) => t.core).every((t) => t.profile === "relying_party"));
});

test("text rules: a lone surrogate is refused, nothing is stripped, and the label profile is the superset only in joiners", () => {
  assert.equal(isRelyingPartyText("a\ud800b", 20), false);
  assert.equal(isRelyingPartyText("a\u200bb", 20), false);
  assert.equal("a\u200bb".length, 3, "the input is untouched");
  for (const t of textRules.vectors) if (t.profile === "relying_party" && t.accepted) assert.equal(isDisplaySafe(t.text, t.max), true, "what the relying-party profile accepts, the label profile accepts: " + t.name);
  assert.equal(isRelyingPartyText("abc", 2), false);
  assert.equal(isRelyingPartyText(5, 10), false);
  assert.equal(isRelyingPartyText(undefined, 10), false);
});

// ---- links
for (const v of links.vectors) {
  test(`[link] ${v.name}: schemas validate`, () => {
    assert.ok(valid("link-signed", v.envelope.signed), JSON.stringify(ajv.errors));
    assert.ok(valid("device-signed", v.envelope), JSON.stringify(ajv.errors));
    assert.ok(valid("link-request", v.link_request), JSON.stringify(ajv.errors));
    assert.ok(valid("link-created", v.link_created), JSON.stringify(ajv.errors));
    assert.ok(isValidLinkSigned(v.envelope.signed));
  });

  test(`[link] ${v.name}: canonical form and the verdict in both modes`, () => {
    assert.equal(jcs(v.envelope.signed), v.canonical);
    assert.equal(sha256Hex(v.canonical), v.canonical_sha256);
    const o = { now: links.now, issuer: links.issuer, consumed: new Set() };
    assert.equal(verifyLink(v.envelope, jwks, o, v.expected_inputs), v.expected.accepted, "attestation mode");
    const { sub, ...pinnedInputs } = v.expected_inputs;
    assert.equal(verifyLinkPinned({ signed: v.envelope.signed, signature: v.envelope.signature, alg: "ES256" }, links.pinned_devices, pinnedInputs, { now: links.now, consumed: new Set() }), v.expected.accepted, "pinned mode");
  });

  test(`[link] ${v.name}: what the device saw is what it signed`, () => {
    const s = v.envelope.signed;
    assert.equal(s.link_id, v.link_request.link_id);
    assert.equal(s.nonce, v.link_request.nonce);
    assert.equal(s.client_fingerprint, v.link_request.client.fingerprint);
    assert.equal(s.client_fingerprint, clientFingerprint(v.client_id));
    assert.equal(v.expected_inputs.domain, v.link_request.client.domain ?? null);
    if (v.expected.accepted) assert.equal(s.domain, v.link_request.client.domain ?? null);
    const u = new URL(v.link_created.link_uri);
    assert.equal(u.protocol, "biatec-authenticator:");
    assert.equal(u.host, "link");
    assert.equal(u.searchParams.get("iss"), links.issuer);
    assert.equal(u.searchParams.get("id"), v.link_created.link_id);
    assert.equal(u.searchParams.get("id"), s.link_id);
    assert.match(u.searchParams.get("s"), /^[A-Za-z0-9_-]{43}$/);
    assert.deepEqual([...u.searchParams.keys()], ["iss", "id", "s"]);
  });

  test(`[link] ${v.name}: any changed signed member invalidates the signature`, () => {
    const mutations = {
      type: (s) => (s.type = "biatec_links"),
      link_id: (s) => (s.link_id += "0"),
      client_fingerprint: (s) => (s.client_fingerprint = "0000000000000000"),
      domain: (s) => (s.domain = s.domain === null ? "evil.example" : null),
      decision: (s) => (s.decision = s.decision === "approve" ? "deny" : "approve"),
      binding_code: (s) => (s.binding_code = s.binding_code === "000000" ? "000001" : "000000"),
      nonce: (s) => (s.nonce += "0"),
      issued_at: (s) => (s.issued_at += 1),
      kid: (s) => (s.kid += "x"),
    };
    assert.deepEqual(Object.keys(v.envelope.signed).sort(), Object.keys(mutations).sort());
    for (const k of Object.keys(mutations)) {
      const e = clone(v.envelope);
      mutations[k](e.signed);
      assert.equal(verifyReceiptSignature({ alg: "ES256", signed: e.signed, signature: e.signature, device: e.device }), false, k);
    }
  });
}

test("link vectors: approve, deny, wrong domain, presence key, wrong code; with and without a proven domain", () => {
  const cell = (d) => links.vectors.some((v) => v.envelope.signed.decision === d && v.expected.accepted);
  assert.ok(cell("approve") && cell("deny"));
  assert.ok(links.vectors.some((v) => v.name.includes("wrong-domain") && !v.expected.accepted));
  assert.ok(links.vectors.some((v) => v.name.includes("presence-key") && !v.expected.accepted && v.envelope.device.key_use === "presence"));
  assert.ok(links.vectors.some((v) => v.envelope.signed.domain === null && v.expected.accepted));
  assert.ok(links.vectors.some((v) => typeof v.envelope.signed.domain === "string" && v.expected.accepted));
});

test("verifyLink and verifyLinkPinned refuse what does not match, replay and stale statements", () => {
  const v = links.vectors.find((x) => x.name === "link-approve-domain-proven");
  const o = () => ({ now: links.now, issuer: links.issuer, consumed: new Set() });
  const inputs = (over = {}) => ({ ...v.expected_inputs, ...over });
  const pin = (e, over = {}, now = links.now) => {
    const { sub, ...rest } = inputs(over);
    return verifyLinkPinned({ signed: e.signed, signature: e.signature, alg: "ES256" }, links.pinned_devices, rest, { now, consumed: new Set() });
  };
  assert.equal(verifyLink(v.envelope, jwks, o(), inputs()), true);
  assert.equal(pin(v.envelope), true);
  for (const over of [{ link_id: "lnk_other_other_other0" }, { client_fingerprint: "0000000000000000" }, { domain: "other.example" }, { domain: null }, { nonce: "link-nonce-other-other-0" }, { binding_code: "000000" }, { binding_code: undefined }]) {
    assert.equal(verifyLink(v.envelope, jwks, o(), inputs(over)), false, JSON.stringify(over));
    assert.equal(pin(v.envelope, over), false, JSON.stringify(over));
  }
  assert.equal(verifyLink(v.envelope, jwks, o(), inputs({ sub: "pw_other" })), false, "attested for another subject");
  assert.equal(verifyLink(v.envelope, jwks, o(), { ...inputs(), sub: undefined }), false);
  assert.equal(verifyLink(v.envelope, jwks, { now: links.now, issuer: links.issuer }, inputs()), false, "the set of spent links is required");
  assert.equal(verifyLink(v.envelope, jwks, { ...o(), consumed: new Set([v.envelope.signed.link_id]) }, inputs()), false, "a link is single use");
  assert.equal(verifyLink(v.envelope, jwks, { ...o(), issuer: "https://evil.example" }, inputs()), false);
  assert.equal(verifyLink(v.envelope, jwks, { ...o(), now: links.now + 3600 }, inputs()), false, "stale");
  assert.equal(pin(v.envelope, {}, links.now + 3600), false, "stale");
  assert.equal(verifyLink(v.envelope, { keys: [] }, o(), inputs()), false);
  // the key named in the statement is the key that signed
  const other = clone(v.envelope);
  other.device.kid = "device-other";
  assert.equal(verifyLink(other, jwks, o(), inputs()), false);
  // pinned: the device list decides
  const noDevices = verifyLinkPinned({ signed: v.envelope.signed, signature: v.envelope.signature, alg: "ES256" }, [], (({ sub, ...r }) => r)(v.expected_inputs), { now: links.now, consumed: new Set() });
  assert.equal(noDevices, false);
  const asPresence = links.pinned_devices.map((d) => ({ ...d, key_use: "presence" }));
  assert.equal(verifyLinkPinned({ signed: v.envelope.signed, signature: v.envelope.signature, alg: "ES256" }, asPresence, (({ sub, ...r }) => r)(v.expected_inputs), { now: links.now, consumed: new Set() }), false, "an approval signed by a key listed as presence");
  assert.equal(verifyLinkPinned({ signed: v.envelope.signed, signature: v.envelope.signature, alg: "ES256" }, links.pinned_devices, (({ sub, ...r }) => r)(v.expected_inputs), { now: links.now, consumed: new Set([v.envelope.signed.link_id]) }), false);
  for (const bad of [null, {}, { signed: {} }]) {
    assert.equal(verifyLink(bad, jwks, o(), inputs()), false);
    assert.equal(verifyLinkPinned(bad, links.pinned_devices, inputs(), { consumed: new Set() }), false);
  }
  // signature re-made with another key
  const forged = clone(v.envelope);
  forged.signature = signEs256(keys.presence, Buffer.from(jcs(forged.signed), "utf8"));
  assert.equal(verifyLink(forged, jwks, o(), inputs()), false);
  assert.equal(pin(forged), false);
});

test("a deny of a link may be signed by either key and does not need the code; an approve needs one", () => {
  const d = links.vectors.find((x) => x.name === "link-deny");
  assert.equal(d.envelope.signed.binding_code, "");
  assert.equal(d.envelope.device.key_use, "presence");
  const { sub, ...rest } = d.expected_inputs;
  assert.equal(verifyLinkPinned({ signed: d.envelope.signed, signature: d.envelope.signature, alg: "ES256" }, links.pinned_devices, { ...rest, binding_code: undefined }, { now: links.now, consumed: new Set() }), true);
  const a = clone(links.vectors.find((x) => x.name === "link-approve-domain-proven").envelope.signed);
  a.binding_code = "";
  assert.equal(valid("link-signed", a), false);
  assert.equal(isValidLinkSigned(a), false);
});

test("link schemas: link request, link created, link status", () => {
  const v = links.vectors[0];
  const bad = (name, mutate, base) => {
    const d = clone(base);
    mutate(d);
    assert.equal(valid(name, d), false);
  };
  bad("link-request", (d) => (d.client.domain_proven = false), v.link_request); // a domain without being proven
  bad("link-request", (d) => delete d.client.domain, v.link_request);
  bad("link-request", (d) => (d.extra = 1), v.link_request);
  bad("link-request", (d) => (d.user_label = "x".repeat(41)), v.link_request);
  bad("link-request", (d) => (d.client.linked = true), v.link_request);
  bad("link-created", (d) => (d.link_uri = d.link_uri.replace("biatec-authenticator", "https")), v.link_created);
  bad("link-created", (d) => (d.binding_code = "12345"), v.link_created);
  bad("link-created", (d) => (d.expires_in = 301), v.link_created);
  bad("link-signed", (d) => (d.decision = "code_failed"), v.envelope.signed);
  bad("link-signed", (d) => (d.domain = "BANK.example"), v.envelope.signed);
  bad("link-signed", (d) => (d.nonce = "short"), v.envelope.signed);
  bad("link-signed", (d) => delete d.domain, v.envelope.signed); // null is a value, absent is not
  bad("link-signed", (d) => (d.extra = 1), v.envelope.signed);

  const approval = { signed: v.envelope.signed, signature: v.envelope.signature, attestation: v.envelope.device.attestation };
  const linked = { status: "linked", subject: links.subject, devices: links.pinned_devices, approval };
  assert.equal(valid("link-status", linked), true, JSON.stringify(ajv.errors));
  assert.equal(valid("link-status", { status: "pending" }), true);
  assert.equal(valid("link-status", { status: "expired" }), true);
  assert.equal(valid("link-status", { status: "denied", approval }), true);
  for (const b of [{ status: "linked" }, { status: "linked", subject: "x", devices: [] }, { status: "pending", subject: "x" }, { status: "denied" }, { status: "denied", approval, subject: "x" }, { status: "maybe" }, { ...linked, extra: 1 }])
    assert.equal(valid("link-status", b), false, JSON.stringify(b).slice(0, 60));
  bad("link-status", (d) => (d.devices[0].key_use = "other"), linked);
  bad("link-status", (d) => (d.devices[0].jwk.d = "x"), linked);
});

test("the generator's --1.2 mode touches only the three new files and refuses to overwrite them without --force", async () => {
  const { spawnSync } = await import("node:child_process");
  const { cpSync, mkdtempSync, readFileSync: read, writeFileSync, rmSync, existsSync } = await import("node:fs");
  const { tmpdir } = await import("node:os");
  const src = root;
  const dir = mkdtempSync(join(tmpdir(), "gen-12-"));
  cpSync(join(src, "scripts"), join(dir, "scripts"), { recursive: true });
  cpSync(join(src, "vectors"), join(dir, "vectors"), { recursive: true });
  const names = ["receipts.json", "receipts-1.0.json", "device-signed.json", "keys.json", "jwks.json", "canonicalization.json", "receipts-1.2.json", "link.json", "text-rules.json"];
  const snapshot = () => Object.fromEntries(names.map((n) => [n, read(join(dir, "vectors", n), "utf8")]));
  const before = snapshot();
  const refuse = spawnSync(process.execPath, [join(dir, "scripts", "generate-vectors.mjs"), "--1.2"], { encoding: "utf8" });
  assert.notEqual(refuse.status, 0);
  assert.match(refuse.stderr, /--force/);
  assert.deepEqual(snapshot(), before);
  const run = spawnSync(process.execPath, [join(dir, "scripts", "generate-vectors.mjs"), "--1.2", "--force"], { encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr);
  const after = snapshot();
  for (const n of ["receipts.json", "receipts-1.0.json", "device-signed.json", "keys.json", "jwks.json", "canonicalization.json"]) assert.equal(after[n], before[n], n + " is frozen");
  assert.notEqual(after["receipts-1.2.json"], before["receipts-1.2.json"], "randomized signatures: the new files are regenerated");
  assert.equal(after["text-rules.json"], before["text-rules.json"], "no signatures in the text rules: reproducible");
  // a missing new file is generated without --force
  rmSync(join(dir, "vectors", "link.json"));
  rmSync(join(dir, "vectors", "receipts-1.2.json"));
  rmSync(join(dir, "vectors", "text-rules.json"));
  const fresh = spawnSync(process.execPath, [join(dir, "scripts", "generate-vectors.mjs"), "--1.2"], { encoding: "utf8" });
  assert.equal(fresh.status, 0, fresh.stderr);
  for (const n of ["receipts-1.2.json", "link.json", "text-rules.json"]) assert.ok(existsSync(join(dir, "vectors", n)), n);
  for (const n of ["receipts.json", "device-signed.json", "keys.json"]) assert.equal(read(join(dir, "vectors", n), "utf8"), before[n], n);
  writeFileSync(join(dir, "done"), "");
});

test("backchannel request: a relying party sends scope openid and one details object without origin", () => {
  const sent = byName("payment-approve").sent_details;
  const ok = { scope: "openid", login_hint: file.subject, authorization_details: [sent], origin: "https://netbanking.bank.example", requested_expiry: 300 };
  assert.equal(valid("backchannel-authentication-request", ok), true, JSON.stringify(ajv.errors));
  assert.equal(valid("backchannel-authentication-request", { scope: "openid", login_hint: "s", authorization_details: [login()] }), true);
  const bad = (mutate) => {
    const r = clone(ok);
    mutate(r);
    assert.equal(valid("backchannel-authentication-request", r), false);
  };
  bad((r) => delete r.authorization_details);
  bad((r) => (r.authorization_details = []));
  bad((r) => (r.authorization_details = [sent, sent]));
  bad((r) => (r.authorization_details[0].origin = "https://evil.example")); // the server adds the origin
  bad((r) => (r.authorization_details[0].extra = 1));
  bad((r) => (r.authorization_details[0].type = "biatec_transaction"));
  bad((r) => (r.unsigned_group = ["AA"]));
  bad((r) => (r.resource = "https://api.biatec.io"));
  bad((r) => (r.binding_message = "Pay"));
  bad((r) => (r.scope = "sign"));
  // the wallet scopes still refuse client details
  assert.equal(valid("backchannel-authentication-request", { scope: "sign", resource: "r", login_hint: "u", origin: "https://bank.example", network: "algorand-mainnet", address: "A", unsigned_group: ["AA"], authorization_details: [sent] }), false);
});

test("verifyReceiptPinned requires the party's own values: auth_req_id, sub, clientFingerprint, bindingCode and a consumed Set; a spent id is refused", () => {
  const v = byName("payment-approve");
  const ok = (extra) => verifyReceiptPinned(v.receipt, pins, v.sent_details, pinnedOpts(v, extra));
  assert.equal(ok({}), true);
  for (const k of ["auth_req_id", "sub", "clientFingerprint", "bindingCode", "consumed"]) assert.equal(ok({ [k]: undefined }), false, k + " is required");
  assert.equal(ok({ consumed: [] }), false, "a Set");
  assert.equal(ok({ bindingCode: "12345" }), false);
  assert.equal(ok({ clientFingerprint: "xyz" }), false);
  const spent = new Set();
  assert.equal(ok({ consumed: spent }), true);
  assert.equal(spent.size, 0, "the function never adds to the set");
  spent.add(v.receipt.signed.auth_req_id); // the caller records the id after it acted
  assert.equal(ok({ consumed: spent }), false, "replay");
  // a deny needs the same values
  const d = byName("payment-deny");
  assert.equal(verifyReceiptPinned(d.receipt, pins, d.sent_details, pinnedOpts(d, { consumed: undefined })), false);
  assert.equal(verifyReceiptPinned(d.receipt, pins, d.sent_details, pinnedOpts(d, { sub: undefined })), false);
});

test("verifyLinkPinned requires alg ES256 and a consumed Set; a spent link id is refused", () => {
  const v = links.vectors.find((x) => x.expected.accepted && x.envelope.signed.decision === "approve");
  const { sub, ...inputs } = v.expected_inputs;
  const run = (approval, o) => verifyLinkPinned(approval, links.pinned_devices, inputs, { now: links.now, ...o });
  const base = { signed: v.envelope.signed, signature: v.envelope.signature, alg: "ES256" };
  assert.equal(run(base, { consumed: new Set() }), true);
  assert.equal(run(base, {}), false, "consumed is required");
  assert.equal(run({ signed: base.signed, signature: base.signature }, { consumed: new Set() }), false, "alg is required");
  for (const alg of ["none", "HS256", "PS256", "es256"]) assert.equal(run({ ...base, alg }, { consumed: new Set() }), false, alg);
  assert.equal(run(base, { consumed: new Set([v.envelope.signed.link_id]) }), false, "single use");
});
