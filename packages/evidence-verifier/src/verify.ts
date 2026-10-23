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
  /** Entries that carry an anchorRef. This verifier does not resolve anchors against the chain, so a count here is a claim, not a proof. */
  anchoredEntries: number;
  /** Entries without an anchorRef: nothing outside the operator's control commits to them yet. */
  unanchoredEntries: number;
  /** Always false: this verifier does not resolve anchors against the chain. A future resolver sets it, and the CLI and warnings read it. */
  anchorsResolved: boolean;
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
    // A JWKS entry that declares another use or algorithm is not an ES256 signature key, whatever its curve (BCHAT-023).
    if ((key.use !== undefined && key.use !== "sig") || (key.alg !== undefined && key.alg !== "ES256")) return false;
    const pub = createPublicKey({ key: { kty: "EC", crv: "P-256", x: key.x, y: key.y }, format: "jwk" });
    return cryptoVerify("sha256", Buffer.from(text, "utf8"), { key: pub, dsaEncoding: "ieee-p1363" }, sig);
  } catch {
    return false;
  }
}

function parseTime(s: unknown): number {
  return typeof s === "string" && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$/.test(s) ? Date.parse(s) : NaN;
}

/**
 * Whether `key` was valid at `t` (ms), allowing clock skew. A present but unparseable created_at or retired_at is not "no bound":
 * the key is treated as outside its lifetime, so a mangled JWKS cannot widen what a key may sign.
 */
function withinLifetime(key: Jwk, t: number): boolean {
  for (const [bound, isStart] of [[key.created_at, true], [key.retired_at, false]] as const) {
    if (bound === undefined || bound === null || bound === "") continue;
    const b = parseTime(bound);
    if (Number.isNaN(b)) return false;
    if (isStart ? t < b - SKEW_MS : t > b + SKEW_MS) return false;
  }
  return true;
}

/** A key map from a list of JWKs; null entries, non-string or repeated key ids are refused (a Map would silently keep the last). */
function keyMap(keys: unknown[], where: string): { map: Map<string, Jwk> } | { error: string } {
  const map = new Map<string, Jwk>();
  for (const k of keys) {
    const kid = (k as Jwk | null)?.kid;
    if (!k || typeof k !== "object" || typeof kid !== "string" || kid === "") return { error: `${where} has a key without a key id.` };
    if (map.has(kid)) return { error: `${where} lists the key id '${kid}' more than once.` };
    map.set(kid, k as Jwk);
  }
  return { map };
}

function safe(f: () => string): string {
  try { return f(); } catch { return ""; }
}

export function verifyBundle(input: unknown, options: VerifyOptions = {}): VerifyResult {
  const errors: string[] = [];
  const warnings: string[] = [];
  const result: VerifyResult = { valid: false, keysAuthenticated: false, entries: 0, payloadsVerified: 0, payloadsDeleted: 0, anchoredEntries: 0, unanchoredEntries: 0, anchorsResolved: false, errors, warnings };
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
    const km = keyMap(options.jwks.keys, "The JWKS");
    if ("error" in km) { fail(km.error); return result; }
    trusted = km.map;
    result.keysAuthenticated = true;
    for (const k of bundle.keys) {
      const p = trusted.get(k?.kid);
      if (!p || p.x !== k.x || p.y !== k.y || p.crv !== k.crv || p.kty !== k.kty)
        fail(`Embedded key '${String(k?.kid)}' is not in the published JWKS.`);
    }
  } else if (options.allowEmbeddedKeys) {
    const km = keyMap(bundle.keys, "The bundle");
    if ("error" in km) { fail(km.error); return result; }
    trusted = km.map;
    warnings.push("Keys were NOT checked against a published JWKS; this proves only that the bundle is consistent with itself, not that it is authentic.");
  } else {
    fail("No JWKS given. Supply the published evidence JWKS; the bundle's embedded keys are not trusted on their own.");
    return result;
  }

  // Bundle signature.
  const bk = trusted.get(String(bundle.kid));
  if (!bk) fail(`Bundle signing key '${String(bundle.kid)}' is not trusted.`);
  else if (!verifyEs256(bk, safe(() => bundleSigningInput(bundle)), bundle.signature)) fail("Bundle signature does not verify.");
  else {
    // The bundle is signed when it is exported, so the key must have been valid then, like an entry's key at signedAt.
    const exported = parseTime(bundle.exportedAt);
    if (Number.isNaN(exported)) fail("exportedAt is not an RFC 3339 UTC timestamp.");
    else if (!withinLifetime(bk, exported)) fail("exportedAt is outside the bundle signing key's lifetime.");
  }

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

    // Convention: payloads are never the JSON literal null (the log always records an object), so null/absent means
    // "deleted", and a deleted entry has a null salt too. A bundle cannot represent a payload that is JSON null.
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
    if (!withinLifetime(key, t)) { fail(`${at}: signedAt is outside the signing key's lifetime.`); break; }
    if (!verifyEs256(key, signingInput(e.entryHash, e.kid, e.signedAt), e.signature)) { fail(`${at}: signature does not verify.`); break; }

    prev = e.entryHash;
    expected++;
    result.entries++;
    // An empty or blank anchorRef anchors nothing.
    if (e.anchorRef === null || e.anchorRef.trim() === "") result.unanchoredEntries++; else result.anchoredEntries++;
  }

  result.valid = errors.length === 0;
  // "Valid" proves the hash chain and the signatures. It does not prove when the log existed: whoever holds the signing key can rewrite it
  // unless an anchor outside their control commits to it, and this verifier does not resolve anchors (BCHAT-022).
  if (result.valid && !result.anchorsResolved) {
    warnings.push(result.anchoredEntries === 0
      ? "No entry is anchored yet: nothing outside the operator's control commits to this log, so this verifies the chain and signatures only, not that the log existed at a point in time."
      : `${result.anchoredEntries} of ${result.entries} entries carry an anchorRef, but this verifier does not resolve anchors against the chain: the chain and signatures are verified, the anchoring is not.`);
  }
  return result;
}
