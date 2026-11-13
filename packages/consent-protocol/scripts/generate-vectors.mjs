// Regenerates vectors/keys.json (adds any missing test key), vectors/receipts.json (protocol 1.1) and vectors/device-signed.json,
// and vectors/canonicalization.json only when it is missing (it has no signatures, so it never needs rewriting). vectors/receipts-1.0.json is the frozen protocol 1.0 set and is never regenerated.
// ECDSA signatures are randomized, so vectors are committed; rerun only to extend them.
// All keys are TEST-ONLY and exist solely so the vectors can be reproduced. They protect nothing.
import { generateKeyPairSync, createHash } from "node:crypto";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { ATTESTATION_TYP, ENROLL_PROOF_TYP, jcs, pairingCode, sha256Hex, signEs256, signCompactJws, thumbprint, verifyApproval, verifyDeviceSigned, verifyReceipt } from "./lib.mjs";

const dir = join(dirname(fileURLToPath(import.meta.url)), "..", "vectors");
const write = (name, v) => writeFileSync(join(dir, name), JSON.stringify(v, null, 2) + "\n");

const newKey = (kid) => {
  const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
  return { ...privateKey.export({ format: "jwk" }), kid, alg: "ES256", use: "sig" };
};
const publicOf = ({ d, ...pub }) => pub;
const pubJwk = (k) => ({ kty: "EC", crv: "P-256", x: k.x, y: k.y });

// ---- keys: the Biatec OIDC key and the approval key of the first device keep their 1.0 values; the rest are added
let keys = {};
const keysPath = join(dir, "keys.json");
if (existsSync(keysPath)) keys = JSON.parse(readFileSync(keysPath, "utf8"));
keys._warning = "TEST-ONLY keys for reproducing the vectors. Never use them anywhere else.";
const want = { oidc: "oidc-test-key-1", device: "device-test-key-1", presence: "presence-test-key-1", new_approval: "new-approval-test-key-1", new_presence: "new-presence-test-key-1" };
for (const [name, kid] of Object.entries(want)) keys[name] ??= newKey(kid);
write("keys.json", keys);

if (existsSync(join(dir, "receipts.json")) && !process.argv.includes("--force")) {
  console.error("vectors/receipts.json exists; ECDSA signatures are randomized so regenerating rewrites every vector. Pass --force to overwrite.");
  process.exit(1);
}
const jwks = { keys: [publicOf(keys.oidc)] };
write("jwks.json", jwks);

// ---- helpers for ids and addresses
const b32 = (buf) => {
  const A = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  let bits = "", out = "";
  for (const b of buf) bits += b.toString(2).padStart(8, "0");
  bits = bits.padEnd(Math.ceil(bits.length / 5) * 5, "0");
  for (let i = 0; i < bits.length; i += 5) out += A[parseInt(bits.slice(i, i + 5), 2)];
  return out;
};
const digest = (s) => createHash("sha256").update(s).digest();
const groupHash = (s) => b32(digest("group:" + s)); // 32 bytes -> 52 chars, same shape as an Algorand grp
const address = (s) => b32(Buffer.concat([digest("addr:" + s), digest("addr2:" + s).subarray(0, 4)])).slice(0, 58); // illustrative, not checksummed
const fingerprintOf = (clientId) => digest(clientId).toString("hex").slice(0, 16);

const ISS = "https://oidc.biatec.test";
const NOW = 1792425600; // 2026-10-19T16:00:00Z
const SUB = "pw_7c1f0a52e9b34d68"; // pairwise subject of the requesting client
const ENROLL_SUB = "pw_a9e3c0417b5d2f86"; // pairwise subject of the authenticator client (the device's enrollment subject)
const ORIGIN = "https://app.biatec.io";

/** A device block with a fresh attestation minted by Biatec OIDC for (sub, key, use). */
function deviceBlock(key, use, sub) {
  const pub = pubJwk(key);
  const attestation = signCompactJws(
    keys.oidc,
    { alg: "ES256", kid: keys.oidc.kid, typ: ATTESTATION_TYP },
    { iss: ISS, sub, device_kid: key.kid, device_jwk_thumbprint: thumbprint(pub), biatec_key_use: use, iat: NOW - 86400 * 3, exp: NOW + 86400 * 365 },
  );
  return { kid: key.kid, key_use: use, public_key: pub, attestation };
}
const KEY_FOR_USE = { approve: keys.device, presence: keys.presence };

// ---- authorization details (protocol 1.1)
const usdc = { id: "31566704", symbol: "USDC", decimals: 6 };
const algo = { id: "0", symbol: "ALGO", decimals: 6 };
const eur = (amount) => ({ amount, currency: "EUR" });
const tx = (n, { transfers, applications = [], total = null, origin = ORIGIN }) => ({
  type: "biatec_transaction",
  network: "algorand-mainnet",
  group_hash: groupHash(n),
  sender: address("user"),
  transfers: transfers.map((t) => ({ receiver: address("receiver-" + t.to), asset: t.asset, amount: t.amount, ...(t.eur ? { fiat_value: eur(t.eur) } : {}) })),
  applications,
  value_known: total !== null,
  ...(total !== null ? { fiat_value: eur(total) } : {}),
  origin,
});
const single = (n, extra = {}) => tx(n, { transfers: [{ to: n, asset: usdc, amount: "5000.000000", eur: "4612.50" }], total: "4612.50", ...extra });

const limitsBody = { per_day_eur: "5000", per_transaction_eur: "2000" };
const limits = {
  type: "biatec_limits_change",
  current_limits: { per_transaction_eur: "1000", per_day_eur: "3000" },
  requested_limits: { per_transaction_eur: "2000", per_day_eur: "5000" },
  request_hash: sha256Hex(jcs(limitsBody)),
};

// ---- clients: Biatec OIDC vouches for none; a client is its fingerprint and, when proven, its domain
const mkClient = (clientId, domain, linked) => ({
  client_id: clientId,
  request: { fingerprint: fingerprintOf(clientId), domain_proven: Boolean(domain), ...(domain ? { domain } : {}), linked },
});
const chat = mkClient("biatec-chat-tenant", "chat.biatec.io", true);
const bank = mkClient("bank-example-client", "bank.example", true);
const impostor = mkClient("client-3f9a1c", null, false);

const cases = [
  { name: "stepup-transaction-approve", entry: "step-up", client: chat, details: single("a"), code: "483920", decision: "approve", message: "", note: "Biatec Chat step-up receipt; binding_message is the empty string." },
  { name: "direct-transaction-approve-domain-proven", entry: "direct", client: bank, details: single("b"), code: "120934", decision: "approve", message: "Invoice 2026-0042", note: "Direct entry of a client with a proven domain; the client text is rendered and signed." },
  { name: "direct-transaction-approve-domain-unproven", entry: "direct", client: impostor, details: single("c"), code: "775201", decision: "approve", message: "", rawClientMessage: "Example Bank refund", note: "A client without a proven domain: the raw text is never rendered, so the signed binding_message is empty." },
  { name: "stepup-limits-change-approve", entry: "step-up", client: chat, details: limits, code: "905512", decision: "approve", message: "", note: "biatec_limits_change; request_hash is SHA-256 of JCS(limitsRequestBody).", limitsRequestBody: limitsBody },
  { name: "direct-transaction-deny", entry: "direct", client: bank, details: single("d"), code: "", decision: "deny", message: "Invoice 2026-0043", note: "Explicit Deny before any code was typed, signed by the approval key." },
  { name: "stepup-transaction-code-failed", entry: "step-up", client: chat, details: single("e"), code: "111111", decision: "code_failed", message: "", note: "Third wrong code; does not count toward rule 10's denial counter." },
  {
    name: "stepup-two-transfers-approve", entry: "step-up", client: chat, code: "246810", decision: "approve", message: "", note: "A group with two outgoing transfers: the card shows both and the total.",
    details: tx("f", { transfers: [{ to: "f1", asset: usdc, amount: "100.000000", eur: "92.25" }, { to: "f2", asset: algo, amount: "50.500000", eur: "8.10" }], total: "100.35" }),
  },
  {
    name: "stepup-transfer-plus-trusted-application-approve", entry: "step-up", client: chat, code: "135790", decision: "approve", message: "", note: "A transfer plus an application the user already trusted for this client: the value is the transfers' value.",
    details: tx("g", { transfers: [{ to: "g", asset: usdc, amount: "250.000000", eur: "230.60" }], applications: [{ id: 1234567, trusted: true }], total: "230.60" }),
  },
  {
    name: "stepup-untrusted-application-approve-remember", entry: "step-up", client: chat, code: "864209", decision: "approve", message: "", note: "An application the user has not trusted makes the value unknown; approving with `remember` trusts it for this client from now on.",
    details: tx("h", { transfers: [{ to: "h", asset: usdc, amount: "75.000000", eur: "69.20" }], applications: [{ id: 7654321, trusted: false }], total: null }),
    remember: { applications: [7654321] },
  },
  {
    name: "stepup-value-unknown-approve", entry: "step-up", client: chat, code: "314159", decision: "approve", message: "", note: "An asset with no market price: the value is unknown, there is no fiat_value and the card says \"Value unknown\".",
    details: tx("i", { transfers: [{ to: "i", asset: { id: "99999999", symbol: "ASA 99999999", decimals: 2 }, amount: "10.00" }], total: null }),
  },
  { name: "direct-transaction-deny-presence-key", entry: "direct", client: bank, details: single("j"), code: "", decision: "deny", message: "", note: "A deny needs no biometric: it may be signed by the presence key, whose attestation says so.", key: "presence" },
  {
    name: "stepup-approve-signed-by-presence-key-REFUSED", entry: "step-up", client: chat, details: single("k"), code: "271828", decision: "approve", message: "", key: "presence",
    note: "NEGATIVE: the signature and the attestation are valid, but an approve receipt must carry an attestation with biatec_key_use \"approve\". Verifiers MUST refuse it.",
    expected: { accepted: false, reason: "approve_requires_approval_key" },
  },
];

const receipts = cases.map((c, i) => {
  const authReqId = "ciba_" + digest("req:" + c.name).toString("hex").slice(0, 32);
  const expiry = NOW + 300;
  const use = c.key ?? "approve";
  const signed = {
    authorization_details: [c.details],
    auth_req_id: authReqId,
    client_fingerprint: c.client.request.fingerprint,
    binding_code: c.code,
    decision: c.decision,
    expiry,
    sub: SUB,
    binding_message: c.message,
    ...(c.remember ? { remember: c.remember } : {}),
  };
  const canonical = jcs(signed);
  const receipt = {
    version: "1.1",
    signed,
    alg: "ES256",
    signature: signEs256(KEY_FOR_USE[use], Buffer.from(canonical, "utf8")),
    issued_at: NOW + 20 + i,
    device: deviceBlock(KEY_FOR_USE[use], use, SUB),
  };
  const request = {
    auth_req_id: authReqId,
    entry: c.entry,
    authorization_details: [c.details],
    client: c.client.request,
    binding_message: c.message,
    requested_expiry: 300,
    expiry,
    bound_hash: c.details.group_hash ?? c.details.request_hash,
    origin: ORIGIN,
    sub: SUB,
  };
  const expected = c.expected ?? { accepted: true };
  const O = { now: NOW + 60, issuer: ISS };
  if (verifyReceipt(receipt, jwks, O) !== expected.accepted) throw new Error("self-check failed for " + c.name);
  if (expected.accepted && c.decision === "approve" && !verifyApproval(receipt, jwks, request, c.code, new Set(), O)) throw new Error("approval self-check failed for " + c.name);
  const v = { name: c.name, description: c.note, client_id: c.client.client_id, expected, request, receipt, canonical, canonical_sha256: sha256Hex(canonical) };
  if (c.limitsRequestBody) v.limits_request_body = c.limitsRequestBody;
  if (c.rawClientMessage) v.unsigned_client_binding_message = c.rawClientMessage;
  return v;
});
write("receipts.json", { _comment: "Protocol 1.1 signed receipt vectors. Verify with jwks.json. Regenerate with scripts/generate-vectors.mjs. receipts-1.0.json is the frozen 1.0 set.", vectors: receipts });

// ---- device-signed statements: settings changes, approval of a new device, enrollment proof, pairing code
const approver = keys.device; // the approval key of the active device
const wrap = (signedObj, key, use, sub) => {
  const canonical = jcs(signedObj);
  return {
    envelope: { signed: signedObj, alg: "ES256", signature: signEs256(key, Buffer.from(canonical, "utf8")), device: deviceBlock(key, use, sub) },
    canonical,
    canonical_sha256: sha256Hex(canonical),
  };
};
const settingsBase = (extra) => ({ type: "biatec_settings_change", sub: ENROLL_SUB, kid: approver.kid, nonce: "n-" + digest(JSON.stringify(extra)).toString("hex").slice(0, 30), issued_at: NOW + 30, change: extra });
const OPTS = { now: NOW + 60, issuer: ISS, usedNonces: new Set() };

const settings = [
  {
    name: "settings-raise-threshold-and-trust-application",
    description: "Loosening: the threshold is raised to 5000 EUR and an application is trusted for one service. Signed by the approval key of an active device.",
    ...wrap(settingsBase({ threshold_eur: "5000", clients: { [bank.client_id]: { trusted_applications_add: [{ network: "algorand-mainnet", app_id: 1234567 }] } } }), approver, "approve", ENROLL_SUB),
    expected: { accepted: true },
  },
  {
    name: "settings-lift-pause-set-label-clear-client-threshold",
    description: "Loosening: a pause is lifted for one origin, the user's own label is set and the per-client threshold is cleared (null).",
    ...wrap(settingsBase({ clients: { [bank.client_id]: { paused: false, origin: "https://bank.example", label: "My bank", per_client_threshold_eur: null } } }), approver, "approve", ENROLL_SUB),
    expected: { accepted: true },
  },
  {
    name: "settings-signed-by-presence-key-REFUSED",
    description: "NEGATIVE: a settings change signed by the presence key. The attestation says presence, so verifiers MUST refuse it.",
    ...wrap(settingsBase({ block_unproven_clients: false }), keys.presence, "presence", ENROLL_SUB),
    expected: { accepted: false, reason: "requires_approval_key" },
  },
];
for (const s of settings) if (verifyDeviceSigned(s.envelope, jwks, OPTS) !== s.expected.accepted) throw new Error("self-check failed for " + s.name);

const newApproval = pubJwk(keys.new_approval);
const newPresence = pubJwk(keys.new_presence);
const enrollNonce = "enroll-nonce-" + digest("enroll").toString("hex").slice(0, 24);
const pairing = {
  new_approval_jwk_thumbprint: thumbprint(newApproval),
  new_presence_jwk_thumbprint: thumbprint(newPresence),
  nonce: enrollNonce,
};
pairing.input = `${pairing.new_approval_jwk_thumbprint}.${pairing.new_presence_jwk_thumbprint}.${pairing.nonce}`;
const pairingDigest = digest(pairing.input);
pairing.sha256_hex = pairingDigest.toString("hex");
pairing.first_four_bytes_uint32 = pairingDigest.readUInt32BE(0);
pairing.code = pairingCode(pairing.new_approval_jwk_thumbprint, pairing.new_presence_jwk_thumbprint, pairing.nonce);
if (pairing.code !== String(pairing.first_four_bytes_uint32 % 1_000_000).padStart(6, "0")) throw new Error("pairing code derivation");

const approvalBase = (over = {}) => ({
  type: "biatec_device_approval",
  approver_kid: approver.kid,
  new_kid: keys.new_approval.kid,
  new_approval_jwk_thumbprint: pairing.new_approval_jwk_thumbprint,
  new_presence_jwk_thumbprint: pairing.new_presence_jwk_thumbprint,
  pairing_code: pairing.code,
  nonce: pairing.nonce,
  issued_at: NOW + 40,
  ...over,
});
const approvals = [
  { name: "device-approval-valid", description: "An active device's approval key activates the new device after the user compared the pairing code on both phones.", ...wrap(approvalBase(), approver, "approve", ENROLL_SUB), expected: { accepted: true } },
  { name: "device-approval-wrong-pairing-code-REFUSED", description: "NEGATIVE: the signature is valid but the pairing code is not the one derived from the new device's keys and nonce.", ...wrap(approvalBase({ pairing_code: pairing.code === "000000" ? "000001" : "000000" }), approver, "approve", ENROLL_SUB), expected: { accepted: false, reason: "pairing_code_mismatch" } },
  { name: "device-approval-signed-by-presence-key-REFUSED", description: "NEGATIVE: signed by the presence key of the approver, whose attestation says presence.", ...wrap(approvalBase({ approver_kid: keys.presence.kid }), keys.presence, "presence", ENROLL_SUB), expected: { accepted: false, reason: "requires_approval_key" } },
];
const expectedApproval = { new_kid: keys.new_approval.kid, new_approval_jwk_thumbprint: pairing.new_approval_jwk_thumbprint, new_presence_jwk_thumbprint: pairing.new_presence_jwk_thumbprint, nonce: pairing.nonce };
for (const a of approvals) if (verifyDeviceSigned(a.envelope, jwks, OPTS, { approval: expectedApproval, sub: ENROLL_SUB, approverActive: true }) !== a.expected.accepted) throw new Error("self-check failed for " + a.name);

const proofHeader = { alg: "ES256", typ: ENROLL_PROOF_TYP, jwk: newApproval };
const proofPayload = { kid: keys.new_approval.kid, presence_kid: keys.new_presence.kid, presence_jwk_thumbprint: pairing.new_presence_jwk_thumbprint, nonce: enrollNonce, iat: NOW + 10 };
const enrollmentProofs = [
  { name: "enrollment-proof-valid", description: "Signed by the new approval key; names the presence key by thumbprint and carries the server's nonce.", jws: signCompactJws(keys.new_approval, proofHeader, proofPayload), header: proofHeader, payload: proofPayload, presence_public_key: newPresence, expected: { accepted: true } },
  {
    name: "enrollment-proof-signed-by-presence-key-REFUSED",
    description: "NEGATIVE: signed by the presence key although the header names the approval key: the signature does not verify under the header's jwk.",
    jws: signCompactJws(keys.new_presence, proofHeader, proofPayload), header: proofHeader, payload: proofPayload, presence_public_key: newPresence, expected: { accepted: false, reason: "signature" },
  },
];

write("device-signed.json", {
  _comment: "Settings changes, device approvals and enrollment proofs of protocol 1.1, with the pairing code worked example. Regenerate with scripts/generate-vectors.mjs.",
  now: NOW + 60,
  issuer: ISS,
  enrollment_sub: ENROLL_SUB,
  settings_changes: settings,
  device_approvals: approvals.map((a) => ({ ...a, expected_inputs: expectedApproval })),
  pairing_code: pairing,
  enrollment_proofs: enrollmentProofs,
});

const canon = [
  { name: "sorted-members-and-no-whitespace", input: { b: 2, a: 1, c: { z: true, y: null, x: [3, 2, 1] } } },
  { name: "utf16-code-unit-order", input: { "\u20ac": "euro", "\r": "cr", "\uFB33": "hebrew", "1": "one", "\u{1F600}": "emoji", "\u0080": "c1", "\u00f6": "o-umlaut" } },
  { name: "string-escapes", input: { s: "tab\t nl\n quote\" backslash\\ bell\u0007 slash/ del\u007f", e: "€😀" } },
  { name: "ecmascript-numbers", input: [333333333.33333329, 1e30, 4.5, 0.002, 1e-9, 0, -0.0, 123456789012, 1e21, 1e-7] },
  { name: "decimal-strings-stay-strings", input: { amount: "5000.000000", fiat: "4612.50" } },
  { name: "empty-containers", input: { a: {}, b: [], c: "" } },
].map((c) => ({ ...c, canonical: jcs(c.input) }));
if (!existsSync(join(dir, "canonicalization.json"))) write("canonicalization.json", { _comment: "RFC 8785 vectors. The backend Biatec.Chat.Core.Canonical.JsonCanonicalizer must produce identical output.", vectors: canon });
console.log("wrote", receipts.length, "receipt vectors,", settings.length + approvals.length + enrollmentProofs.length, "device-signed vectors and", canon.length, "canonicalization vectors");
