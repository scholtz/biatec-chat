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

const nowSeconds = () => Math.floor(Date.now() / 1000);

/**
 * Verifies the attestation JWS with the Biatec OIDC JWKS and that it binds this receipt's sub and device key.
 * Fails closed: the attestation must not be expired at `opts.now` (Unix seconds, default the current time),
 * its `iss` must equal `opts.issuer` (required), and the signing key must be
 * an EC P-256 signature key. Returns false (never throws) on malformed input.
 */
export function verifyAttestation(receipt, jwks, opts = {}) {
  try {
    const now = opts.now ?? nowSeconds();
    if (typeof opts.issuer !== "string" || opts.issuer === "") return false;
    const parts = receipt.device.attestation.split(".");
    if (parts.length !== 3) return false;
    const [h, p, s] = parts;
    const header = JSON.parse(decodeB64u(h).toString("utf8"));
    const payload = JSON.parse(decodeB64u(p).toString("utf8"));
    if (typeof header.kid !== "string" || header.kid === "") return false;
    const key = jwks.keys.find((k) => k.kid === header.kid);
    if (!key || key.kty !== "EC" || key.crv !== "P-256" || (key.use !== undefined && key.use !== "sig")) return false;
    if (header.alg !== "ES256" || header.typ !== ATTESTATION_TYP) return false;
    if (!verifyEs256(key, Buffer.from(h + "." + p, "ascii"), s)) return false;
    if (payload.iss !== opts.issuer) return false;
    if (typeof payload.exp !== "number" || payload.exp <= now) return false;
    return (
      payload.sub === receipt.signed.sub &&
      payload.device_kid === receipt.device.kid &&
      payload.device_jwk_thumbprint === thumbprint(receipt.device.public_key)
    );
  } catch {
    return false;
  }
}

/** Both signatures (rule 9) with the attestation checks above. Says nothing about decision or expiry. Never throws. */
export function verifyReceipt(receipt, jwks, opts = {}) {
  try {
    return verifyReceiptSignature(receipt) && verifyAttestation(receipt, jwks, opts);
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
      s.client_entity_id !== request.client.entity_id ||
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
