// Regenerates vectors/keys.json (adds any missing test key), vectors/receipts.json (protocol 1.1) and vectors/device-signed.json,
// and vectors/canonicalization.json only when it is missing (it has no signatures, so it never needs rewriting). vectors/receipts-1.0.json is the frozen protocol 1.0 set and is never regenerated.
// Protocol 1.2 (relying parties) adds vectors/receipts-1.2.json, vectors/link.json and vectors/text-rules.json. `--1.2` generates ONLY those three files and leaves every other vector (the frozen 1.0 and 1.1 sets included) untouched; it refuses when one of them exists unless --force is also given.
// ECDSA signatures are randomized, so vectors are committed; rerun only to extend them.
// All keys are TEST-ONLY and exist solely so the vectors can be reproduced. They protect nothing.
import { generateKeyPairSync, createHash } from "node:crypto";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { ATTESTATION_TYP, ENROLL_PRESENCE_PROOF_TYP, ENROLL_PROOF_TYP, b64u, detailsHash, isDisplaySafe, isRelyingPartyText, jcs, pairingCode, sha256Hex, signEs256, signCompactJws, thumbprint, verifyApproval, verifyDeviceSigned, verifyEnrollmentProof, verifyLink, verifyLinkPinned, verifyReceipt, verifyReceiptPinned } from "./lib.mjs";

const dir = join(dirname(fileURLToPath(import.meta.url)), "..", "vectors");
const only12 = process.argv.includes("--1.2");
const NEW_FILES = new Set(["receipts-1.2.json", "link.json", "text-rules.json"]);
// With --1.2 only the three new files are written; the rest of the script still runs in memory (it holds the helpers) and writes nothing.
const write = (name, v) => {
  if (only12 && !NEW_FILES.has(name)) return;
  writeFileSync(join(dir, name), JSON.stringify(v, null, 2) + "\n");
};

const newKey = (kid) => {
  const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
  return { ...privateKey.export({ format: "jwk" }), kid, alg: "ES256", use: "sig" };
};
const publicOf = ({ d, ...pub }) => pub;
const pubJwk = (k) => ({ kty: "EC", crv: "P-256", x: k.x, y: k.y });

// Refuse before anything is written: a run without --force must leave the working tree alone.
if (only12 && [...NEW_FILES].some((n) => existsSync(join(dir, n))) && !process.argv.includes("--force")) {
  console.error("A protocol 1.2 vector file exists; ECDSA signatures are randomized so regenerating rewrites every vector in it. Pass --force to overwrite.");
  process.exit(1);
}
if (!only12 && (existsSync(join(dir, "receipts.json")) || existsSync(join(dir, "device-signed.json"))) && !process.argv.includes("--force")) {
  console.error("vectors/receipts.json or vectors/device-signed.json exists; ECDSA signatures are randomized so regenerating rewrites every vector. Pass --force to overwrite.");
  process.exit(1);
}

// ---- keys: the Biatec OIDC key and the approval key of the first device keep their 1.0 values; the rest are added
let keys = {};
const keysPath = join(dir, "keys.json");
if (existsSync(keysPath)) keys = JSON.parse(readFileSync(keysPath, "utf8"));
keys._warning = "TEST-ONLY keys for reproducing the vectors. Never use them anywhere else.";
const want = { oidc: "oidc-test-key-1", device: "device-test-key-1", presence: "presence-test-key-1", new_approval: "new-approval-test-key-1", new_presence: "new-presence-test-key-1" };
for (const [name, kid] of Object.entries(want)) keys[name] ??= newKey(kid);
write("keys.json", keys);

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
  const O = { now: NOW + 60, issuer: ISS, deviceActive: true };
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
const settingsBase = (extra, kid = approver.kid) => ({ type: "biatec_settings_change", sub: ENROLL_SUB, kid, nonce: "n-" + digest(JSON.stringify(extra)).toString("hex").slice(0, 30), issued_at: NOW + 30, change: extra });
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
    ...wrap(settingsBase({ block_unproven_clients: false }, keys.presence.kid), keys.presence, "presence", ENROLL_SUB),
    expected: { accepted: false, reason: "requires_approval_key" },
  },
];
for (const s of settings) if (verifyDeviceSigned(s.envelope, jwks, OPTS, { approverActive: true, sub: ENROLL_SUB }) !== s.expected.accepted) throw new Error("self-check failed for " + s.name);

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
const presenceHeader = { alg: "ES256", typ: ENROLL_PRESENCE_PROOF_TYP, jwk: newPresence };
const enrollmentProofs = [
  { name: "enrollment-proof-valid", description: "Signed by the new approval key; names the presence key by thumbprint and carries the server's nonce.", jws: signCompactJws(keys.new_approval, proofHeader, proofPayload), presence_proof: signCompactJws(keys.new_presence, presenceHeader, proofPayload), header: proofHeader, payload: proofPayload, presence_public_key: newPresence, expected: { accepted: true } },
  {
    name: "enrollment-proof-signed-by-presence-key-REFUSED",
    description: "NEGATIVE: signed by the presence key although the header names the approval key: the signature does not verify under the header's jwk.",
    jws: signCompactJws(keys.new_presence, proofHeader, proofPayload), presence_proof: signCompactJws(keys.new_presence, presenceHeader, proofPayload), header: proofHeader, payload: proofPayload, presence_public_key: newPresence, expected: { accepted: false, reason: "signature" },
  },
];

for (const e of enrollmentProofs) {
  const verdict = verifyEnrollmentProof(e.jws, { approval_public_key: newApproval, kid: keys.new_approval.kid, presence_kid: keys.new_presence.kid, presence_public_key: newPresence, presence_proof: e.presence_proof, nonce: enrollNonce, usedNonces: new Set(), now: NOW + 15 });
  if (verdict !== e.expected.accepted) throw new Error("self-check failed for " + e.name);
}

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

// =====================================================================================================================
// Protocol 1.2: relying parties. vectors/receipts-1.2.json, vectors/link.json, vectors/text-rules.json
// =====================================================================================================================
const SUB_RP = "pw_5e2b9d0c71a84f36"; // pairwise subject of the relying party's client (the attestation is minted for it)
const RP_ORIGIN = "https://netbanking.bank.example";
const rpUnproven = mkClient("rp-unproven-3b7e", null, true); // a relying party without a proven domain; linked, or nothing reaches the phone
const O12 = { now: NOW + 60, issuer: ISS, deviceActive: true };
const pinnedDevices = [keys.device, keys.presence].map((k) => ({ kid: k.kid, key_use: k === keys.device ? "approve" : "presence", jwk: pubJwk(k) }));
const pins = pinnedDevices.map((p) => ({ key_use: p.key_use, jwk: p.jwk }));

// What the relying parties send (no origin: Biatec OIDC adds it when it has one)
const sentPayment = { type: "biatec_payment", instructed_amount: { amount: "1250.00", currency: "EUR" }, creditor_name: "Muster Energie GmbH", creditor_account: { iban: "DE89370400440532013000" }, remittance: "Invoice 2026-0042", execution_date: "2026-10-20" };
const sentPaymentOther = { type: "biatec_payment", instructed_amount: { amount: "89.90", currency: "GBP" }, creditor_name: "Example Supplies Ltd", creditor_account: { identification: "20000055779911", scheme: "SORTCODE" } };
const sentLogin = { type: "biatec_login", context: { ip: "203.0.113.42", location: "Bratislava, Slovakia", user_agent: "Firefox 133 on Windows 11" } };
const sentAction = { type: "biatec_action", title: "Add payee", lines: [{ label: "Payee", value: "Muster Energie GmbH" }, { label: "Account", value: "DE89 3704 0044 0532 0130 00" }, { label: "Daily limit", value: "5 000 EUR" }] };

const alterOneChar = (iban) => iban.slice(0, -1) + (iban.endsWith("0") ? "1" : "0");
const withOrigin = (sent, origin) => (origin ? { ...sent, origin } : { ...sent });

const cases12 = [
  { name: "payment-approve", client: bank, sent: sentPayment, origin: RP_ORIGIN, code: "620418", decision: "approve", note: "A bank asks for a payment to an IBAN. Approval key; the server added the origin it had." },
  { name: "payment-deny", client: bank, sent: sentPayment, origin: RP_ORIGIN, code: "", decision: "deny", note: "The user denied the same payment before typing a code." },
  { name: "payment-account-identification-approve", client: rpUnproven, sent: sentPaymentOther, origin: null, code: "384105", decision: "approve", note: "A relying party without a proven domain, an account that is not an IBAN, no remittance or date, and no origin (the server had none)." },
  { name: "login-approve", client: bank, sent: sentLogin, origin: RP_ORIGIN, code: "917263", decision: "approve", note: "Access to internet banking: the login context the party reported." },
  { name: "login-deny", client: bank, sent: sentLogin, origin: RP_ORIGIN, code: "", decision: "deny", key: "presence", note: "A deny needs no biometric: it may be signed by the presence key." },
  { name: "action-approve", client: bank, sent: sentAction, origin: null, code: "550193", decision: "approve", note: "Any other action: a title and label/value lines the app renders itself." },
  { name: "action-deny", client: bank, sent: sentAction, origin: null, code: "", decision: "deny", note: "The user denied the action." },
  {
    name: "payment-approve-signed-by-presence-key-REFUSED", client: bank, sent: sentPayment, origin: RP_ORIGIN, code: "208846", decision: "approve", key: "presence",
    note: "NEGATIVE: the signature and the attestation are valid, but a payment approval must be signed by an approval key. Verifiers MUST refuse it, in attestation mode (key use) and in pinned mode (the presence pin does not approve).",
    expected: { accepted: false, reason: "approve_requires_approval_key" },
  },
  {
    name: "payment-approve-iban-altered-REFUSED", client: bank, sent: sentPayment, origin: RP_ORIGIN, code: "731950", decision: "approve", alteredIban: true,
    note: "NEGATIVE: the device signed a payment whose IBAN differs by one character from the one the party sent, next to the details_hash of the ORIGINAL (a relay swapping the payee after the party computed the hash). Both signatures are valid; the hash computed from the signed details does not equal details_hash, and the altered IBAN fails its checksum as well.",
    expected: { accepted: false, reason: "details_hash_mismatch" },
  },
  {
    name: "payment-approve-new-device-unpinned", client: bank, sent: sentPayment, origin: RP_ORIGIN, code: "462790", decision: "approve", key: "new_device",
    note: "A second phone the user added after linking: its approval key is attested by Biatec OIDC but the relying party never pinned it. Attestation mode accepts; pinned mode refuses until the party decides (spec section 14.6).",
    expected: { accepted: true }, expected_pinned: { accepted: false, reason: "unpinned_key" },
  },
];
const KEY12 = { approve: [keys.device, "approve"], presence: [keys.presence, "presence"], new_device: [keys.new_approval, "approve"] };

const receipts12 = cases12.map((c) => {
  const [key, use] = KEY12[c.key ?? "approve"];
  const authReqId = "ciba_" + digest("req12:" + c.name).toString("hex").slice(0, 32);
  const expiry = NOW + 300;
  const details = withOrigin(c.sent, c.origin);
  const requestHash = detailsHash(details);
  const signedDetails = c.alteredIban ? { ...details, creditor_account: { iban: alterOneChar(details.creditor_account.iban) } } : details;
  const signed = {
    authorization_details: [signedDetails],
    auth_req_id: authReqId,
    client_fingerprint: c.client.request.fingerprint,
    binding_code: c.code,
    decision: c.decision,
    expiry,
    sub: SUB_RP,
    binding_message: "",
    details_hash: c.alteredIban ? requestHash : detailsHash(signedDetails),
  };
  const canonical = jcs(signed);
  const receipt = {
    version: "1.2",
    signed,
    alg: "ES256",
    signature: signEs256(key, Buffer.from(canonical, "utf8")),
    issued_at: NOW + 20,
    device: deviceBlock(key, use, SUB_RP),
  };
  const request = {
    auth_req_id: authReqId,
    entry: "direct",
    authorization_details: [details],
    client: c.client.request,
    binding_message: "",
    requested_expiry: 300,
    expiry,
    bound_hash: requestHash,
    ...(c.origin ? { origin: c.origin } : {}),
    sub: SUB_RP,
  };
  const expected = c.expected ?? { accepted: true };
  const expectedPinned = c.expected_pinned ?? expected;
  if (verifyReceipt(receipt, jwks, O12) !== expected.accepted) throw new Error("1.2 self-check (attested) failed for " + c.name);
  const pinnedOpts = { now: NOW + 60, origin: c.origin ?? null, clientFingerprint: c.client.request.fingerprint, sub: SUB_RP, auth_req_id: authReqId, ...(c.decision === "approve" ? { bindingCode: c.code } : {}) };
  if (verifyReceiptPinned(receipt, pins, c.sent, pinnedOpts) !== expectedPinned.accepted) throw new Error("1.2 self-check (pinned) failed for " + c.name);
  if (expected.accepted && c.decision === "approve" && !verifyApproval(receipt, jwks, request, c.code, new Set(), O12)) throw new Error("1.2 approval self-check failed for " + c.name);
  const v = {
    name: c.name,
    description: c.note,
    client_id: c.client.client_id,
    sent_details: c.sent,
    server_origin: c.origin ?? null,
    details_hash: requestHash,
    expected,
    request,
    receipt,
    canonical,
    canonical_sha256: sha256Hex(canonical),
  };
  if (c.expected_pinned) v.expected_pinned = c.expected_pinned;
  return v;
});
const ibanOriginal = sentPayment.creditor_account.iban;
write("receipts-1.2.json", {
  _comment: "Protocol 1.2 signed receipt vectors (payment, login, action). Verify with jwks.json (attestation mode) or with pinned_devices alone (pinned mode, spec section 14.5). `sent_details` is what the relying party sent, `server_origin` the origin Biatec OIDC added (null: none). Regenerate with scripts/generate-vectors.mjs --1.2.",
  now: NOW + 60,
  issuer: ISS,
  subject: SUB_RP,
  pinned_devices: pinnedDevices,
  iban_alteration: {
    description: "One character of the creditor IBAN changed: the details hash changes, and the altered IBAN no longer satisfies its mod-97 check digits.",
    original_iban: ibanOriginal,
    altered_iban: alterOneChar(ibanOriginal),
    original_details_hash: detailsHash(withOrigin(sentPayment, RP_ORIGIN)),
    altered_details_hash: detailsHash(withOrigin({ ...sentPayment, creditor_account: { iban: alterOneChar(ibanOriginal) } }, RP_ORIGIN)),
  },
  vectors: receipts12,
});

// ---- link statements
const linkClient = (c, label) => ({ link_id: null, client: { fingerprint: c.request.fingerprint, domain_proven: c.request.domain_proven, ...(c.request.domain ? { domain: c.request.domain } : {}) }, ...(label ? { user_label: label } : {}) });
const linkCases = [
  { name: "link-approve-domain-proven", client: bank, label: "Alex M. (account ending 4421)", decision: "approve", code: "620418", note: "The user links a bank that proved its domain. Approval key.", seed: "a" },
  { name: "link-approve-domain-unproven", client: rpUnproven, decision: "approve", code: "157302", note: "The user links a party without a proven domain: the signed `domain` is null, the user decided on the fingerprint.", seed: "b" },
  { name: "link-deny", client: bank, label: "Alex M. (account ending 4421)", decision: "deny", code: "", shown: "573082", key: "presence", note: "The user refused to link before typing the code; a deny may be signed by the presence key.", seed: "c" },
  {
    name: "link-wrong-domain-REFUSED", client: bank, decision: "approve", code: "846513", signedDomain: "bank-example.test", seed: "d",
    note: "NEGATIVE: the signed domain is not the one the server showed the device. Everything else is valid; verifiers MUST refuse it.",
    expected: { accepted: false, reason: "domain_mismatch" },
  },
  {
    name: "link-approve-signed-by-presence-key-REFUSED", client: bank, decision: "approve", code: "290417", key: "presence", seed: "e",
    note: "NEGATIVE: an approval of a link signed by the presence key. Verifiers MUST refuse it (a link is signed by the approval key).",
    expected: { accepted: false, reason: "approve_requires_approval_key" },
  },
  {
    name: "link-wrong-binding-code-REFUSED", client: bank, decision: "approve", code: "000000", shown: "718420", seed: "f",
    note: "NEGATIVE: the code the user typed is not the code the relying party showed.",
    expected: { accepted: false, reason: "binding_code_mismatch" },
  },
];
const linkVectors = linkCases.map((c) => {
  const [key, use] = KEY12[c.key ?? "approve"];
  const linkId = "lnk_" + digest("link:" + c.seed).toString("hex").slice(0, 24);
  const secret = b64u(digest("link-secret:" + c.seed));
  const nonce = "link-nonce-" + digest("link-nonce:" + c.seed).toString("hex").slice(0, 24);
  const showDomain = c.client.request.domain ?? null;
  const shownCode = c.shown ?? c.code;
  const signed = {
    type: "biatec_link",
    link_id: linkId,
    client_fingerprint: c.client.request.fingerprint,
    domain: c.signedDomain ?? showDomain,
    decision: c.decision,
    binding_code: c.code,
    nonce,
    issued_at: NOW + 30,
    kid: key.kid,
  };
  const canonical = jcs(signed);
  const envelope = { signed, alg: "ES256", signature: signEs256(key, Buffer.from(canonical, "utf8")), device: deviceBlock(key, use, SUB_RP) };
  const expectedInputs = { link_id: linkId, client_fingerprint: c.client.request.fingerprint, domain: showDomain, nonce, binding_code: shownCode, sub: SUB_RP };
  const expected = c.expected ?? { accepted: true };
  if (verifyLink(envelope, jwks, { now: NOW + 60, issuer: ISS, consumed: new Set() }, expectedInputs) !== expected.accepted) throw new Error("link self-check (attested) failed for " + c.name);
  const { sub, ...pinnedInputs } = expectedInputs;
  if (verifyLinkPinned({ signed, signature: envelope.signature }, pinnedDevices, pinnedInputs, { now: NOW + 60 }) !== expected.accepted) throw new Error("link self-check (pinned) failed for " + c.name);
  return {
    name: c.name,
    description: c.note,
    client_id: c.client.client_id,
    link_created: { link_id: linkId, link_uri: `biatec-authenticator://link?iss=${encodeURIComponent(ISS)}&id=${linkId}&s=${secret}`, binding_code: shownCode, expires_in: 300, interval: 2 },
    link_request: { ...linkClient(c.client, c.label), link_id: linkId, nonce, expires_at: NOW + 300 },
    expected,
    expected_inputs: expectedInputs,
    envelope,
    canonical,
    canonical_sha256: sha256Hex(canonical),
  };
});
write("link.json", {
  _comment: "Protocol 1.2 link statements (`biatec_link`) with the link request the device fetched and the link code the party showed. Attestation mode: verifyLink(envelope, jwks, ...). Pinned mode: verifyLinkPinned({ signed, signature }, pinned_devices, expected_inputs without sub). Regenerate with scripts/generate-vectors.mjs --1.2.",
  now: NOW + 60,
  issuer: ISS,
  subject: SUB_RP,
  pinned_devices: pinnedDevices,
  vectors: linkVectors,
});

// ---- text rules (spec section 15). Every verdict is declared here and checked against lib.mjs, never derived from it.
const cp = (...c) => String.fromCodePoint(...c);
const textVectors = [];
const T = (name, text, accepted, reason, opts = {}) => textVectors.push({ name, text, max: opts.max ?? 80, profile: opts.profile ?? "relying_party", accepted, reason, core: opts.core ?? false });
// accepted
T("ascii", "Muster Energie GmbH", true, "plain_text", { core: true });
T("nfc-accent", "Zo\u00eb M\u00fcller", true, "nfc_text", { core: true });
T("non-latin", "\u65e5\u672c\u8a9e\u306e\u652f\u6255\u5148", true, "non_latin_text", { core: true });
T("single-emoji", "Paid " + cp(0x1f600) + " ok", true, "emoji_is_one_code_point", { core: true });
T("exactly-at-limit", "a".repeat(70), true, "length_equals_max", { max: 70, core: true });
T("code-points-not-utf16-units", cp(0x1f600).repeat(70), true, "length_counts_code_points", { max: 70, core: true });
T("look-alike-script-not-detected", "p\u0430ypal", true, "look_alike_letters_are_not_detected", {});
T("label-persian-zwnj", "\u0645\u06cc\u200c\u062e\u0648\u0627\u0647\u0645", true, "label_allows_zwnj_in_context", { profile: "label", max: 40 });
T("label-zwj-emoji-sequence", cp(0x1f469, 0x200d, 0x1f4bb), true, "label_allows_zwj_in_context", { profile: "label", max: 40 });
// refused
T("empty", "", false, "empty", { core: true });
T("nfd-not-normalized", "Zoe\u0301", false, "not_nfc", { core: true });
T("one-over-the-limit", "a".repeat(71), false, "too_long", { max: 70, core: true });
T("emoji-one-over-the-limit", cp(0x1f600).repeat(71), false, "too_long", { max: 70, core: true });
for (const [n, c] of [["lre", 0x202a], ["rle", 0x202b], ["pdf", 0x202c], ["lro", 0x202d], ["rlo", 0x202e], ["lri", 0x2066], ["rli", 0x2067], ["fsi", 0x2068], ["pdi", 0x2069], ["lrm", 0x200e], ["rlm", 0x200f], ["alm", 0x061c]])
  T("bidi-" + n, "Pay" + cp(c) + "ment", false, "bidirectional_control", { core: true });
for (const [n, c] of [["zwsp", 0x200b], ["zwnj", 0x200c], ["zwj", 0x200d], ["word-joiner", 0x2060], ["bom", 0xfeff]])
  T("zero-width-" + n, "Pay" + cp(c) + "ment", false, "zero_width", { core: true });
for (const [n, c] of [["lf", 0x0a], ["cr", 0x0d], ["nel", 0x85], ["line-separator", 0x2028], ["paragraph-separator", 0x2029]])
  T("line-break-" + n, "Line one" + cp(c) + "line two", false, "line_break", { core: true });
T("line-break-crlf", "Line one\r\nline two", false, "line_break", { core: true });
for (const [n, c] of [["nul", 0x00], ["bell", 0x07], ["tab", 0x09], ["escape", 0x1b], ["del", 0x7f], ["c1", 0x9f]])
  T("control-" + n, "Pay" + cp(c) + "ment", false, "control_character", { core: true });
T("leading-space", " Muster", false, "padding");
T("trailing-space", "Muster ", false, "padding");
T("run-of-spaces", "Muster  Energie", false, "run_of_spaces");
T("blank", "   ", false, "blank");
T("no-break-space", "Muster" + cp(0xa0) + "Energie", false, "white_space_other_than_space");
T("soft-hyphen", "Mus" + cp(0xad) + "ter", false, "invisible_character");
T("hangul-filler", "Mus" + cp(0x3164) + "ter", false, "invisible_character");
T("tag-character", "Mus" + cp(0xe0041) + "ter", false, "invisible_character");
T("variation-selector", "Mus" + cp(0xfe0f) + "ter", false, "invisible_character");
T("invisible-times", "Mus" + cp(0x2062) + "ter", false, "invisible_character");
T("private-use", "Mus" + cp(0xe000) + "ter", false, "private_use");
T("bare-combining-mark", cp(0x0301) + "Muster", false, "mark_without_base");
T("stacked-combining-marks", "e" + cp(0x0301, 0x0302, 0x0303, 0x0304) + "x", false, "too_many_combining_marks");
T("relying-party-zwj-emoji-sequence", cp(0x1f469, 0x200d, 0x1f4bb), false, "relying_party_profile_refuses_joiners");
T("relying-party-persian-zwnj", "\u0645\u06cc\u200c\u062e\u0648\u0627\u0647\u0645", false, "relying_party_profile_refuses_joiners");
T("label-bidi-control", "My" + cp(0x202e) + "bank", false, "bidirectional_control", { profile: "label", max: 40 });
T("label-zwsp", "My" + cp(0x200b) + "bank", false, "zero_width", { profile: "label", max: 40 });
T("label-zwj-at-end", "bank" + cp(0x200d), false, "joiner_not_in_context", { profile: "label", max: 40 });
for (const t of textVectors) {
  const verdict = t.profile === "label" ? isDisplaySafe(t.text, t.max) : isRelyingPartyText(t.text, t.max);
  if (verdict !== t.accepted) throw new Error("text-rules self-check failed for " + t.name);
}
const escapeNonAscii = (json) => json.replace(/[^\x20-\x7E\n]/g, (ch) => "\\u" + ch.charCodeAt(0).toString(16).padStart(4, "0")); // one UTF-16 unit per escape: astral characters become surrogate pairs
if (!only12 || NEW_FILES.has("text-rules.json"))
  writeFileSync(join(dir, "text-rules.json"), escapeNonAscii(JSON.stringify({
    _comment: "Text rules of spec section 15. profile `relying_party` is isRelyingPartyText(text, max), `label` is isDisplaySafe(text, max). `max` is in code points. `core: true` marks the vectors that follow from the minimum rules every implementation applies (NFC, no control characters, no bidirectional controls, no zero-width characters, no line breaks, length in code points); a verifier with only those rules MUST agree on them. Non-ASCII text is written as \\u escapes. Regenerate with scripts/generate-vectors.mjs --1.2.",
    vectors: textVectors,
  }, null, 2)) + "\n");

const canon = [
  { name: "sorted-members-and-no-whitespace", input: { b: 2, a: 1, c: { z: true, y: null, x: [3, 2, 1] } } },
  { name: "utf16-code-unit-order", input: { "\u20ac": "euro", "\r": "cr", "\uFB33": "hebrew", "1": "one", "\u{1F600}": "emoji", "\u0080": "c1", "\u00f6": "o-umlaut" } },
  { name: "string-escapes", input: { s: "tab\t nl\n quote\" backslash\\ bell\u0007 slash/ del\u007f", e: "€😀" } },
  { name: "ecmascript-numbers", input: [333333333.33333329, 1e30, 4.5, 0.002, 1e-9, 0, -0.0, 123456789012, 1e21, 1e-7] },
  { name: "decimal-strings-stay-strings", input: { amount: "5000.000000", fiat: "4612.50" } },
  { name: "empty-containers", input: { a: {}, b: [], c: "" } },
].map((c) => ({ ...c, canonical: jcs(c.input) }));
if (!only12 && !existsSync(join(dir, "canonicalization.json"))) write("canonicalization.json", { _comment: "RFC 8785 vectors. The backend Biatec.Chat.Core.Canonical.JsonCanonicalizer must produce identical output.", vectors: canon });
console.log("wrote", receipts.length, "receipt vectors,", settings.length + approvals.length + enrollmentProofs.length, "device-signed vectors and", canon.length, "canonicalization vectors");
