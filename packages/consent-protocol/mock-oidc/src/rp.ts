// Relying parties (protocol 1.2, spec chapter 14): what the mock needs to register a `private_key_jwt` client and to authenticate it.
// Pure functions; the state (clients, used `jti` values, links) lives in app.ts.
import { createPublicKey, verify as cryptoVerify, constants } from "node:crypto";

export type PublicJwk = Record<string, string>;

/** A problem with the client's metadata or key set, answered as RFC 7591 `invalid_client_metadata`. */
export class MetadataError extends Error {}
/** A client assertion that is not acceptable (RFC 7523), answered as `401 invalid_client`. */
export class AssertionError extends Error {}

export const CLIENT_ASSERTION_TYPE = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
export const CIBA_GRANT = "urn:openid:params:grant-type:ciba";
/** The most a client assertion may live, and the most `iat` may be ahead of the clock (RFC 7523 with the mock's tolerance). */
export const ASSERTION_MAX_LIFETIME_SECONDS = 300;
export const ASSERTION_ALGORITHMS = ["ES256", "PS256"] as const;
const PRIVATE_MEMBERS = ["d", "p", "q", "dp", "dq", "qi", "oth", "k"];
const b64uBytes = (s: unknown) => (typeof s === "string" && /^[A-Za-z0-9_-]+$/.test(s) ? Buffer.from(s, "base64url") : null);

/** The public members the mock keeps of a validated key (`kid`, `use` and `alg` too, when given). */
function normalize(k: Record<string, unknown>): PublicJwk {
  const keep = k.kty === "EC" ? ["kty", "crv", "x", "y"] : ["kty", "n", "e"];
  const out: PublicJwk = {};
  for (const m of [...keep, "kid", "use", "alg"]) if (typeof k[m] === "string") out[m] = k[m] as string;
  return out;
}

/**
 * The `jwks` of a registration: at most 4 keys, EC P-256 or RSA of 2048 bits or more, no private member, `use` `sig` when given.
 * Throws a MetadataError with the reason; never accepts a key it cannot use for ES256 or PS256.
 */
export function validateJwks(jwks: unknown): PublicJwk[] {
  const keys = (jwks as { keys?: unknown } | null)?.keys;
  if (!jwks || typeof jwks !== "object" || Array.isArray(jwks) || !Array.isArray(keys) || keys.length < 1 || keys.length > 4) throw new MetadataError("jwks must be { keys: [...] } with 1 to 4 keys");
  const kids = new Set<string>();
  return keys.map((k: unknown) => {
    if (!k || typeof k !== "object" || Array.isArray(k)) throw new MetadataError("every key of jwks must be an object");
    const key = k as Record<string, unknown>;
    for (const m of PRIVATE_MEMBERS) if (m in key) throw new MetadataError(`jwks holds a private member (${m}): send public keys only`);
    if (key.use !== undefined && key.use !== "sig") throw new MetadataError("a key of jwks is for signatures: use is sig or absent");
    if (key.kid !== undefined && (typeof key.kid !== "string" || key.kid === "" || key.kid.length > 128 || kids.has(key.kid))) throw new MetadataError("kid is a unique string of at most 128 characters");
    if (typeof key.kid === "string") kids.add(key.kid);
    if (key.kty === "EC") {
      if (key.crv !== "P-256" || !b64uBytes(key.x) || !b64uBytes(key.y)) throw new MetadataError("an EC key is P-256 with x and y");
      if (key.alg !== undefined && key.alg !== "ES256") throw new MetadataError("an EC key is for ES256");
    } else if (key.kty === "RSA") {
      const n = b64uBytes(key.n);
      const e = b64uBytes(key.e);
      if (!n || !e) throw new MetadataError("an RSA key has n and e");
      const bits = n.length * 8 - (n.length > 0 ? Math.clz32(n[0]) - 24 : 0);
      if (bits < 2048) throw new MetadataError("an RSA key is 2048 bits or more");
      if (key.alg !== undefined && key.alg !== "PS256") throw new MetadataError("an RSA key is for PS256");
    } else throw new MetadataError("a key is EC P-256 or RSA (kty)");
    try {
      createPublicKey({ key: normalize(key) as never, format: "jwk" });
    } catch {
      throw new MetadataError("a key of jwks is not a valid public key");
    }
    return normalize(key);
  });
}

/** `client_uri`: an absolute https URL with a host name that is not an IP address, no credentials. Returns the lower-case host. */
export function clientUriHost(uri: unknown): string {
  if (typeof uri !== "string" || uri.length > 2048) throw new MetadataError("client_uri is required: an absolute https URL");
  let u: URL;
  try {
    u = new URL(uri);
  } catch {
    throw new MetadataError("client_uri is required: an absolute https URL");
  }
  const host = u.hostname;
  if (u.protocol !== "https:" || u.username || u.password || !host.includes(".") || /^[0-9.]+$/.test(host) || host.startsWith("[") || !/^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$/.test(host)) {
    throw new MetadataError("client_uri must be an https URL with a host name (not an IP address or a single label) and no credentials");
  }
  return host;
}

/**
 * Verifies a client assertion (RFC 7523) against the client's keys: `ES256` or `PS256` only, `iss` = `sub` = the client id, `aud` one of
 * `audiences`, `exp` in the future and at most 5 minutes ahead, a `jti`. Returns the `jti` and its expiry for the caller's single-use set.
 */
export function verifyAssertion(jwt: string, keys: PublicJwk[], clientId: string, audiences: string[], now: number): { jti: string; exp: number } {
  const bad = (why: string): never => {
    throw new AssertionError("client assertion: " + why);
  };
  const parts = jwt.split(".");
  if (parts.length !== 3 || !parts.every((p) => /^[A-Za-z0-9_-]+$/.test(p) || p === "")) bad("not a compact JWS");
  let header: any, payload: any;
  try {
    header = JSON.parse(Buffer.from(parts[0], "base64url").toString("utf8"));
    payload = JSON.parse(Buffer.from(parts[1], "base64url").toString("utf8"));
  } catch {
    return bad("not JSON");
  }
  if (!header || typeof header !== "object" || !payload || typeof payload !== "object") bad("not JSON objects");
  if (!(ASSERTION_ALGORITHMS as readonly string[]).includes(header.alg)) bad("alg must be ES256 or PS256");
  const kty = header.alg === "ES256" ? "EC" : "RSA";
  const candidates = keys.filter((k) => k.kty === kty && (header.kid === undefined ? true : k.kid === header.kid));
  if (header.kid === undefined && keys.filter((k) => k.kty === kty).length > 1) bad("kid is required when the client has more than one key");
  if (candidates.length === 0) bad("no key of the client matches");
  const data = Buffer.from(parts[0] + "." + parts[1], "ascii");
  const sig = Buffer.from(parts[2], "base64url");
  const ok = candidates.some((k) => {
    try {
      const key = createPublicKey({ key: normalize(k) as never, format: "jwk" });
      return header.alg === "ES256" ? sig.length === 64 && cryptoVerify("sha256", data, { key, dsaEncoding: "ieee-p1363" }, sig) : cryptoVerify("sha256", data, { key, padding: constants.RSA_PKCS1_PSS_PADDING, saltLength: 32 }, sig);
    } catch {
      return false;
    }
  });
  if (!ok) bad("the signature does not verify with a key of the client");
  if (payload.iss !== clientId || payload.sub !== clientId) bad("iss and sub must both be the client_id");
  const aud: unknown[] = Array.isArray(payload.aud) ? payload.aud : [payload.aud];
  if (!aud.some((a) => typeof a === "string" && audiences.includes(a))) bad("aud must be the token endpoint or the issuer");
  if (!Number.isInteger(payload.exp) || payload.exp <= now) bad("expired or no exp");
  if (payload.exp > now + ASSERTION_MAX_LIFETIME_SECONDS) bad("exp is more than 5 minutes ahead");
  if (payload.iat !== undefined && (!Number.isInteger(payload.iat) || payload.iat > now + 60)) bad("iat is in the future");
  if (payload.nbf !== undefined && (!Number.isInteger(payload.nbf) || payload.nbf > now + 60)) bad("not valid yet (nbf)");
  if (typeof payload.jti !== "string" || payload.jti.length < 8 || payload.jti.length > 256) bad("jti is required (8 to 256 characters)");
  return { jti: payload.jti, exp: payload.exp };
}
