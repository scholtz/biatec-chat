// What a relying party and a phone do, over HTTP, for the tests of the mock, the conformance kit and `scripts/rp-demo.mjs`.
// The "phone" is scripted: it uses the test-only device keys of `vectors/keys.json` and the mock's helper `GET /mock/devices/{kid}` (which only a mock started with
// test helpers serves). Nothing here is for production: a real phone is the authenticator app (T-247), a real relying party is a bank.
import { createPrivateKey, generateKeyPairSync, randomBytes, sign as cryptoSign, constants } from "node:crypto";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { detailsHash, jcs, signEs256 } from "../../scripts/lib.mjs";
import { CLIENT_ASSERTION_TYPE } from "./rp.ts";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
/** The test-only keys of the vectors (a device approval key and a presence key): public data, they protect nothing. */
export const vectorKeys = JSON.parse(readFileSync(join(root, "vectors", "keys.json"), "utf8")) as Record<string, Record<string, string>>;

const b64u = (b: Uint8Array | Buffer | string) => Buffer.from(b as never).toString("base64url");

export interface RpKey {
  alg: "ES256" | "PS256";
  kid: string;
  /** The private key (held by the relying party only; never sent anywhere). */
  privateJwk: Record<string, string>;
  /** What the registration sends in `jwks.keys`. */
  publicJwk: Record<string, string>;
}

/** A fresh key pair in memory (nothing is written to disk). */
export function generateRpKey(alg: "ES256" | "PS256" = "ES256", kid = "rp-key-" + randomBytes(4).toString("hex")): RpKey {
  const pair = alg === "ES256" ? generateKeyPairSync("ec", { namedCurve: "P-256" }) : generateKeyPairSync("rsa", { modulusLength: 2048 });
  const privateJwk = { ...(pair.privateKey.export({ format: "jwk" }) as Record<string, string>), kid, alg };
  const publicJwk = { ...(pair.publicKey.export({ format: "jwk" }) as Record<string, string>), kid, alg, use: "sig" };
  return { alg, kid, privateJwk, publicJwk };
}

export interface AssertionOptions {
  /** Default: 60 seconds ahead. */
  exp?: number;
  iat?: number;
  jti?: string;
  iss?: string;
  sub?: string;
  aud?: string | string[];
  /** Default: the key's own. Setting another one signs it the other way (a test of the refusal of `none`, HS256 and friends does not use this: it builds its own JWS). */
  alg?: "ES256" | "PS256";
  kid?: string | null;
  now?: number;
}

/** A client assertion (RFC 7523) for the token endpoint `audience`: iss = sub = client id, a fresh `jti`, expiry a minute ahead. */
export function clientAssertion(key: RpKey, clientId: string, audience: string, o: AssertionOptions = {}): string {
  const now = o.now ?? Math.floor(Date.now() / 1000);
  const alg = o.alg ?? key.alg;
  const header: Record<string, unknown> = { alg, typ: "JWT", ...(o.kid === null ? {} : { kid: o.kid ?? key.kid }) };
  const payload = { iss: o.iss ?? clientId, sub: o.sub ?? clientId, aud: o.aud ?? audience, iat: o.iat ?? now, exp: o.exp ?? now + 60, jti: o.jti ?? randomBytes(16).toString("hex") };
  const input = b64u(JSON.stringify(header)) + "." + b64u(JSON.stringify(payload));
  const k = createPrivateKey({ key: key.privateJwk as never, format: "jwk" });
  const sig = alg === "ES256" ? cryptoSign("sha256", Buffer.from(input), { key: k, dsaEncoding: "ieee-p1363" }) : cryptoSign("sha256", Buffer.from(input), { key: k, padding: constants.RSA_PKCS1_PSS_PADDING, saltLength: 32 });
  return input + "." + b64u(sig);
}

export interface Reply { status: number; headers: Headers; json: any }

export async function httpJson(base: string, method: string, path: string, opts: { headers?: Record<string, string>; body?: unknown; form?: Record<string, string> } = {}): Promise<Reply> {
  const headers: Record<string, string> = { ...opts.headers };
  let body: string | undefined;
  if (opts.form) {
    headers["Content-Type"] = "application/x-www-form-urlencoded";
    body = new URLSearchParams(opts.form).toString();
  } else if (opts.body !== undefined) {
    headers["Content-Type"] = "application/json";
    body = JSON.stringify(opts.body);
  }
  const res = await fetch(base + path, { method, headers, body });
  const text = await res.text();
  let json: any = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* leave null */ }
  return { status: res.status, headers: res.headers, json };
}

/** A registered relying party: its client id and key, and the calls it makes (each with a fresh assertion). */
export interface RelyingParty { base: string; issuer: string; clientId: string; key: RpKey; /** Unix seconds; default the wall clock (a test that moves the mock's clock moves this one too). */ clock?: () => number }

/**
 * A request of the relying party. A request with a body carries `client_assertion_type` and `client_assertion` in it (RFC 7523, form or JSON);
 * one without a body carries the assertion in `Authorization: Client-Assertion <jwt>`. `assertion` overrides the one built (for refusals).
 */
export function rpCall(rp: RelyingParty, method: string, path: string, opts: { body?: Record<string, unknown>; form?: Record<string, string>; assertion?: string; assertionOptions?: AssertionOptions } = {}): Promise<Reply> {
  const jwt = opts.assertion ?? clientAssertion(rp.key, rp.clientId, rp.issuer, { now: rp.clock?.(), ...opts.assertionOptions });
  if (opts.form) return httpJson(rp.base, method, path, { form: { ...opts.form, client_assertion_type: CLIENT_ASSERTION_TYPE, client_assertion: jwt } });
  if (opts.body) return httpJson(rp.base, method, path, { body: { ...opts.body, client_assertion_type: CLIENT_ASSERTION_TYPE, client_assertion: jwt } });
  return httpJson(rp.base, method, path, { headers: { Authorization: "Client-Assertion " + jwt } });
}

/** `POST /register`: a relying party that registers itself (key generated in memory). Returns the answer too, for a test of the refusals. */
export async function registerRelyingParty(base: string, issuer: string, o: { clientUri?: string; alg?: "ES256" | "PS256"; extra?: Record<string, unknown> } = {}) {
  const key = generateRpKey(o.alg ?? "ES256");
  const reply = await httpJson(base, "POST", "/register", {
    body: { token_endpoint_auth_method: "private_key_jwt", grant_types: ["urn:openid:params:grant-type:ciba"], client_uri: o.clientUri ?? "https://bank.example", jwks: { keys: [key.publicJwk] }, ...o.extra },
  });
  const rp: RelyingParty = { base, issuer, clientId: reply.json?.client_id, key };
  return { rp, reply };
}

/** Publishes the well-known file of the relying party at the mock's stand-in fetcher (the mock never goes out to the network): the proof of its domain. Needs the mock's helpers. */
export function proveDomain(rp: RelyingParty, host: string) {
  return httpJson(rp.base, "POST", "/mock/serve", { body: { url: `https://${host}/.well-known/biatec-client.json`, json: { client_ids: [rp.clientId] } } });
}

// ---------------------------------------------------------------------------------------------------------------
// The phone

export interface TestDevice {
  /** `Authorization: Device <token>`. */
  auth: string;
  kid: string;
  publicKey: Record<string, string>;
  attestation: string;
  presence: { kid: string; public_key: Record<string, string>; attestation: string };
  /** The private keys that sign for this device: default the test-only keys of the vectors (the mock's seeded devices). */
  approvalKey?: Record<string, string>;
  presenceKey?: Record<string, string>;
}

/** A seeded device of the mock, by user name (`alice`, ...): its token, keys and attestations, from the mock's helper. Needs `MOCK_TEST_HELPERS=1`. */
export async function testDevice(base: string, user = "alice", o: { kid?: string; token?: string; approvalKey?: Record<string, string>; presenceKey?: Record<string, string> } = {}): Promise<TestDevice> {
  const r = await httpJson(base, "GET", `/mock/devices/${o.kid ?? user + "-device"}`);
  if (r.status !== 200) throw new Error(`the mock has no test helpers (GET /mock/devices/${user}-device answered ${r.status}): start it with MOCK_TEST_HELPERS=1`);
  return { auth: "Device " + (o.token ?? r.json.device_token), kid: r.json.kid, publicKey: r.json.public_key, attestation: r.json.attestation, presence: r.json.presence, ...(o.approvalKey ? { approvalKey: o.approvalKey } : {}), ...(o.presenceKey ? { presenceKey: o.presenceKey } : {}) };
}

/** The link code of the QR: `biatec-authenticator://link?iss=<issuer>&id=<link_id>&s=<link_secret>`. */
export function parseLinkUri(uri: string) {
  const u = new URL(uri);
  return { iss: u.searchParams.get("iss")!, id: u.searchParams.get("id")!, secret: u.searchParams.get("s")! };
}

export interface LinkApproval {
  /** Typed on the phone. Default: the code the relying party was given. */
  code: string;
  decision?: "approve" | "deny";
  /** Sign with the presence key (a refusal case: a presence key never approves). */
  presence?: boolean;
  /** Overrides of the signed members, for the refusal cases (the statement is signed after the change). */
  change?: Record<string, unknown>;
  /** Unix seconds of `issued_at`. */
  now?: number;
}

/** What the phone does on a scanned link code: fetch the link request, build the `biatec_link` statement, sign it with the approval key and post it. Returns both answers. */
export async function approveLink(base: string, dev: TestDevice, linkUri: string, a: LinkApproval) {
  const { id, secret } = parseLinkUri(linkUri);
  const linkRequest = await httpJson(base, "GET", `/consent/links/${id}`, { headers: { Authorization: dev.auth, "X-Link-Secret": secret } });
  if (linkRequest.status !== 200) return { linkRequest, posted: undefined };
  const r = linkRequest.json;
  const decision = a.decision ?? "approve";
  const signed = {
    type: "biatec_link", link_id: r.link_id, client_fingerprint: r.client.fingerprint, domain: r.client.domain ?? null, decision,
    binding_code: a.code, nonce: r.nonce, issued_at: a.now ?? Math.floor(Date.now() / 1000), kid: a.presence ? dev.presence.kid : dev.kid, ...a.change,
  };
  const key = a.presence ? dev.presenceKey ?? vectorKeys.presence : dev.approvalKey ?? vectorKeys.device;
  const posted = await httpJson(base, "POST", `/consent/links/${id}/receipt`, { headers: { Authorization: dev.auth }, body: { signed, signature: signEs256(key, Buffer.from(jcs(signed), "utf8")) } });
  return { linkRequest, posted, signed };
}

export interface RequestApproval {
  code: string;
  decision?: "approve" | "deny";
  presence?: boolean;
  /** Overrides of the signed members (the receipt is signed after the change). */
  change?: Record<string, unknown>;
  now?: number;
}

/**
 * What the phone does for a relying party's request: fetch the request, build the 1.2 receipt from it (the `details_hash` computed from the object it renders, never copied),
 * sign it with the approval key and post it. Returns the fetched request, the receipt and the answer.
 */
export async function approveRequest(base: string, dev: TestDevice, authReqId: string, a: RequestApproval) {
  const fetched = await httpJson(base, "GET", `/consent/requests/${authReqId}`, { headers: { Authorization: dev.auth } });
  if (fetched.status !== 200) return { fetched, receipt: undefined, posted: undefined };
  const request = fetched.json;
  const signed = {
    authorization_details: request.authorization_details,
    auth_req_id: request.auth_req_id,
    client_fingerprint: request.client.fingerprint,
    binding_code: a.code,
    decision: a.decision ?? "approve",
    expiry: request.expiry,
    sub: request.sub,
    binding_message: "",
    details_hash: detailsHash(request.authorization_details[0]),
    ...a.change,
  };
  const key = a.presence ? dev.presenceKey ?? vectorKeys.presence : dev.approvalKey ?? vectorKeys.device;
  const receipt = {
    version: "1.2",
    signed,
    alg: "ES256",
    signature: signEs256(key, Buffer.from(jcs(signed), "utf8")),
    issued_at: a.now ?? Math.floor(Date.now() / 1000),
    device: a.presence
      ? { kid: dev.presence.kid, key_use: "presence", public_key: dev.presence.public_key, attestation: dev.presence.attestation }
      : { kid: dev.kid, key_use: "approve", public_key: dev.publicKey, attestation: dev.attestation },
  };
  const posted = await httpJson(base, "POST", `/consent/requests/${authReqId}/receipt`, { headers: { Authorization: dev.auth }, body: receipt });
  return { fetched, receipt, posted };
}
