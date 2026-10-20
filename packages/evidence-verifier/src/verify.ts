import { createHash, createPublicKey, verify as cryptoVerify } from "node:crypto";

// Offline verification of a Biatec Chat evidence bundle (PRODUCT-DESCRIPTION.md, Evidence Record).
// Hashes are lowercase hex SHA-256 over RFC 8785 canonical JSON; signatures are ES256 (IEEE P1363) over UTF-8 JCS.

export interface Jwk {
  kty: string;
  crv: string;
  alg?: string;
  use?: string;
  kid: string;
  x: string;
  y: string;
  created_at?: string;
  retired_at?: string;
}
export interface Jwks {
  keys: Jwk[];
}
export interface BundleEntry {
  seq: number;
  kind: string;
  anchorRef: string | null;
  payload: unknown;
  salt: string | null;
  payloadHash: string;
  prevHash: string;
  entryHash: string;
  kid: string;
  signedAt: string;
  signature: string;
}
export interface EvidenceBundle {
  format: string;
  version: number;
  conversationId: string;
  exportedAt: string;
  kid: string;
  jwksUrl?: string;
  keys: Jwk[];
  entries: BundleEntry[];
  signature: string;
}
export interface VerifyOptions {
  /** The published JWKS (fetched at verification time or pinned earlier). The only source of trusted keys. */
  jwks?: Jwks;
  /** Trust the keys embedded in the bundle. Proves self-consistency only, never authenticity. */
  allowEmbeddedKeys?: boolean;
}
export interface VerifyResult {
  valid: boolean;
  /** False when only the bundle's own keys were used. */
  keysAuthenticated: boolean;
  entries: number;
  /** Entries whose payload hash was recomputed from the payload. */
  payloadsVerified: number;
  /** Entries whose payload was deleted; the stored hash was used. */
  payloadsDeleted: number;
  errors: string[];
  warnings: string[];
}

export const FORMAT = "biatec-evidence-bundle";
const GENESIS = "0".repeat(64);
const SKEW_MS = 60_000;
const HEX64 = /^[0-9a-f]{64}$/;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const B64U = /^[A-Za-z0-9_-]*$/;

/** RFC 8785 canonical JSON. Node's JSON.stringify already emits ECMAScript number and string forms. */
export function jcs(value: unknown): string {
  if (typeof value === "string") {
    if (!value.isWellFormed()) throw new Error("lone surrogate");
    return JSON.stringify(value);
  }
  if (value === null || typeof value === "boolean") return JSON.stringify(value);
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new Error("non-finite number");
    if (Number.isInteger(value) && !Number.isSafeInteger(value)) throw new Error("integer above 2^53 must be a decimal string");
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) return "[" + value.map(jcs).join(",") + "]";
  if (typeof value === "object") {
    const o = value as Record<string, unknown>;
    return "{" + Object.keys(o).sort().map((k) => jcs(k) + ":" + jcs(o[k])).join(",") + "}";
  }
  throw new Error("unsupported value");
}

export const sha256Hex = (text: string): string => createHash("sha256").update(text, "utf8").digest("hex");

export const payloadHash = (salt: string, payload: unknown): string => sha256Hex(jcs({ salt, payload }));

export const entryHash = (e: { conversationId: string; seq: number; prevHash: string; kind: string; payloadHash: string; anchorRef: string | null }): string =>
  sha256Hex(jcs({ conversationId: e.conversationId, seq: e.seq, prevHash: e.prevHash, kind: e.kind, payloadHash: e.payloadHash, anchorRef: e.anchorRef }));

export const signingInput = (entryHashHex: string, kid: string, signedAt: string): string => jcs({ entryHash: entryHashHex, kid, signedAt });

/** The bundle signature covers JCS of the whole bundle without its `signature` member. */
export const bundleSigningInput = (bundle: EvidenceBundle): string => {
  const { signature: _omit, ...body } = bundle;
  return jcs(body);
};

function decodeB64u(s: unknown): Buffer | null {
  if (typeof s !== "string" || !B64U.test(s)) return null;
  const buf = Buffer.from(s, "base64url");
  return buf.toString("base64url") === s ? buf : null;
}

function verifyEs256(key: Jwk, text: string, signature: unknown): boolean {
  try {
    const sig = decodeB64u(signature);
    if (!sig || sig.length !== 64) return false;
    if (key.kty !== "EC" || key.crv !== "P-256") return false;
    const pub = createPublicKey({ key: { kty: "EC", crv: "P-256", x: key.x, y: key.y }, format: "jwk" });
    return cryptoVerify("sha256", Buffer.from(text, "utf8"), { key: pub, dsaEncoding: "ieee-p1363" }, sig);
  } catch {
    return false;
  }
}

function parseTime(s: unknown): number {
  return typeof s === "string" && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$/.test(s) ? Date.parse(s) : NaN;
}

function safe(f: () => string): string {
  try { return f(); } catch { return ""; }
}

export function verifyBundle(input: unknown, options: VerifyOptions = {}): VerifyResult {
  const errors: string[] = [];
  const warnings: string[] = [];
  const result: VerifyResult = { valid: false, keysAuthenticated: false, entries: 0, payloadsVerified: 0, payloadsDeleted: 0, errors, warnings };
  const fail = (m: string) => { errors.push(m); };

  const b = input as Partial<EvidenceBundle> | null;
  if (!b || typeof b !== "object" || Array.isArray(b)) { fail("Bundle is not a JSON object."); return result; }
  if (b.format !== FORMAT) fail(`Unknown bundle format (expected "${FORMAT}").`);
  if (b.version !== 1) fail("Unsupported bundle version.");
  if (typeof b.conversationId !== "string" || !UUID.test(b.conversationId)) fail("conversationId is not a lowercase UUID.");
  if (!Array.isArray(b.entries) || b.entries.length === 0) fail("Bundle has no entries.");
  if (!Array.isArray(b.keys)) fail("Bundle has no keys array.");
  if (errors.length) return result;
  const bundle = b as EvidenceBundle;

  // Trusted keys: the published JWKS. Embedded keys are a convenience and must all be in the JWKS.
  let trusted: Map<string, Jwk>;
  if (options.jwks) {
    if (!Array.isArray(options.jwks.keys)) { fail("The JWKS has no keys array."); return result; }
    trusted = new Map(options.jwks.keys.map((k) => [k.kid, k]));
    result.keysAuthenticated = true;
    for (const k of bundle.keys) {
      const p = trusted.get(k?.kid);
      if (!p || p.x !== k.x || p.y !== k.y || p.crv !== k.crv || p.kty !== k.kty)
        fail(`Embedded key '${String(k?.kid)}' is not in the published JWKS.`);
    }
  } else if (options.allowEmbeddedKeys) {
    trusted = new Map(bundle.keys.map((k) => [k.kid, k]));
    warnings.push("Keys were NOT checked against a published JWKS; this proves only that the bundle is consistent with itself, not that it is authentic.");
  } else {
    fail("No JWKS given. Supply the published evidence JWKS; the bundle's embedded keys are not trusted on their own.");
    return result;
  }

  // Bundle signature.
  const bk = trusted.get(String(bundle.kid));
  if (!bk) fail(`Bundle signing key '${String(bundle.kid)}' is not trusted.`);
  else if (!verifyEs256(bk, safe(() => bundleSigningInput(bundle)), bundle.signature)) fail("Bundle signature does not verify.");

  // Chain.
  let prev = GENESIS;
  let expected = 1;
  for (const e of bundle.entries) {
    if (!e || typeof e !== "object") { fail(`Entry ${expected} is not an object.`); break; }
    const at = `Entry ${String(e.seq)}`;
    if (e.seq !== expected) { fail(`${at}: expected seq ${expected}.`); break; }
    if (e.prevHash !== prev) { fail(`${at}: prevHash does not match the previous entryHash.`); break; }
    if (typeof e.kind !== "string" || e.kind === "") { fail(`${at}: kind is missing.`); break; }
    if (!HEX64.test(String(e.payloadHash)) || !HEX64.test(String(e.entryHash)) || !HEX64.test(String(e.prevHash))) { fail(`${at}: hash is not lowercase hex SHA-256.`); break; }
    if (e.anchorRef !== null && typeof e.anchorRef !== "string") { fail(`${at}: anchorRef must be a string or null.`); break; }

    const hasPayload = e.payload !== null && e.payload !== undefined;
    const hasSalt = e.salt !== null && e.salt !== undefined;
    if (hasPayload !== hasSalt) { fail(`${at}: payload and salt must be present or deleted together.`); break; }
    if (hasPayload) {
      if (typeof e.salt !== "string" || !HEX64.test(e.salt)) { fail(`${at}: salt is not 32 bytes of lowercase hex.`); break; }
      let ph: string;
      try { ph = payloadHash(e.salt, e.payload); } catch (err) { fail(`${at}: payload cannot be canonicalized (${(err as Error).message}).`); break; }
      if (ph !== e.payloadHash) { fail(`${at}: payloadHash does not match the payload.`); break; }
      result.payloadsVerified++;
    } else {
      result.payloadsDeleted++; // deletion never breaks the chain: the stored payloadHash is used
    }

    if (entryHash({ conversationId: bundle.conversationId, seq: e.seq, prevHash: e.prevHash, kind: e.kind, payloadHash: e.payloadHash, anchorRef: e.anchorRef }) !== e.entryHash) {
      fail(`${at}: entryHash does not match the entry.`); break;
    }

    const key = trusted.get(e.kid);
    if (!key) { fail(`${at}: unknown or untrusted signing key '${String(e.kid)}'.`); break; }
    const t = parseTime(e.signedAt);
    if (Number.isNaN(t)) { fail(`${at}: signedAt is not an RFC 3339 UTC timestamp.`); break; }
    const created = key.created_at ? parseTime(key.created_at) : NaN;
    const retired = key.retired_at ? parseTime(key.retired_at) : NaN;
    if ((!Number.isNaN(created) && t < created - SKEW_MS) || (!Number.isNaN(retired) && t > retired + SKEW_MS)) {
      fail(`${at}: signedAt is outside the signing key's lifetime.`); break;
    }
    if (!verifyEs256(key, signingInput(e.entryHash, e.kid, e.signedAt), e.signature)) { fail(`${at}: signature does not verify.`); break; }

    prev = e.entryHash;
    expected++;
    result.entries++;
  }

  result.valid = errors.length === 0;
  return result;
}
