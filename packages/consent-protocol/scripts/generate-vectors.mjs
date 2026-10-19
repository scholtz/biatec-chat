// Regenerates vectors/keys.json (only if missing), vectors/receipts.json and vectors/canonicalization.json.
// ECDSA signatures are randomized, so vectors are committed; rerun only to extend them.
// All keys are TEST-ONLY and exist solely so the vectors can be reproduced. They protect nothing.
import { generateKeyPairSync, createHash } from "node:crypto";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { ATTESTATION_TYP, jcs, sha256Hex, signEs256, signCompactJws, thumbprint, verifyReceipt } from "./lib.mjs";

const dir = join(dirname(fileURLToPath(import.meta.url)), "..", "vectors");
const write = (name, v) => writeFileSync(join(dir, name), JSON.stringify(v, null, 2) + "\n");

const newKey = (kid) => {
  const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
  return { ...privateKey.export({ format: "jwk" }), kid, alg: "ES256", use: "sig" };
};
const publicOf = ({ d, ...pub }) => pub;

let keys;
const keysPath = join(dir, "keys.json");
if (existsSync(keysPath)) keys = JSON.parse(readFileSync(keysPath, "utf8"));
else {
  keys = {
    _warning: "TEST-ONLY keys for reproducing the vectors. Never use them anywhere else.",
    oidc: newKey("oidc-test-key-1"),
    device: newKey("device-test-key-1"),
  };
  write("keys.json", keys);
}
if (existsSync(join(dir, "receipts.json")) && !process.argv.includes("--force")) {
  console.error("vectors/receipts.json exists; ECDSA signatures are randomized so regenerating rewrites every vector. Pass --force to overwrite.");
  process.exit(1);
}
const jwks = { keys: [publicOf(keys.oidc)] };
write("jwks.json", jwks);

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

const SUB = "pw_7c1f0a52e9b34d68";
const NOW = 1792425600; // 2026-10-19T16:00:00Z
const deviceJwk = publicOf(keys.device);
const attestation = signCompactJws(
  keys.oidc,
  { alg: "ES256", kid: keys.oidc.kid, typ: ATTESTATION_TYP },
  { iss: "https://oidc.biatec.test", sub: SUB, device_kid: keys.device.kid, device_jwk_thumbprint: thumbprint(deviceJwk), iat: NOW - 86400 * 3, exp: NOW + 86400 * 365 },
);
const device = {
  kid: keys.device.kid,
  public_key: { kty: "EC", crv: "P-256", x: deviceJwk.x, y: deviceJwk.y },
  attestation,
};

const usdc = { id: "31566704", symbol: "USDC", decimals: 6 };
const tx = (n, extra) => ({
  type: "biatec_transaction",
  network: "algorand-mainnet",
  group_hash: groupHash(n),
  sender: address("user"),
  receiver: address("receiver-" + n),
  asset: usdc,
  amount: "5000.000000",
  fiat_value: { amount: "4612.50", currency: "EUR" },
  ...extra,
});
const limitsBody = { per_day_eur: "5000", per_transaction_eur: "2000" };
const limits = {
  type: "biatec_limits_change",
  current_limits: { per_transaction_eur: "1000", per_day_eur: "3000" },
  requested_limits: { per_transaction_eur: "2000", per_day_eur: "5000" },
  request_hash: sha256Hex(jcs(limitsBody)),
};

const biatecClient = { entity_id: "https://chat.biatec.io", verified: true, display_name: "Biatec Chat", domain: "chat.biatec.io" };
const bank = { entity_id: "https://bank.example", verified: true, display_name: "Example Bank", domain: "bank.example" };
const impostor = { entity_id: "client-3f9a1c", verified: false, fingerprint: "3F9A-1C77" };

const cases = [
  { name: "stepup-transaction-approve", entry: "step-up", client: biatecClient, details: tx("a"), code: "483920", decision: "approve", message: "", note: "Biatec Chat step-up receipt; binding_message is the empty string." },
  { name: "direct-transaction-approve-verified", entry: "direct", client: bank, details: tx("b"), code: "120934", decision: "approve", message: "Invoice 2026-0042", note: "Direct entry of a trust-anchored client; the client text is rendered and signed." },
  { name: "direct-transaction-approve-unverified", entry: "direct", client: impostor, details: tx("c"), code: "775201", decision: "approve", message: "", rawClientMessage: "Example Bank refund", note: "Unverified client: the raw text is never rendered, so the signed binding_message is empty." },
  { name: "stepup-limits-change-approve", entry: "step-up", client: biatecClient, details: limits, code: "905512", decision: "approve", message: "", note: "biatec_limits_change; request_hash is SHA-256 of JCS(limitsRequestBody)." , limitsRequestBody: limitsBody },
  { name: "direct-transaction-deny", entry: "direct", client: bank, details: tx("d"), code: "", decision: "deny", message: "Invoice 2026-0043", note: "Explicit Deny before any code was typed." },
  { name: "stepup-transaction-code-failed", entry: "step-up", client: biatecClient, details: tx("e"), code: "111111", decision: "code_failed", message: "", note: "Third wrong code; does not count toward rule 10's denial counter." },
];

const receipts = cases.map((c, i) => {
  const authReqId = "ciba_" + digest("req:" + c.name).toString("hex").slice(0, 32);
  const expiry = NOW + 300;
  const signed = {
    authorization_details: [c.details],
    auth_req_id: authReqId,
    client_entity_id: c.client.entity_id,
    binding_code: c.code,
    decision: c.decision,
    expiry,
    sub: SUB,
    binding_message: c.message,
  };
  const canonical = jcs(signed);
  const receipt = {
    version: "1",
    signed,
    alg: "ES256",
    signature: signEs256(keys.device, Buffer.from(canonical, "utf8")),
    issued_at: NOW + 20 + i,
    device,
  };
  const request = {
    auth_req_id: authReqId,
    entry: c.entry,
    authorization_details: [c.details],
    client: c.client,
    binding_message: c.message,
    requested_expiry: 300,
    expiry,
    bound_hash: c.details.group_hash ?? c.details.request_hash,
    origin: "https://app.biatec.io",
    sub: SUB,
  };
  if (!verifyReceipt(receipt, jwks)) throw new Error("self-check failed for " + c.name);
  const v = { name: c.name, description: c.note, request, receipt, canonical, canonical_sha256: sha256Hex(canonical) };
  if (c.limitsRequestBody) v.limits_request_body = c.limitsRequestBody;
  if (c.rawClientMessage) v.unsigned_client_binding_message = c.rawClientMessage;
  return v;
});
write("receipts.json", { _comment: "Signed receipt vectors. Verify with jwks.json. Regenerate with scripts/generate-vectors.mjs.", vectors: receipts });

const canon = [
  { name: "sorted-members-and-no-whitespace", input: { b: 2, a: 1, c: { z: true, y: null, x: [3, 2, 1] } } },
  { name: "utf16-code-unit-order", input: { "€": "euro", "\r": "cr", "דּ": "hebrew", "1": "one", "😀": "emoji", "\u0080": "c1", "ö": "o-umlaut" } },
  { name: "string-escapes", input: { s: "tab\t nl\n quote\" backslash\\ bell\u0007 slash/ del\u007f", e: "€😀" } },
  { name: "ecmascript-numbers", input: [333333333.33333329, 1e30, 4.5, 0.002, 1e-9, 0, -0.0, 123456789012, 1e21, 1e-7] },
  { name: "decimal-strings-stay-strings", input: { amount: "5000.000000", fiat: "4612.50" } },
  { name: "empty-containers", input: { a: {}, b: [], c: "" } },
].map((c) => ({ ...c, canonical: jcs(c.input) }));
write("canonicalization.json", { _comment: "RFC 8785 vectors. The backend Biatec.Chat.Core.Canonical.JsonCanonicalizer must produce identical output.", vectors: canon });
console.log("wrote", receipts.length, "receipt vectors and", canon.length, "canonicalization vectors");
