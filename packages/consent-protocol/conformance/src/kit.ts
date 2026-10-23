// Everything the suite needs to behave like a client plus an authenticator, and the receipt checks themselves.
// Defaults are the public seed data of the mock; every value can be overridden with environment variables
// (see README.md) so the same suite runs against another implementation.
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020Module from "ajv/dist/2020.js";
import { jcs, signEs256, verifyReceipt } from "../../scripts/lib.mjs";
import { encodeTx } from "../../mock-oidc/src/group.ts";

const Ajv2020 = Ajv2020Module as unknown as typeof Ajv2020Module.default;
const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
export const loadJson = (...p: string[]) => JSON.parse(readFileSync(join(root, ...p), "utf8"));

const env = (name: string, fallback: string) => process.env[name] || fallback;
const vectorKeys = loadJson("vectors", "keys.json");

export const config = {
  issuer: env("CONFORMANCE_ISSUER", "https://oidc.biatec.test"),
  /** Direct entry (and token redemption) client, HTTP Basic. */
  clientId: env("CONFORMANCE_CLIENT_ID", "biatec-chat"),
  clientSecret: env("CONFORMANCE_CLIENT_SECRET", "biatec-chat-secret"),
  loginHint: env("CONFORMANCE_LOGIN_HINT", "alice@example.com"),
  address: env("CONFORMANCE_ADDRESS", "ALICEADDRESS"),
  network: env("CONFORMANCE_NETWORK", "algorand-testnet"),
  origin: env("CONFORMANCE_ORIGIN", "https://chat.biatec.io"),
  /** Bearer token of the same user and client, over the step-up threshold when signing `unsignedGroup`. */
  stepUpBearer: env("CONFORMANCE_STEPUP_BEARER", "alice-chat-sign"),
  /** Authenticator side: device token, kid and the private JWK that signs receipts. */
  deviceToken: env("CONFORMANCE_DEVICE_TOKEN", "device-token-alice"),
  deviceKid: env("CONFORMANCE_DEVICE_KID", "alice-device"),
  devicePrivateJwk: process.env.CONFORMANCE_DEVICE_PRIVATE_JWK ? JSON.parse(process.env.CONFORMANCE_DEVICE_PRIVATE_JWK) : vectorKeys.device,
  /** Unsigned group items above the user's step-up threshold. Default is the mock's descriptor encoding. */
  unsignedGroup: process.env.CONFORMANCE_UNSIGNED_GROUP
    ? (JSON.parse(process.env.CONFORMANCE_UNSIGNED_GROUP) as string[])
    : [
        encodeTx({
          sender: env("CONFORMANCE_ADDRESS", "ALICEADDRESS"),
          receiver: "PAYEE",
          asset: { id: "31566704", symbol: "USDC", decimals: 6 },
          amount: "5000.000000",
          fiat_eur: "5000.00",
        }),
      ],
};

// ---------------------------------------------------------------------------------------------------------------
// Schemas

const ajv = new Ajv2020({ allErrors: true, strict: true });
for (const f of readdirSync(join(root, "schemas")).filter((n) => n.endsWith(".schema.json"))) ajv.addSchema(loadJson("schemas", f));

/** Returns a list of problems (empty when the data matches the named schema). */
export function schemaProblems(name: string, data: unknown): string[] {
  const v = ajv.getSchema(`https://schemas.biatec.io/consent/v1/${name}.schema.json`);
  if (!v) return [`unknown schema ${name}`];
  return v(data) ? [] : [`${name}: ${ajv.errorsText(v.errors)}`];
}

// ---------------------------------------------------------------------------------------------------------------
// HTTP

export interface Reply { status: number; headers: Headers; json: any }

export const basicAuth = (id: string, secret: string) => "Basic " + Buffer.from(`${encodeURIComponent(id)}:${encodeURIComponent(secret)}`).toString("base64");

export async function http(base: string, method: string, path: string, opts: { auth?: string; body?: unknown; form?: Record<string, string> } = {}): Promise<Reply> {
  const headers: Record<string, string> = {};
  if (opts.auth) headers.Authorization = opts.auth;
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

// ---------------------------------------------------------------------------------------------------------------
// Receipt checks: schema, canonical form and signing rules

export interface ReceiptContext {
  jwks: { keys: Record<string, string>[] };
  /** The consent request as the authenticator saw it. */
  request: Record<string, any>;
  bindingCode: string;
  now?: number;
}

/**
 * All conformance checks that apply to a consent receipt, whichever entry produced it. Returns the list of
 * violations, empty when the receipt conforms.
 */
export function receiptProblems(receipt: any, ctx: ReceiptContext): string[] {
  const problems: string[] = [];
  const schema = schemaProblems("consent-receipt", receipt);
  if (schema.length) return schema; // nothing below is meaningful on a malformed receipt
  const s = receipt.signed;

  // Canonical form: the signature is over the RFC 8785 form of `signed`, so a re-serialization must be identical.
  try {
    const canonical = jcs(s);
    if (jcs(JSON.parse(canonical)) !== canonical) problems.push("canonical form is not stable under re-serialization");
  } catch (e) {
    return [`signed is not canonicalizable: ${(e as Error).message}`];
  }

  // Signing rules: device ES256 signature over JCS(signed) and the Biatec OIDC attestation of the device key.
  if (!verifyReceipt(receipt, ctx.jwks, { issuer: config.issuer, now: ctx.now })) problems.push("signature or device attestation does not verify against the published JWKS");

  // The receipt restates the request member for member.
  const r = ctx.request;
  const same = (label: string, a: unknown, b: unknown) => { if (jcs(a) !== jcs(b)) problems.push(`signed.${label} differs from the consent request`); };
  same("auth_req_id", s.auth_req_id, r.auth_req_id);
  same("client_entity_id", s.client_entity_id, r.client.entity_id);
  same("sub", s.sub, r.sub);
  same("expiry", s.expiry, r.expiry);
  same("binding_message", s.binding_message, r.binding_message);
  same("authorization_details", s.authorization_details, r.authorization_details);
  if (s.binding_code !== ctx.bindingCode) problems.push("signed.binding_code is not the code that was entered");
  if (s.decision !== "approve") problems.push(`signed.decision is ${s.decision}, expected approve`);
  return problems;
}

/** The structure a relying party's audit trail depends on; the entries must agree on it. */
export const receiptShape = (receipt: any) => ({
  top: Object.keys(receipt).sort(),
  signed: Object.keys(receipt.signed).sort(),
  alg: receipt.alg,
  version: receipt.version,
});

// ---------------------------------------------------------------------------------------------------------------
// Flows

export interface FlowResult {
  entry: "step-up" | "direct";
  request: Record<string, any>;
  bindingCode: string;
  receipt: any;
  tokenReply: Reply;
  signReply: Reply;
  jwks: { keys: Record<string, string>[] };
}

const clientAuth = () => basicAuth(config.clientId, config.clientSecret);
const deviceAuth = () => "Device " + config.deviceToken;
export const signPath = () => `/wallet/${config.network}/${config.address}/sign`;

/** What the authenticator does: build a receipt from the fetched request and the typed code, and sign it. */
async function approve(base: string, authReqId: string, code: string) {
  const reqReply = await http(base, "GET", `/consent/requests/${authReqId}`, { auth: deviceAuth() });
  if (reqReply.status !== 200) throw new Error(`device could not fetch the consent request: HTTP ${reqReply.status} ${JSON.stringify(reqReply.json)}`);
  const request = reqReply.json;
  const devReply = await http(base, "GET", `/mock/devices/${config.deviceKid}`);
  if (devReply.status !== 200) throw new Error("device attestation lookup failed (CONFORMANCE_DEVICE_* must describe an enrolled device)");
  const signed = {
    authorization_details: request.authorization_details,
    auth_req_id: request.auth_req_id,
    client_entity_id: request.client.entity_id,
    binding_code: code,
    decision: "approve",
    expiry: request.expiry,
    sub: request.sub,
    binding_message: request.binding_message,
  };
  const receipt = {
    version: "1",
    signed,
    alg: "ES256",
    signature: signEs256(config.devicePrivateJwk, Buffer.from(jcs(signed), "utf8")),
    issued_at: Math.floor(Date.now() / 1000),
    device: { kid: config.deviceKid, public_key: devReply.json.public_key, attestation: devReply.json.attestation },
  };
  const posted = await http(base, "POST", `/consent/requests/${authReqId}/receipt`, { auth: deviceAuth(), body: receipt });
  if (posted.status !== 200) throw new Error(`receipt was not accepted: HTTP ${posted.status} ${JSON.stringify(posted.json)}`);
  return request;
}

async function redeem(base: string, authReqId: string) {
  // The mock decides on the first poll; a real server may answer authorization_pending first.
  // A conforming client waits the interval between polls (2 s here) and adds 5 s after every slow_down.
  let waitMs = 2500;
  for (let i = 0; i < 6; i++) {
    const r = await http(base, "POST", "/token", { auth: clientAuth(), form: { grant_type: "urn:openid:params:grant-type:ciba", auth_req_id: authReqId } });
    if (r.json?.error !== "authorization_pending" && r.json?.error !== "slow_down") return r;
    if (r.json?.error === "slow_down") waitMs += 5000;
    await new Promise((resolve) => setTimeout(resolve, waitMs));
  }
  throw new Error("token endpoint stayed authorization_pending");
}

async function finish(base: string, entry: FlowResult["entry"], authReqId: string, code: string): Promise<FlowResult> {
  const request = await approve(base, authReqId, code);
  const tokenReply = await redeem(base, authReqId);
  if (tokenReply.status !== 200) throw new Error(`token endpoint refused: HTTP ${tokenReply.status} ${JSON.stringify(tokenReply.json)}`);
  const signReply = await http(base, "POST", signPath(), { auth: "Bearer " + tokenReply.json.access_token, body: { unsigned_group: config.unsignedGroup } });
  const jwks = (await http(base, "GET", "/.well-known/jwks.json")).json;
  return { entry, request, bindingCode: code, receipt: tokenReply.json.consent_receipt, tokenReply, signReply, jwks };
}

/** Step-up entry (RFC 9470): signing above the threshold answers 401 with the challenge, then the CIBA grant. */
export async function runStepUp(base: string): Promise<FlowResult & { challenge: Reply }> {
  const challenge = await http(base, "POST", signPath(), { auth: "Bearer " + config.stepUpBearer, body: { unsigned_group: config.unsignedGroup } });
  if (challenge.status !== 401) throw new Error(`expected the step-up challenge (401), got HTTP ${challenge.status} ${JSON.stringify(challenge.json)}`);
  const flow = await finish(base, "step-up", challenge.json.auth_req_id, challenge.json.binding_code);
  return { ...flow, challenge };
}

/** Direct entry: the verified client opens a backchannel authentication request itself. */
export async function runDirect(base: string): Promise<FlowResult & { start: Reply }> {
  const start = await http(base, "POST", "/connect/ciba", {
    auth: clientAuth(),
    body: {
      scope: "sign",
      resource: "https://api.biatec.io",
      login_hint: config.loginHint,
      network: config.network,
      address: config.address,
      unsigned_group: config.unsignedGroup,
      origin: config.origin,
      binding_message: "Conformance payment",
    },
  });
  if (start.status !== 200) throw new Error(`direct entry refused: HTTP ${start.status} ${JSON.stringify(start.json)}`);
  const flow = await finish(base, "direct", start.json.auth_req_id, start.json.binding_code);
  return { ...flow, start };
}
