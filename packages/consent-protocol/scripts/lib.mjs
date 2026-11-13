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
 * Verifies the attestation JWS with the Biatec OIDC JWKS and that it binds this receipt's sub and device key
 * (see attestationClaims for the checks on the JWS itself). For a 1.1 receipt it also requires a valid attested
 * `biatec_key_use`, and the informational `device.key_use` must agree with it. Returns false (never throws) on malformed input.
 */
export function verifyAttestation(receipt, jwks, opts = {}) {
  try {
    const payload = boundClaims(receipt.device, receipt.signed.sub, jwks, opts);
    if (!payload) return false;
    if (receipt.version === "1.1") {
      if (!KEY_USES.includes(payload.biatec_key_use)) return false;
      if (receipt.device.key_use !== undefined && receipt.device.key_use !== payload.biatec_key_use) return false;
    }
    return true;
  } catch {
    return false;
  }
}

/** The attested `biatec_key_use` of a receipt's device key, or null when the attestation does not verify. */
export function attestedKeyUse(receipt, jwks, opts = {}) {
  try {
    const payload = boundClaims(receipt.device, receipt.signed.sub, jwks, opts);
    return payload && KEY_USES.includes(payload.biatec_key_use) ? payload.biatec_key_use : null;
  } catch {
    return null;
  }
}

/**
 * Both signatures (rule 9) with the attestation checks above. Says nothing about decision or expiry. Never throws.
 * From version 1.1 on, an `approve` receipt MUST be signed by an approval key (attested `biatec_key_use` "approve");
 * `deny` and `code_failed` accept either key. Version 1 receipts predate the two keys and carry no key use.
 */
export function verifyReceipt(receipt, jwks, opts = {}) {
  try {
    if (!(verifyReceiptSignature(receipt) && verifyAttestation(receipt, jwks, opts))) return false;
    if (receipt.version === "1.1" && receipt.signed.decision === "approve") return attestedKeyUse(receipt, jwks, opts) === "approve";
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

/**
 * Verifies a settings change or the approval of a new device (spec section 8.2): the signature over JCS(signed) by
 * `device.public_key`, the attestation (binding the device key and, for a settings change, `signed.sub`), the attested
 * `biatec_key_use` = "approve", the signing key named inside the statement, and `issued_at` within 300 seconds of `opts.now`.
 * With `expect.approval` ({ new_approval_jwk_thumbprint, new_presence_jwk_thumbprint, nonce }) a device approval must
 * name exactly those keys and nonce and carry the pairing code derived from them. Never throws.
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
    const payload = boundClaims(envelope.device, settings ? s.sub : undefined, jwks, { ...opts, now });
    if (!payload || payload.biatec_key_use !== "approve" || envelope.device.key_use !== "approve") return false;
    if ((settings ? s.kid : s.approver_kid) !== envelope.device.kid) return false;
    if (!Number.isInteger(s.issued_at) || Math.abs(now - s.issued_at) > SIGNED_WINDOW_SECONDS) return false;
    if (approval && expect.approval) {
      const e = expect.approval;
      if (s.new_approval_jwk_thumbprint !== e.new_approval_jwk_thumbprint || s.new_presence_jwk_thumbprint !== e.new_presence_jwk_thumbprint || s.nonce !== e.nonce) return false;
      if (!timingSafeEqualStr(s.pairing_code, pairingCode(e.new_approval_jwk_thumbprint, e.new_presence_jwk_thumbprint, e.nonce))) return false;
    }
    return true;
  } catch {
    return false;
  }
}

/**
 * Verifies an enrollment proof JWS (spec section 8.2): typ, ES256 under the header's own `jwk`, `presence_jwk_thumbprint`
 * equals the thumbprint of `expect.presence_public_key`, `nonce` equals `expect.nonce`, and `iat` is within 300 seconds of
 * `expect.now`. Never throws.
 */
export function verifyEnrollmentProof(jws, expect = {}) {
  try {
    const now = expect.now ?? nowSeconds();
    const parts = jws.split(".");
    if (parts.length !== 3) return false;
    const header = JSON.parse(decodeB64u(parts[0]).toString("utf8"));
    const payload = JSON.parse(decodeB64u(parts[1]).toString("utf8"));
    if (header.alg !== "ES256" || header.typ !== ENROLL_PROOF_TYP || header.jwk?.kty !== "EC" || header.jwk?.crv !== "P-256") return false;
    if (!verifyEs256(header.jwk, Buffer.from(parts[0] + "." + parts[1], "ascii"), parts[2])) return false;
    if (typeof expect.nonce !== "string" || payload.nonce !== expect.nonce) return false;
    if (!expect.presence_public_key || payload.presence_jwk_thumbprint !== thumbprint(expect.presence_public_key)) return false;
    return Number.isInteger(payload.iat) && Math.abs(now - payload.iat) <= SIGNED_WINDOW_SECONDS;
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
    if (!verifyReceipt(receipt, jwks, { ...opts, now })) return false;
    const s = receipt.signed;
    const d = s.authorization_details?.[0];
    if (typeof expectedBindingCode !== "string" || !/^[0-9]{6}$/.test(expectedBindingCode)) return false;
    if (s.decision !== "approve" || !timingSafeEqualStr(s.binding_code, expectedBindingCode)) return false;
    if (!Number.isInteger(s.expiry) || s.expiry <= now) return false;
    if (
      typeof request.auth_req_id !== "string" ||
      typeof request.bound_hash !== "string" ||
      s.auth_req_id !== request.auth_req_id ||
      (receipt.version === "1.1" ? s.client_fingerprint !== request.client.fingerprint : s.client_entity_id !== request.client.entity_id) ||
      s.binding_message !== request.binding_message ||
      s.expiry !== request.expiry ||
      s.sub !== request.sub ||
      jcs(s.authorization_details) !== jcs(request.authorization_details) ||
      (d?.group_hash ?? d?.request_hash) !== request.bound_hash
    )
      return false;
    return !consumed.has(s.auth_req_id);
  } catch {
    return false;
  }
}

function timingSafeEqualStr(a, b) {
  const x = Buffer.from(String(a), "utf8");
  const y = Buffer.from(String(b), "utf8");
  return x.length === y.length && timingSafeEqual(x, y);
}
