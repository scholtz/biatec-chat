// Shared helpers: RFC 8785 (JCS) canonicalization, receipt signing and verification.
// Node's JSON.stringify already emits ECMAScript number and minimal string formatting, which is what RFC 8785 requires.
import { timingSafeEqual, createHash, createPublicKey, createPrivateKey, sign, verify } from "node:crypto";

export function jcs(value) {
  if (typeof value === "string") {
    if (!value.isWellFormed()) throw new Error("lone surrogate");
    return JSON.stringify(value);
  }
  if (value === null || typeof value === "boolean") return JSON.stringify(value);
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new Error("non-finite number");
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) return "[" + value.map(jcs).join(",") + "]";
  if (typeof value === "object") {
    const proto = Object.getPrototypeOf(value);
    if (proto !== Object.prototype && proto !== null) throw new Error("only plain objects, arrays and primitives can be canonicalized");
    // Array#sort on strings compares UTF-16 code units, as RFC 8785 requires.
    const keys = Object.keys(value).sort();
    return "{" + keys.map((k) => jcs(k) + ":" + jcs(value[k])).join(",") + "}";
  }
  throw new Error("unsupported value (undefined, function, symbol or bigint)");
}

export const b64u = (buf) => Buffer.from(buf).toString("base64url");
export const sha256Hex = (text) => createHash("sha256").update(text, "utf8").digest("hex");

/** RFC 7638 thumbprint of an EC P-256 public JWK. */
export function thumbprint(jwk) {
  return b64u(createHash("sha256").update(jcs({ crv: jwk.crv, kty: jwk.kty, x: jwk.x, y: jwk.y }), "utf8").digest());
}

const pub = (jwk) => createPublicKey({ key: { kty: jwk.kty, crv: jwk.crv, x: jwk.x, y: jwk.y }, format: "jwk" });

/** ES256 over UTF-8 bytes, IEEE P1363 output, base64url. */
export function signEs256(privateJwk, bytes) {
  return b64u(sign("sha256", bytes, { key: createPrivateKey({ key: privateJwk, format: "jwk" }), dsaEncoding: "ieee-p1363" }));
}

/** Strict unpadded base64url decode: rejects any non-canonical encoding (stray characters, non-zero trailing bits). */
export function decodeB64u(text) {
  if (typeof text !== "string" || !/^[A-Za-z0-9_-]*$/.test(text)) throw new Error("invalid base64url");
  const buf = Buffer.from(text, "base64url");
  if (buf.toString("base64url") !== text) throw new Error("non-canonical base64url");
  return buf;
}

export function verifyEs256(publicJwk, bytes, signatureB64u) {
  const sig = decodeB64u(signatureB64u);
  if (sig.length !== 64) return false;
  return verify("sha256", bytes, { key: pub(publicJwk), dsaEncoding: "ieee-p1363" }, sig);
}

/** Verifies the device signature over JCS(receipt.signed). Returns false (never throws) on malformed input. */
export function verifyReceiptSignature(receipt) {
  try {
    if (receipt.alg !== "ES256") return false;
    return verifyEs256(receipt.device.public_key, Buffer.from(jcs(receipt.signed), "utf8"), receipt.signature);
  } catch {
    return false;
  }
}

export function signCompactJws(privateJwk, header, payload) {
  const input = b64u(JSON.stringify(header)) + "." + b64u(JSON.stringify(payload));
  return input + "." + signEs256(privateJwk, Buffer.from(input, "ascii"));
}

export const ATTESTATION_TYP = "biatec-device-attestation+jwt";
export const ENROLL_PROOF_TYP = "biatec-device-enroll+jwt";
export const ENROLL_PRESENCE_PROOF_TYP = "biatec-device-enroll-presence+jwt";
export const KEY_USES = ["approve", "presence"];

const nowSeconds = () => Math.floor(Date.now() / 1000);

/**
 * Verifies the attestation JWS with the Biatec OIDC JWKS: the key must be an EC P-256 signature key, `typ` and `alg`
 * must be right, `iss` must equal `opts.issuer` (required) and `exp` must be in the future at `opts.now`.
 * Returns the payload, or null. Never throws. It says nothing yet about what the attestation is bound to.
 */
export function attestationClaims(attestation, jwks, opts = {}) {
  try {
    const now = opts.now ?? nowSeconds();
    if (typeof opts.issuer !== "string" || opts.issuer === "") return null;
    const parts = attestation.split(".");
    if (parts.length !== 3) return null;
    const [h, p, s] = parts;
    const header = JSON.parse(decodeB64u(h).toString("utf8"));
    const payload = JSON.parse(decodeB64u(p).toString("utf8"));
    if (typeof header.kid !== "string" || header.kid === "") return null;
    const key = jwks.keys.find((k) => k.kid === header.kid);
    if (!key || key.kty !== "EC" || key.crv !== "P-256" || (key.use !== undefined && key.use !== "sig")) return null;
    if (header.alg !== "ES256" || header.typ !== ATTESTATION_TYP) return null;
    if (!verifyEs256(key, Buffer.from(h + "." + p, "ascii"), s)) return null;
    if (payload.iss !== opts.issuer) return null;
    if (typeof payload.exp !== "number" || payload.exp <= now) return null;
    if (typeof payload.sub !== "string" || (payload.iat !== undefined && (!Number.isInteger(payload.iat) || payload.iat > now + 300))) return null; // not from the future
    return payload;
  } catch {
    return null;
  }
}

/** The attestation payload when it also binds this device key (kid and thumbprint) and, if `sub` is given, that subject. */
function boundClaims(device, sub, jwks, opts) {
  const payload = attestationClaims(device.attestation, jwks, opts);
  if (!payload) return null;
  if (sub !== undefined && payload.sub !== sub) return null;
  if (payload.device_kid !== device.kid || payload.device_jwk_thumbprint !== thumbprint(device.public_key)) return null;
  return payload;
}

/**
 * The verified attestation payload of a receipt, or null: the JWS (see attestationClaims), its binding to this receipt's sub
 * and device key, and the version rules. `version` is not signed, so nothing may depend on it alone: the shape of the signed
 * object has to agree with it (`client_fingerprint` for 1.1, the 1.0 client member for 1.0), an attested key use is honoured
 * whatever the version says, a 1.1 device must be attested for a key use and name the same one in `device.key_use`, and a
 * device that names a key use but is attested for none is refused. Never throws.
 */
function receiptClaims(receipt, jwks, opts) {
  try {
    const payload = boundClaims(receipt.device, receipt.signed.sub, jwks, opts);
    if (!payload) return null;
    const s = receipt.signed;
    const is11 = receipt.version === "1.1";
    if (!is11 && receipt.version !== "1") return null;
    if (is11 ? !("client_fingerprint" in s) || "client_entity_id" in s : !("client_entity_id" in s) || "client_fingerprint" in s) return null;
    if (payload.biatec_key_use !== undefined && !KEY_USES.includes(payload.biatec_key_use)) return null;
    if (is11 && payload.biatec_key_use === undefined) return null;
    if (receipt.device.key_use !== payload.biatec_key_use && (is11 || receipt.device.key_use !== undefined)) return null;
    return payload;
  } catch {
    return null;
  }
}

/** True when the attestation verifies and binds this receipt's sub and device key, with the version rules of receiptClaims. */
export function verifyAttestation(receipt, jwks, opts = {}) {
  return receiptClaims(receipt, jwks, opts) !== null;
}

/** The attested `biatec_key_use` of a receipt's device key, or null when the attestation does not verify or names none. */
export function attestedKeyUse(receipt, jwks, opts = {}) {
  const payload = receiptClaims(receipt, jwks, opts);
  return payload && KEY_USES.includes(payload.biatec_key_use) ? payload.biatec_key_use : null;
}

/**
 * Both signatures (rule 9) with the attestation checks above. Says nothing about decision or expiry. Never throws.
 * From version 1.1 on, an `approve` receipt MUST be signed by an approval key (attested `biatec_key_use` "approve");
 * `deny` and `code_failed` accept either key. Version 1 receipts predate the two keys and carry no key use; the
 * unsigned `version` can neither relabel a 1.1 receipt as 1.0 (the signed shape must agree, and an attested key use is
 * always honoured) nor the other way round.
 */
export function verifyReceipt(receipt, jwks, opts = {}) {
  try {
    if (!verifyReceiptSignature(receipt)) return false;
    const payload = receiptClaims(receipt, jwks, opts);
    if (!payload) return false;
    // An approve needs an approval key. A device that is attested for a key use (every 1.1 device is) can never approve with another one,
    // whatever `version` the receipt wears; only a 1.0 device, whose attestation carries no key use, approves without one.
    if (receipt.signed.decision === "approve" && payload.biatec_key_use !== undefined && payload.biatec_key_use !== "approve") return false;
    // `remember` trusts applications: only an approval of a 1.1 receipt may carry it, never a denial.
    if (receipt.signed.remember !== undefined && (receipt.signed.decision !== "approve" || receipt.version !== "1.1")) return false;
    return true;
  } catch {
    return false;
  }
}

/**
 * The six digit pairing code both phones show when a new device is enrolled: the first four bytes of
 * SHA-256(newApprovalThumbprint + "." + newPresenceThumbprint + "." + nonce) as a big-endian unsigned integer,
 * modulo 10^6, zero padded to six digits.
 */
export function pairingCode(newApprovalThumbprint, newPresenceThumbprint, nonce) {
  const d = createHash("sha256").update(`${newApprovalThumbprint}.${newPresenceThumbprint}.${nonce}`, "utf8").digest();
  return String(d.readUInt32BE(0) % 1_000_000).padStart(6, "0");
}

const SIGNED_WINDOW_SECONDS = 300;

/** Format, control, line and paragraph separators; white space other than a plain space; a run of spaces. */
const NOT_ALLOWED_SPACE_OR_FORMAT = /[\p{Cf}\p{Cc}\p{Co}\p{Cn}\p{Zl}\p{Zp}]|[^\S ]|  /u;
/** Invisible or filler characters outside the general categories above (soft hyphen, fillers, invisible operators, variation selectors, tags). */
const INVISIBLE = /[\u00AD\u034F\u115F\u1160\u17B4\u17B5\u180B-\u180E\u2061-\u2065\u206A-\u206F\u2800\u3164\uFE00-\uFE0F\uFFA0\uFFF9-\uFFFC]|[\u{1D173}-\u{1D17A}\u{E0000}-\u{E007F}\u{E0100}-\u{E01EF}]/u;

/**
 * Text a user types that is shown next to a security decision: Unicode NFC, at most `max` code points, no control characters,
 * no bidirectional controls (U+202A to U+202E, U+2066 to U+2069, U+200E, U+200F, U+061C), no zero-width characters (U+200B to
 * U+200D, U+2060, U+FEFF), no line breaks, no filler or invisible characters (soft hyphen, Hangul fillers, invisible operators,
 * variation selectors, tag characters), no padding and not blank. No bare or stacked combining marks (a mark must follow a base character, at most three in a row). A violation is refused, never stripped. Look-alike letters of another script are NOT detected: the label sits beside the proven domain or fingerprint, never instead of it.
 */
export function isDisplaySafe(text, max = 40) {
  if (typeof text !== "string" || text.length === 0 || !text.isWellFormed() || text !== text.normalize("NFC")) return false;
  if ([...text].length > max || text.trim() !== text || text.trim() === "") return false; // no padding, not blank
  if (NOT_ALLOWED_SPACE_OR_FORMAT.test(text) || /[\p{Zs}]/u.test(text.replaceAll(" ", ""))) return false;
  if (/^\p{M}|\p{M}{4}/u.test(text)) return false; // a mark needs a base character; stacks are limited
  return !INVISIBLE.test(text);
}

const MEMBERS = new Set(["always_ask", "per_client_threshold_eur", "label", "trusted_applications_add", "trusted_applications_remove", "paused", "origin", "unlink"]);

const AMOUNT = /^(0|[1-9][0-9]{0,14})(\.[0-9]{1,2})?$/;
/** A canonical origin: scheme, lower-case host of well-formed labels, optional port 1-65535 that is not the scheme's default. */
const ORIGIN = /^(?!https:\/\/[^/]*:443$)(?!http:\/\/[^/]*:80$)https?:\/\/[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*(:([1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5]))?$/;
const appKey = (a) => a.network + ":" + a.app_id;
const isApplicationList = (l) =>
  Array.isArray(l) && l.length >= 1 && l.length <= 16 && new Set(l.map((a) => a && appKey(a))).size === l.length && l.every((a) => a && typeof a === "object" && (a.network === "algorand-mainnet" || a.network === "algorand-testnet") && Number.isSafeInteger(a.app_id) && a.app_id >= 0 && Object.keys(a).length === 2);
const TOP = new Set(["threshold_eur", "block_unproven_clients", "biometric_required", "ask_on_test_networks", "clients"]);

/**
 * What the schema requires, which a relying party that uses only this file must not have to take on trust: a non-empty change of
 * known members of the right types (amounts are strings of at most two decimals, flags are booleans, `unlink` is `true`), then
 * no unknown member per client, no `unlink` mixed with anything else, a pause with its origin, and a display-safe label.
 */
function settingsChangeWellFormed(change) {
  if (change === null || typeof change !== "object" || Array.isArray(change) || Object.keys(change).length === 0) return false;
  if (Object.keys(change).some((k) => !TOP.has(k))) return false;
  if ("threshold_eur" in change && !(typeof change.threshold_eur === "string" && AMOUNT.test(change.threshold_eur))) return false;
  for (const k of ["block_unproven_clients", "biometric_required", "ask_on_test_networks"]) if (k in change && typeof change[k] !== "boolean") return false;
  if ("clients" in change && (change.clients === null || typeof change.clients !== "object" || Array.isArray(change.clients) || Object.keys(change.clients).length === 0 || Object.keys(change.clients).length > 50)) return false;
  if (Object.keys(change.clients ?? {}).some((id) => id.length === 0 || [...id].length > 200 || /[\u0000-\u001F\u007F-\u009F]/.test(id))) return false; // a client id is a bounded printable key
  for (const c of Object.values(change.clients ?? {})) {
    if (c === null || typeof c !== "object" || Object.keys(c).length === 0) return false;
    if (Object.keys(c).some((k) => !MEMBERS.has(k))) return false;
    if ("unlink" in c && Object.keys(c).length !== 1) return false;
    if ("paused" in c !== "origin" in c) return false;
    if ("paused" in c && typeof c.paused !== "boolean") return false;
    if ("origin" in c && !(typeof c.origin === "string" && c.origin.length <= 253 && ORIGIN.test(c.origin))) return false;
    for (const k of ["trusted_applications_add", "trusted_applications_remove"]) if (k in c && !isApplicationList(c[k])) return false;
    // Adding and removing the same application in one change is ambiguous (and its direction, loosening or tightening, with it).
    if (c.trusted_applications_add && c.trusted_applications_remove) {
      const removed = new Set(c.trusted_applications_remove.map(appKey));
      if (c.trusted_applications_add.some((a) => removed.has(appKey(a)))) return false;
    }
    if ("always_ask" in c && typeof c.always_ask !== "boolean") return false;
    if ("unlink" in c && c.unlink !== true) return false;
    if ("per_client_threshold_eur" in c && c.per_client_threshold_eur !== null && !(typeof c.per_client_threshold_eur === "string" && AMOUNT.test(c.per_client_threshold_eur))) return false;
    if ("label" in c && !isDisplaySafe(c.label)) return false;
  }
  return true;
}

/**
 * Verifies a settings change or the approval of a new device (spec section 8.2): the signature over JCS(signed) by
 * `device.public_key`, the attestation (binding the device key and, for a settings change, `signed.sub`), the attested
 * `biatec_key_use` = "approve", the signing key named inside the statement, and `issued_at` within 300 seconds of `opts.now`.
 * With `expect.approval` ({ new_kid, new_approval_jwk_thumbprint, new_presence_jwk_thumbprint, nonce }) a device approval must
 * name exactly that `new_kid`, those keys and that nonce and carry the pairing code derived from them. For a device approval both `expect.sub`
 * (the approver's attestation must be for that subject: the account of the device being enrolled) and `expect.approval` are REQUIRED;
 * for a settings change `expect.sub` is REQUIRED too and must equal the statement's `sub` (the account the caller is changing).
 * For every statement `expect.approverActive === true` (the caller looked up that the signing device is ACTIVE: a pending
 * device cannot loosen settings or approve another device) is REQUIRED; without it the function returns false, and an approver that is
 * one of the keys being enrolled is refused. For every statement `opts.usedNonces`
 * (a Set of nonces already accepted from this device) is REQUIRED and must not hold the statement's nonce: a statement is single
 * use and this function only reads the set. Never throws.
 */
export function verifyDeviceSigned(envelope, jwks, opts = {}, expect = {}) {
  try {
    const now = opts.now ?? nowSeconds();
    const s = envelope.signed;
    if (envelope.alg !== "ES256") return false;
    if (!verifyEs256(envelope.device.public_key, Buffer.from(jcs(s), "utf8"), envelope.signature)) return false;
    const settings = s.type === "biatec_settings_change";
    const approval = s.type === "biatec_device_approval";
    if (!settings && !approval) return false;
    // A settings change that cannot be read unambiguously, or whose label is not display-safe, is refused (spec section 8.2).
    if (settings && !settingsChangeWellFormed(s.change)) return false;
    // The signed object has exactly the members the schema allows: nothing rides along unsigned-looking but signed.
    const MEMBERS_OF = {
      biatec_settings_change: ["type", "sub", "kid", "nonce", "issued_at", "change"],
      biatec_device_approval: ["type", "approver_kid", "new_kid", "new_approval_jwk_thumbprint", "new_presence_jwk_thumbprint", "pairing_code", "nonce", "issued_at"],
    }[s.type];
    if (Object.keys(s).length !== MEMBERS_OF.length || !MEMBERS_OF.every((k) => k in s)) return false;
    // A settings change names the subject it is for; the attestation must be for that same subject.
    // ... and it is for the account the caller is changing: `expect.sub` is required and must equal it.
    if (settings && (typeof s.sub !== "string" || s.sub === "" || s.sub !== expect.sub)) return false;
    const payload = boundClaims(envelope.device, settings ? s.sub : approval ? expect.sub : undefined, jwks, { ...opts, now });
    if (!payload || payload.biatec_key_use !== "approve" || envelope.device.key_use !== "approve") return false;
    if ((settings ? s.kid : s.approver_kid) !== envelope.device.kid) return false;
    if (!Number.isInteger(s.issued_at) || Math.abs(now - s.issued_at) > SIGNED_WINDOW_SECONDS) return false;
    // A statement is single use: the nonce has the shape the schema requires and the caller's set of nonces already accepted from this
    // device (it keeps the set, as for receipts) does not hold it. Without the set the verifier cannot tell a replay and fails closed.
    if (typeof s.nonce !== "string" || s.nonce.length < 16 || s.nonce.length > 128) return false;
    if (!(opts.usedNonces instanceof Set) || opts.usedNonces.has(s.nonce)) return false;
    // A device approval carries no subject and names no enrollment on its own: the verifier MUST say whose approval it accepts, for
    // which keys and nonce, and that the approver is an ACTIVE device (the caller looked that up; an inactive device, including the
    // one being enrolled, cannot approve). Without all of it the verifier fails closed.
    if (expect.approverActive !== true) return false; // a pending device (the first 24 hours, or one just enrolled by an attacker) cannot loosen anything
    if (approval && (typeof expect.sub !== "string" || !expect.approval)) return false;
    if (approval) {
      // A device never approves itself: the approver is not one of the keys being enrolled.
      const self = thumbprint(envelope.device.public_key);
      if (self === s.new_approval_jwk_thumbprint || self === s.new_presence_jwk_thumbprint || s.approver_kid === s.new_kid) return false;
      if (s.new_approval_jwk_thumbprint === s.new_presence_jwk_thumbprint) return false; // two keys, not one used twice
      const e = expect.approval;
      if (typeof e.new_kid !== "string" || s.new_kid !== e.new_kid) return false; // the kid being activated is the kid the approver signed
      if (s.new_approval_jwk_thumbprint !== e.new_approval_jwk_thumbprint || s.new_presence_jwk_thumbprint !== e.new_presence_jwk_thumbprint || s.nonce !== e.nonce) return false;
      if (typeof s.pairing_code !== "string" || !timingSafeEqualStr(s.pairing_code, pairingCode(e.new_approval_jwk_thumbprint, e.new_presence_jwk_thumbprint, e.nonce))) return false;
    }
    return true;
  } catch {
    return false;
  }
}

/** An EC P-256 public JWK with its coordinates: a real point (Node refuses to import one that is not on the curve). */
function isP256PublicJwk(jwk) {
  try {
    if (!jwk || jwk.kty !== "EC" || jwk.crv !== "P-256" || typeof jwk.x !== "string" || typeof jwk.y !== "string") return false;
    pub(jwk);
    return true;
  } catch {
    return false;
  }
}

/**
 * Verifies an enrollment proof (spec section 8.3), a JWS signed by the approval key plus `expect.presence_proof`, a second JWS over the same
 * payload signed by the presence key (both are required: a key named only by its thumbprint has not proven possession). The first JWS: typ, ES256 under the header's own `jwk`, which MUST be the approval key
 * the request carries (`expect.approval_public_key`, with `expect.kid`, `expect.presence_kid` and `expect.usedNonces`, all required), `presence_jwk_thumbprint`
 * equals the thumbprint of `expect.presence_public_key`, `nonce` equals `expect.nonce`, and `iat` is within 300 seconds of
 * `expect.now`. Never throws.
 */
export function verifyEnrollmentProof(jws, expect = {}) {
  // The approval key the server received and the key that signed the proof must be the same: it is the proof of possession.
  if (!expect.approval_public_key) return false;
  // A proof is single use: the caller's set of enrollment nonces already consumed (the server consumes the nonce of its challenge once).
  if (!(expect.usedNonces instanceof Set)) return false;
  if (![expect.approval_public_key, expect.presence_public_key].every(isP256PublicJwk)) return false;
  try {
    const now = expect.now ?? nowSeconds();
    const parts = jws.split(".");
    if (parts.length !== 3) return false;
    const header = JSON.parse(decodeB64u(parts[0]).toString("utf8"));
    const payload = JSON.parse(decodeB64u(parts[1]).toString("utf8"));
    if (header.alg !== "ES256" || header.typ !== ENROLL_PROOF_TYP || header.jwk?.kty !== "EC" || header.jwk?.crv !== "P-256") return false;
    // Exactly the members the schema allows: no private `d` rides in the header jwk, no extra claim in the payload.
    const sameMembers = (o, keys) => Object.keys(o).length === keys.length && keys.every((k) => k in o);
    if (!sameMembers(header, ["alg", "typ", "jwk"]) || !sameMembers(header.jwk, ["kty", "crv", "x", "y"])) return false;
    if (!sameMembers(payload, ["kid", "presence_kid", "presence_jwk_thumbprint", "nonce", "iat"])) return false;
    if (!verifyEs256(header.jwk, Buffer.from(parts[0] + "." + parts[1], "ascii"), parts[2])) return false;
    if (!isP256PublicJwk(header.jwk) || thumbprint(header.jwk) !== thumbprint(expect.approval_public_key)) return false;
    // Two keys, not one used twice: the presence key is another key than the approval key.
    if (payload.presence_jwk_thumbprint === thumbprint(header.jwk) || payload.presence_kid === payload.kid) return false;
    // The key ids the device is registered under are the ones the proof names (the caller says which: both are required).
    if (typeof expect.kid !== "string" || expect.kid === "" || payload.kid !== expect.kid) return false;
    if (typeof expect.presence_kid !== "string" || expect.presence_kid === "" || payload.presence_kid !== expect.presence_kid) return false;
    if (typeof expect.nonce !== "string" || expect.nonce.length < 16 || expect.nonce.length > 128) return false; // the shape the schema requires
    if (payload.nonce !== expect.nonce || expect.usedNonces.has(payload.nonce)) return false;
    if (!expect.presence_public_key || payload.presence_jwk_thumbprint !== thumbprint(expect.presence_public_key)) return false;
    if (!Number.isInteger(payload.iat) || Math.abs(now - payload.iat) > SIGNED_WINDOW_SECONDS) return false;
    // Possession of the presence key: a second JWS over the SAME payload, signed by the presence key. Naming a key by its thumbprint
    // proves nothing (anyone's public key could be named); without this second signature the proof is refused.
    const pp = typeof expect.presence_proof === "string" ? expect.presence_proof.split(".") : [];
    if (pp.length !== 3 || pp[1] !== parts[1]) return false; // the same payload bytes
    const ph = JSON.parse(decodeB64u(pp[0]).toString("utf8"));
    if (ph.alg !== "ES256" || ph.typ !== ENROLL_PRESENCE_PROOF_TYP || !sameMembers(ph, ["alg", "typ", "jwk"]) || !sameMembers(ph.jwk ?? {}, ["kty", "crv", "x", "y"])) return false;
    if (!isP256PublicJwk(ph.jwk) || thumbprint(ph.jwk) !== thumbprint(expect.presence_public_key)) return false;
    return verifyEs256(ph.jwk, Buffer.from(pp[0] + "." + pp[1], "ascii"), pp[2]);
  } catch {
    return false;
  }
}

/**
 * Authorization check for one pending request. Fails closed and never throws.
 * - `request` is the stored consent request the caller is waiting on (never anything the receipt supplies).
 * - `expectedBindingCode` is the binding code Biatec OIDC issued for that request (number matching, rule 2).
 * - `consumed` is a Set of already spent `auth_req_id` values owned by the caller. This function only reads it:
 *   a consent is spent when Biatec OIDC actually signs (rule 5), so the caller adds the id after a successful sign.
 * - `opts.issuer` (required) is the expected Biatec OIDC issuer; `opts.now` defaults to the current time.
 * The receipt must match the request member for member, carry decision "approve" and not be expired.
 */
export function verifyApproval(receipt, jwks, request, expectedBindingCode, consumed, opts) {
  try {
    const now = opts.now ?? nowSeconds();
    // Biatec OIDC authorizes only receipts of devices it holds as ACTIVE (a pending device, such as one an attacker just enrolled, has a
    // valid attestation too). A caller that knows the device is not active passes `opts.deviceActive === false`; a device attested for a
    // key use (every 1.1 device) needs `true`, as checked below. The 1.0 flows, whose devices have no attested use, carry no such lookup.
    if (opts.deviceActive === false) return false;
    if (!verifyReceipt(receipt, jwks, { ...opts, now })) return false;
    // A device attested for a key use is a 1.1 device whatever `version` the receipt wears (a pending one has a valid attestation too):
    // it is authorized only with the caller's `deviceActive === true`. Only a 1.0 device, with no attested use, needs no lookup.
    if (receiptClaims(receipt, jwks, { ...opts, now })?.biatec_key_use !== undefined && opts.deviceActive !== true) return false;
    const s = receipt.signed;
    const d = s.authorization_details?.[0];
    if (typeof expectedBindingCode !== "string" || !/^[0-9]{6}$/.test(expectedBindingCode)) return false;
    if (s.decision !== "approve" || !timingSafeEqualStr(s.binding_code, expectedBindingCode)) return false;
    if (!Number.isInteger(s.expiry) || s.expiry <= now) return false;
    if (
      typeof request.auth_req_id !== "string" ||
      typeof request.bound_hash !== "string" ||
      s.auth_req_id !== request.auth_req_id ||
      !clientMatches(receipt, s, request) ||
      s.binding_message !== request.binding_message ||
      s.expiry !== request.expiry ||
      s.sub !== request.sub ||
      jcs(s.authorization_details) !== jcs(request.authorization_details) ||
      (d?.group_hash ?? d?.request_hash) !== request.bound_hash
    )
      return false;
    // The origin the server recorded is the origin the user saw and signed (a transaction of 1.1 carries it in its details).
    if (receipt.version === "1.1" && d?.type === "biatec_transaction" && (typeof d.origin !== "string" || d.origin !== request.origin || !ORIGIN.test(d.origin) || !Array.isArray(d.transfers))) return false;
    if (s.remember !== undefined) {
      // Each id MUST be an application of the request (section 8): the user can only trust what the card showed.
      // Only applications the card offered to trust (the untrusted ones): an already trusted one has nothing to remember.
      const all = d?.applications ?? [];
      if (new Set(all.map((a) => a.id)).size !== all.length) return false; // `remember` names bare ids: the same id twice would be ambiguous
      const shown = new Set(all.filter((a) => a.trusted === false).map((a) => a.id));
      const ids = s.remember.applications;
      if (!Array.isArray(ids) || ids.length === 0 || new Set(ids).size !== ids.length || !ids.every((id) => shown.has(id))) return false;
    }
    return !consumed.has(s.auth_req_id);
  } catch {
    return false;
  }
}

/**
 * The stored request decides which version applies, never the receipt: a request opened under 1.1 (its client has a
 * fingerprint and `domain_proven`) is answered by a 1.1 receipt naming that fingerprint, a 1.0 request by a 1.0 receipt.
 */
function clientMatches(receipt, s, request) {
  const c = request.client;
  if (c && typeof c.fingerprint === "string" && typeof c.domain_proven === "boolean")
    return receipt.version === "1.1" && typeof s.client_fingerprint === "string" && s.client_fingerprint === c.fingerprint;
  return receipt.version === "1" && typeof c?.entity_id === "string" && s.client_entity_id === c.entity_id;
}

function timingSafeEqualStr(a, b) {
  const x = Buffer.from(String(a), "utf8");
  const y = Buffer.from(String(b), "utf8");
  return x.length === y.length && timingSafeEqual(x, y);
}
