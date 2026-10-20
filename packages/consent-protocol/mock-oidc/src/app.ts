// Mock Biatec OIDC consent server. It implements only what the consent protocol needs:
//   POST /connect/ciba                        direct entry (backchannel authentication)
//   POST /wallet/:network/:address/sign       step-up entry (RFC 9470 challenge) and signing with a consent token
//   PUT  /wallet/limits                       step-up entry for loosening limits
//   POST /token                               CIBA token grant, returns consent_receipt
//   GET  /consent/receipts/:auth_req_id       receipt retrieval by the owning client
//   POST /devices                             device enrollment (rule 7)
//   PUT  /devices/push                        register or replace the device's FCM/APNs token
//   GET  /devices/requests, GET /consent/requests/:id, POST /consent/requests/:id/receipt   authenticator side
//   GET  /.well-known/jwks.json, /.well-known/openid-configuration
//   GET /devices, POST /devices/:kid/cancel   device list; cancelling a pending enrollment (rule 7, T-084)
//   GET/PUT /devices/settings                 consent settings; loosening needs a device-signed biatec_settings_change (T-084)
//   GET/POST /mock/...                        test helpers (activate a device, inspect state, push outbox)
// Push: opening a request queues one message per active device with a push token; its data is only the
// auth_req_id (GET /mock/push-outbox shows what would be sent). Nothing is sent to FCM or APNs.
// Rules enforced: 2 (binding code, 3 attempts), 4 (first use), 5 (300 s, single use, group-hash binding),
// 7 (24 h inactive first device), 8 (no device, no above-threshold actions), 9 (offline-verifiable receipts).
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import { randomBytes, randomInt, timingSafeEqual } from "node:crypto";
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020Module from "ajv/dist/2020.js";
import { jcs, sha256Hex, signCompactJws, thumbprint, verifyApproval, verifyEs256, verifyReceipt, ATTESTATION_TYP } from "../../scripts/lib.mjs";
import { GroupError, decodeGroup, transactionDetails } from "./group.ts";
import { clients, staticTokens, users, type SeedClient, type SeedUser } from "./seed.ts";

const Ajv2020 = Ajv2020Module as unknown as typeof Ajv2020Module.default;
const pkgRoot = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const loadJson = (...p: string[]) => JSON.parse(readFileSync(join(pkgRoot, ...p), "utf8"));

export const CONSENT_VALIDITY_SECONDS = 300;
export const MAX_CODE_ATTEMPTS = 3;
export const INACTIVE_DEVICE_SECONDS = 24 * 3600;
export const ISSUER = "https://oidc.biatec.test";

export interface MockOptions {
  /** Unix seconds. Defaults to the wall clock. */
  now?: () => number;
  issuer?: string;
  /** Binding code generator, for deterministic tests. */
  newBindingCode?: () => string;
  /** Overrides vectors/keys.json (the test-only Biatec OIDC signing key). */
  oidcKey?: Record<string, string>;
}

interface Device { kid: string; sub: string; publicKey: Record<string, string>; token: string; activeAt: number; cancelled: boolean; attestation: string; recoveryCode?: string; recoveryAcknowledged?: boolean; push?: PushTarget }
interface PushTarget { provider: "fcm" | "apns"; token: string }
// What a real deployment hands to FCM or APNs. The data payload is the request id and nothing else.
interface PushMessage { kid: string; provider: PushTarget["provider"]; token: string; data: { auth_req_id: string } }
interface Pending {
  request: Record<string, any>;
  code: string;
  attempts: number;
  clientId: string;
  status: "pending" | "approved" | "denied" | "code_failed";
  receipt?: Record<string, any>;
  tokenIssued: boolean;
  scope: string;
  /** For limits requests: the body to apply once Biatec OIDC accepts the consent. */
  limitsBody?: Record<string, string>;
}
interface Token { sub: string; clientId: string; scope: string; kind: "access" | "login" | "consent"; exp?: number; authReqId?: string }

class HttpError extends Error {
  status: number;
  body: Record<string, unknown>;
  headers: Record<string, string>;
  constructor(status: number, error: string, description?: string, extra: Record<string, unknown> = {}, headers: Record<string, string> = {}) {
    super(description ?? error);
    this.status = status;
    this.body = { error, ...(description ? { error_description: description } : {}), ...extra };
    this.headers = headers;
  }
}

const safeEqual = (a: string, b: string) => {
  const x = Buffer.from(a);
  const y = Buffer.from(b);
  return x.length === y.length && timingSafeEqual(x, y);
};

function parseForm(text: string): Record<string, unknown> {
  const out: Record<string, any> = {};
  for (const [k, v] of new URLSearchParams(text)) {
    if (k in out) out[k] = [].concat(out[k], v as never);
    else out[k] = v;
  }
  for (const k of ["unsigned_group"]) if (typeof out[k] === "string") out[k] = [out[k]];
  if (typeof out.limits_request === "string") out.limits_request = JSON.parse(out.limits_request);
  if (typeof out.requested_expiry === "string") out.requested_expiry = Number(out.requested_expiry);
  return out;
}

export function createMockOidc(options: MockOptions = {}) {
  const now = options.now ?? (() => Math.floor(Date.now() / 1000));
  const issuer = options.issuer ?? ISSUER;
  const newCode = options.newBindingCode ?? (() => String(randomInt(0, 1_000_000)).padStart(6, "0"));
  const keys = options.oidcKey ? { oidc: options.oidcKey, device: loadJson("vectors", "keys.json").device } : loadJson("vectors", "keys.json");
  const oidcKey = keys.oidc;
  const { d: _d, ...oidcPublic } = oidcKey;
  const jwks = { keys: [oidcPublic] };

  const ajv = new Ajv2020({ allErrors: true, strict: true });
  const schemaDir = join(pkgRoot, "schemas");
  for (const f of readdirSync(schemaDir).filter((n) => n.endsWith(".schema.json"))) ajv.addSchema(loadJson("schemas", f));
  const validator = (name: string) => ajv.getSchema(`https://schemas.biatec.io/consent/v1/${name}.schema.json`)!;
  const validate = (name: string, data: unknown, label: string) => {
    const v = validator(name);
    if (!v(data)) throw new HttpError(400, "invalid_request", `${label}: ${ajv.errorsText(v.errors)}`);
  };

  // State
  const userMap = new Map<string, SeedUser & { used: Set<string> }>();
  for (const u of users) userMap.set(u.sub, { ...structuredClone(u), used: new Set(u.usedClients) });
  const clientMap = new Map<string, SeedClient>(clients.map((c) => [c.id, c]));
  const tokens = new Map<string, Token>(Object.entries(staticTokens).map(([k, v]) => [k, { ...v }]));
  const devices = new Map<string, Device>();
  const pending = new Map<string, Pending>();
  const consumed = new Set<string>();
  const pushOutbox: PushMessage[] = [];
  // Consent settings beyond what the seed holds (T-084). Keys: sub, or sub|clientId.
  const accountSettings = new Map<string, { biometricRequired: boolean; blockUnverified: boolean }>();
  const clientSettings = new Map<string, { perClientThresholdEur?: number; pausedUntil?: number }>();
  const settingsNonces = new Set<string>();

  const attest = (sub: string, kid: string, pub: Record<string, string>) =>
    signCompactJws(oidcKey, { alg: "ES256", kid: oidcKey.kid, typ: ATTESTATION_TYP }, {
      iss: issuer, sub, device_kid: kid, device_jwk_thumbprint: thumbprint(pub as never), iat: now(), exp: now() + 365 * 86400,
    });
  const addDevice = (sub: string, kid: string, publicKey: Record<string, string>, activeAt: number, token = randomBytes(24).toString("base64url")) => {
    const d: Device = { kid, sub, publicKey, token, activeAt, cancelled: false, attestation: attest(sub, kid, publicKey) };
    devices.set(kid, d);
    return d;
  };
  // Seeded active devices use the vector device key, so receipts signed with vectors/keys.json verify.
  const { d: _dd, kid: _dk, alg: _da, use: _du, ...devicePub } = keys.device;
  for (const u of users) if (u.activeDevice) addDevice(u.sub, u.email.split("@")[0] + "-device", devicePub, 0, "device-token-" + u.email.split("@")[0]);

  const activeDevices = (sub: string) => [...devices.values()].filter((d) => d.sub === sub && !d.cancelled && d.activeAt <= now());

  // Helpers
  const clientInfo = (c: SeedClient) =>
    c.verified
      ? { entity_id: c.entityId, verified: true, display_name: c.displayName, domain: c.domain }
      : { entity_id: c.entityId, verified: false, fingerprint: c.fingerprint };

  function authClient(req: IncomingMessage, body: Record<string, any>): SeedClient {
    let id: string | undefined, secret: string | undefined;
    const h = req.headers.authorization;
    if (h?.startsWith("Basic ")) {
      const raw = Buffer.from(h.slice(6), "base64").toString("utf8");
      const i = raw.indexOf(":");
      id = decodeURIComponent(raw.slice(0, i));
      secret = decodeURIComponent(raw.slice(i + 1));
    } else {
      id = body.client_id;
      secret = body.client_secret;
    }
    const c = id ? clientMap.get(id) : undefined;
    if (!c || typeof secret !== "string" || !safeEqual(secret, c.secret)) throw new HttpError(401, "invalid_client", "client authentication failed", {}, { "WWW-Authenticate": 'Basic realm="mock-oidc"' });
    return c;
  }

  function bearer(req: IncomingMessage): Token & { value: string } {
    const h = req.headers.authorization;
    const value = h?.startsWith("Bearer ") ? h.slice(7) : "";
    const t = tokens.get(value);
    if (!t || (t.exp !== undefined && t.exp <= now())) throw new HttpError(401, "invalid_token", "missing, unknown or expired bearer token", {}, { "WWW-Authenticate": 'Bearer error="invalid_token"' });
    return { ...t, value };
  }

  function deviceAuth(req: IncomingMessage): Device {
    const h = req.headers.authorization;
    const value = h?.startsWith("Device ") ? h.slice(7) : "";
    const d = [...devices.values()].find((x) => !x.cancelled && safeEqual(x.token, value));
    if (!d) throw new HttpError(401, "invalid_token", "unknown device token");
    return d;
  }

  const expired = (p: Pending) => p.request.expiry <= now();

  function openRequest(opts: { entry: "step-up" | "direct"; user: SeedUser; client: SeedClient; details: Record<string, any>; bindingMessage: string; requestedExpiry: number; origin: string; scope: string; limitsBody?: Record<string, string> }) {
    const authReqId = "ciba_" + randomBytes(16).toString("hex");
    const code = newCode();
    // Rendered, and so signed, only for a verified client and only in the direct entry (spec section 5).
    const message = opts.entry === "direct" && opts.client.verified ? opts.bindingMessage : "";
    const request = {
      auth_req_id: authReqId,
      entry: opts.entry,
      authorization_details: [opts.details],
      client: clientInfo(opts.client),
      binding_message: message,
      requested_expiry: opts.requestedExpiry,
      expiry: now() + opts.requestedExpiry,
      bound_hash: opts.details.group_hash ?? opts.details.request_hash,
      origin: opts.origin,
      sub: opts.user.sub,
    };
    validate("consent-request", request, "internal consent request");
    pending.set(authReqId, { request, code, attempts: 0, clientId: opts.client.id, status: "pending", tokenIssued: false, scope: opts.scope, limitsBody: opts.limitsBody });
    for (const d of activeDevices(opts.user.sub)) if (d.push) pushOutbox.push({ kid: d.kid, ...d.push, data: { auth_req_id: authReqId } });
    if (pushOutbox.length > 500) pushOutbox.splice(0, pushOutbox.length - 500); // a long-running mock keeps the recent ones
    return { authReqId, code, expiresIn: opts.requestedExpiry };
  }

  function limitsDetails(user: SeedUser, body: Record<string, string>) {
    return {
      type: "biatec_limits_change",
      current_limits: { ...user.limits },
      requested_limits: { per_transaction_eur: String(body.per_transaction_eur), per_day_eur: String(body.per_day_eur) },
      request_hash: sha256Hex(jcs(body)),
    };
  }
  const isLoosening = (user: SeedUser, body: Record<string, string>) =>
    Number(body.per_transaction_eur) > Number(user.limits.per_transaction_eur) || Number(body.per_day_eur) > Number(user.limits.per_day_eur);

  // --- Handlers -------------------------------------------------------------------------------------------------

  function ciba(req: IncomingMessage, body: Record<string, any>) {
    const client = authClient(req, body);
    const { client_id: _a, client_secret: _b, ...params } = body;
    validate("backchannel-authentication-request", params, "backchannel authentication request");
    if (params.scope === "manage-limits" && !client.allowManageLimits) throw new HttpError(403, "unauthorized_client", "manage-limits is reserved for operator-allowlisted clients");
    const user = [...userMap.values()].find((u) => u.email === params.login_hint || u.sub === params.login_hint);
    if (!user) throw new HttpError(400, "unknown_user_id", "login_hint does not identify a user");
    if (activeDevices(user.sub).length === 0) throw new HttpError(403, "access_denied", "the user has no active enrolled device (rule 8)", { reason: "no_active_device" });
    let details: Record<string, any>;
    let limitsBody: Record<string, string> | undefined;
    if (params.scope === "sign") {
      try {
        details = transactionDetails(decodeGroup(params.unsigned_group, params.address), params.network);
      } catch (e) {
        if (e instanceof GroupError) throw new HttpError(400, "invalid_request", e.message);
        throw e;
      }
    } else {
      limitsBody = params.limits_request;
      details = limitsDetails(user, limitsBody!);
    }
    const r = openRequest({ entry: "direct", user, client, details, bindingMessage: params.binding_message ?? "", requestedExpiry: params.requested_expiry ?? CONSENT_VALIDITY_SECONDS, origin: params.origin, scope: params.scope, limitsBody });
    const res = { auth_req_id: r.authReqId, expires_in: r.expiresIn, interval: 2, binding_code: r.code };
    validate("backchannel-authentication-response", res, "internal response");
    return { status: 200, body: res };
  }

  function requireScope(t: Token, scope: string) {
    if (!t.scope.split(" ").includes(scope)) throw new HttpError(403, "insufficient_scope", `the token lacks the ${scope} scope`, {}, { "WWW-Authenticate": `Bearer error="insufficient_scope", scope="${scope}"` });
  }

  function challenge(t: Token, details: Record<string, any>, scope: string, origin: string, limitsBody?: Record<string, string>) {
    const user = userMap.get(t.sub)!;
    const client = clientMap.get(t.clientId)!;
    if (activeDevices(user.sub).length === 0) {
      throw new HttpError(403, "access_denied", "phone approval is required but the user has no active enrolled device (rule 8)", { reason: "no_active_device" });
    }
    const r = openRequest({ entry: "step-up", user, client, details, bindingMessage: "", requestedExpiry: CONSENT_VALIDITY_SECONDS, origin, scope, limitsBody });
    const body = { error: "insufficient_user_authentication", error_description: "Phone approval is required for this action.", acr_values: "biatec:consent", auth_req_id: r.authReqId, expires_in: r.expiresIn, interval: 2, binding_code: r.code };
    validate("stepup-challenge", body, "internal challenge");
    return new HttpError(401, body.error, body.error_description, { acr_values: body.acr_values, auth_req_id: body.auth_req_id, expires_in: body.expires_in, interval: body.interval, binding_code: body.binding_code }, {
      "WWW-Authenticate": 'Bearer error="insufficient_user_authentication", acr_values="biatec:consent"',
    });
  }

  /** Verifies a consent token against the stored request and the presented hash. Spends the consent only after this returns. */
  function checkConsent(t: Token, boundHash: string, scope: string, network?: string): Pending {
    const p = t.authReqId ? pending.get(t.authReqId) : undefined;
    if (!p || p.status !== "approved" || !p.receipt) throw new HttpError(401, "invalid_token", "no approved consent for this token");
    if (consumed.has(t.authReqId!)) throw new HttpError(401, "invalid_token", "this consent was already used (rule 5)", { reason: "consent_consumed" });
    if (p.scope !== scope) throw new HttpError(403, "insufficient_scope", "the consent was granted for another action");
    if (network !== undefined && p.request.authorization_details?.[0]?.network !== network) throw new HttpError(403, "group_hash_mismatch", "the consent was granted for another network");
    if (p.request.bound_hash !== boundHash) throw new HttpError(403, "group_hash_mismatch", "the presented group differs from the one consented to (rule 5)");
    if (!verifyApproval(p.receipt, jwks, p.request, p.code, consumed, { issuer, now: now() })) throw new HttpError(401, "invalid_token", "the consent receipt does not verify (expired, altered or wrong code)");
    return p;
  }

  function sign(req: IncomingMessage, params: Record<string, string>, body: Record<string, any>) {
    const t = bearer(req);
    requireScope(t, "sign");
    const { network, address } = params;
    if (!/^(algorand|bitcoin)-(mainnet|testnet)$/.test(network)) throw new HttpError(404, "unknown_network");
    if (!Array.isArray(body.unsigned_group) || body.unsigned_group.length === 0 || !body.unsigned_group.every((x: unknown) => typeof x === "string")) {
      throw new HttpError(400, "invalid_request", "unsigned_group must be a non-empty array of strings");
    }
    let details: Record<string, any>;
    try {
      details = transactionDetails(decodeGroup(body.unsigned_group, address), network);
    } catch (e) {
      if (e instanceof GroupError) throw new HttpError(400, "invalid_request", e.message);
      throw e;
    }
    const finish = () => ({ status: 200, body: { signed_group: body.unsigned_group.map((g: string) => "signed:" + g), group_hash: details.group_hash } });
    if (t.kind === "consent") {
      const p = checkConsent(t, details.group_hash, "sign", network);
      consumed.add(t.authReqId!);
      userMap.get(t.sub)!.used.add(t.clientId);
      void p;
      return finish();
    }
    const user = userMap.get(t.sub)!;
    const fiat = Number(details.fiat_value.amount);
    const needs = body.requireConsent === true || fiat > (clientSettings.get(t.sub + "|" + t.clientId)?.perClientThresholdEur ?? user.thresholdEur) || user.alwaysAsk.includes(t.clientId) || !user.used.has(t.clientId);
    if (!needs) return finish();
    throw challenge(t, details, "sign", String(req.headers.origin ?? "https://" + (clientMap.get(t.clientId)?.domain ?? "client.invalid")));
  }

  function putLimits(req: IncomingMessage, body: Record<string, any>) {
    const t = bearer(req);
    requireScope(t, "manage-limits");
    const ok = body && typeof body === "object" && Object.keys(body).sort().join() === "per_day_eur,per_transaction_eur" && Object.values(body).every((v) => typeof v === "string" && /^(0|[1-9][0-9]*)(\.[0-9]+)?$/.test(v));
    if (!ok) throw new HttpError(400, "invalid_request", "body must be {per_transaction_eur, per_day_eur} decimal strings");
    const user = userMap.get(t.sub)!;
    const details = limitsDetails(user, body as Record<string, string>);
    const apply = () => {
      user.limits = { per_transaction_eur: body.per_transaction_eur, per_day_eur: body.per_day_eur };
      return { status: 200, body: { limits: user.limits } };
    };
    if (t.kind === "consent") {
      checkConsent(t, details.request_hash, "manage-limits");
      consumed.add(t.authReqId!);
      return apply();
    }
    // Tightening applies immediately; loosening is a consent-elevated action (section 12).
    if (!isLoosening(user, body as Record<string, string>) && body.requireConsent !== true) return apply();
    throw challenge(t, details, "manage-limits", String(req.headers.origin ?? "https://" + (clientMap.get(t.clientId)?.domain ?? "client.invalid")), body as Record<string, string>);
  }

  function tokenEndpoint(req: IncomingMessage, body: Record<string, any>) {
    const client = authClient(req, body);
    if (body.grant_type !== "urn:openid:params:grant-type:ciba") throw new HttpError(400, "unsupported_grant_type");
    const p = typeof body.auth_req_id === "string" ? pending.get(body.auth_req_id) : undefined;
    if (!p || p.clientId !== client.id) throw new HttpError(400, "invalid_grant", "unknown auth_req_id");
    if (p.tokenIssued) throw new HttpError(400, "invalid_grant", "auth_req_id was already redeemed");
    if (p.status === "denied") throw new HttpError(400, "access_denied", "the user denied the request");
    if (p.status === "code_failed") throw new HttpError(400, "access_denied", "the binding code was entered wrongly three times", { reason: "code_failed" });
    if (expired(p)) throw new HttpError(400, "expired_token", "the request expired");
    if (p.status === "pending") throw new HttpError(400, "authorization_pending");
    const value = "consent_" + randomBytes(24).toString("base64url");
    const exp = Math.min(now() + CONSENT_VALIDITY_SECONDS, p.request.expiry);
    tokens.set(value, { sub: p.request.sub, clientId: client.id, scope: p.scope, kind: "consent", exp, authReqId: p.request.auth_req_id });
    p.tokenIssued = true;
    const res = { access_token: value, token_type: "Bearer", expires_in: Math.max(1, exp - now()), scope: p.scope, consent_receipt: p.receipt };
    validate("token-response", res, "internal token response");
    return { status: 200, body: res };
  }

  function getReceipt(req: IncomingMessage, id: string) {
    let clientId: string;
    if (req.headers.authorization?.startsWith("Bearer ")) clientId = bearer(req).clientId;
    else clientId = authClient(req, {}).id;
    const p = pending.get(id);
    if (!p || p.clientId !== clientId) throw new HttpError(404, "not_found");
    if (!p.receipt) throw new HttpError(404, "not_found", "no receipt yet");
    return { status: 200, body: p.receipt };
  }

  function pushTarget(v: any): PushTarget {
    if (!v || (v.provider !== "fcm" && v.provider !== "apns") || typeof v.token !== "string" || !v.token || v.token.length > 4096) throw new HttpError(400, "invalid_request", "push needs provider fcm or apns and a token");
    return { provider: v.provider, token: v.token };
  }

  // A push token reaches one phone, so it belongs to one device record: registering it moves it there, and a
  // phone re-enrolled for another user stops receiving the previous user's requests.
  function setPush(d: Device, push: PushTarget | undefined) {
    if (push) for (const o of devices.values()) if (o !== d && o.push?.provider === push.provider && o.push.token === push.token) o.push = undefined;
    d.push = push;
  }

  function enroll(req: IncomingMessage, body: Record<string, any>) {
    const t = bearer(req);
    if (t.kind !== "login") throw new HttpError(403, "access_denied", "enrollment needs a fresh full login");
    const pk = body.public_key;
    if (typeof body.kid !== "string" || !body.kid || !pk || pk.kty !== "EC" || pk.crv !== "P-256" || typeof pk.x !== "string" || typeof pk.y !== "string") throw new HttpError(400, "invalid_request", "kid and an EC P-256 public_key are required");
    if (devices.has(body.kid)) throw new HttpError(409, "conflict", "kid already enrolled");
    const hadActive = activeDevices(t.sub).length > 0;
    let activeAt = now() + INACTIVE_DEVICE_SECONDS;
    if (typeof body.approved_by_kid === "string") {
      const approver = devices.get(body.approved_by_kid);
      if (!approver || approver.sub !== t.sub || approver.cancelled || approver.activeAt > now()) throw new HttpError(400, "invalid_request", "approved_by_kid is not an active device of this user");
      activeAt = now();
    }
    const push = body.push === undefined ? undefined : pushTarget(body.push);
    const d = addDevice(t.sub, body.kid, { kty: "EC", crv: "P-256", x: pk.x, y: pk.y }, activeAt);
    setPush(d, push);
    return { status: 201, body: { kid: d.kid, active: d.activeAt <= now(), active_at: d.activeAt, attestation: d.attestation, device_token: d.token, cancel_notice_sent_to_existing_devices: hadActive && activeAt > now() } };
  }

  function deviceView(p: Pending) {
    return p.request; // the consent request object, never the binding code
  }

  function deviceOwns(d: Device, id: string): Pending {
    const p = pending.get(id);
    if (!p || p.request.sub !== d.sub) throw new HttpError(404, "not_found");
    return p;
  }

  function postReceipt(req: IncomingMessage, id: string, receipt: Record<string, any>) {
    const d = deviceAuth(req);
    const p = deviceOwns(d, id);
    if (d.activeAt > now()) throw new HttpError(403, "access_denied", "this device is not active yet (rule 7)");
    validate("consent-receipt", receipt, "receipt");
    const s = receipt.signed;
    const decision = s.decision;
    if (p.status === "approved" || p.status === "denied" || (p.status === "code_failed" && p.receipt)) throw new HttpError(409, "conflict", "this request already has a decision");
    if (expired(p)) throw new HttpError(410, "expired_token", "the request expired");
    // Rule 2: code_failed is terminal; only the code_failed receipt itself is accepted afterwards.
    if (p.status === "code_failed" && decision !== "code_failed") throw new HttpError(403, "access_denied", "attempts exhausted", { reason: "code_failed" });
    // Rule 9: both signatures, and the device must be the enrolled one.
    if (receipt.device.kid !== d.kid || jcs(receipt.device.public_key) !== jcs(d.publicKey) || !verifyReceipt(receipt, jwks, { issuer, now: now() })) {
      throw new HttpError(400, "invalid_receipt", "receipt signature or attestation does not verify");
    }
    // The receipt must restate the stored request (rule 1); it never supplies the thing being consented to.
    const r = p.request;
    if (s.auth_req_id !== r.auth_req_id || s.client_entity_id !== r.client.entity_id || s.sub !== r.sub || s.expiry !== r.expiry || s.binding_message !== r.binding_message || jcs(s.authorization_details) !== jcs(r.authorization_details)) {
      throw new HttpError(400, "receipt_mismatch", "receipt does not match the stored request");
    }
    if (decision === "deny") {
      p.status = "denied";
      p.receipt = receipt;
      return { status: 200, body: { status: "denied" } };
    }
    if (decision === "code_failed") {
      if (p.status !== "code_failed") throw new HttpError(400, "invalid_receipt", "code_failed is only accepted after three wrong codes");
      p.receipt = receipt;
      return { status: 200, body: { status: "code_failed" } };
    }
    if (p.status === "code_failed") throw new HttpError(403, "access_denied", "attempts exhausted", { reason: "code_failed" });
    // Rule 2: number matching with three attempts in total.
    if (!safeEqual(s.binding_code, p.code)) {
      p.attempts += 1;
      if (p.attempts >= MAX_CODE_ATTEMPTS) {
        p.status = "code_failed";
        throw new HttpError(403, "access_denied", "the third wrong binding code ends the request", { reason: "code_failed", attempts_remaining: 0 });
      }
      throw new HttpError(400, "wrong_code", "the binding code does not match", { attempts_remaining: MAX_CODE_ATTEMPTS - p.attempts });
    }
    if (!verifyApproval(receipt, jwks, r, p.code, consumed, { issuer, now: now() })) throw new HttpError(400, "invalid_receipt", "approval does not verify");
    p.status = "approved";
    p.receipt = receipt;
    return { status: 200, body: { status: "approved" } };
  }

  // --- Device list and consent settings (T-084) ------------------------------------------------------------------

  const acct = (sub: string) => {
    let a = accountSettings.get(sub);
    if (!a) accountSettings.set(sub, (a = { biometricRequired: true, blockUnverified: false }));
    return a;
  };
  const cs = (sub: string, clientId: string) => {
    let c = clientSettings.get(sub + "|" + clientId);
    if (!c) clientSettings.set(sub + "|" + clientId, (c = {}));
    return c;
  };
  const money = (v: number) => v.toFixed(2);

  function settingsView(sub: string) {
    const user = userMap.get(sub)!;
    return {
      sub,
      biometric_required: acct(sub).biometricRequired,
      threshold_eur: money(user.thresholdEur),
      block_unverified_clients: acct(sub).blockUnverified,
      clients: clients.map((c) => {
        const s = cs(sub, c.id);
        const paused = s.pausedUntil !== undefined && s.pausedUntil > now();
        return {
          client_id: c.id,
          verified: c.verified,
          label: c.verified ? c.displayName : "Unverified application " + c.fingerprint,
          always_ask: user.alwaysAsk.includes(c.id),
          per_client_threshold_eur: s.perClientThresholdEur === undefined ? null : money(s.perClientThresholdEur),
          paused_until: paused ? s.pausedUntil : null,
        };
      }),
    };
  }

  const isDecimal = (v: unknown) => typeof v === "string" && /^(0|[1-9][0-9]*)(\.[0-9]+)?$/.test(v);

  /** Validates the change shape and reports whether it loosens anything (raise a threshold, drop a requirement, lift a pause). */
  function classifyChange(sub: string, change: any): { loosening: boolean } {
    const user = userMap.get(sub)!;
    const bad = (m: string) => new HttpError(400, "invalid_request", m);
    if (!change || typeof change !== "object" || Array.isArray(change)) throw bad("change must be an object");
    const allowed = new Set(["threshold_eur", "biometric_required", "block_unverified_clients", "clients"]);
    for (const k of Object.keys(change)) if (!allowed.has(k)) throw bad("unknown setting " + k);
    if (Object.keys(change).length === 0) throw bad("change is empty");
    let loosening = false;
    if ("threshold_eur" in change) {
      if (!isDecimal(change.threshold_eur)) throw bad("threshold_eur must be a decimal string");
      if (Number(change.threshold_eur) > user.thresholdEur) loosening = true;
    }
    for (const k of ["biometric_required", "block_unverified_clients"]) {
      if (k in change && typeof change[k] !== "boolean") throw bad(k + " must be a boolean");
    }
    if (change.biometric_required === false && acct(sub).biometricRequired) loosening = true;
    if (change.block_unverified_clients === false && acct(sub).blockUnverified) loosening = true;
    if ("clients" in change) {
      if (!change.clients || typeof change.clients !== "object" || Array.isArray(change.clients)) throw bad("clients must be an object");
      for (const [id, c] of Object.entries<any>(change.clients)) {
        if (!clientMap.has(id)) throw bad("unknown client " + id);
        if (!c || typeof c !== "object" || Object.keys(c).length === 0) throw bad("empty change for " + id);
        for (const k of Object.keys(c)) if (!["always_ask", "per_client_threshold_eur", "paused"].includes(k)) throw bad("unknown client setting " + k);
        if ("always_ask" in c && typeof c.always_ask !== "boolean") throw bad("always_ask must be a boolean");
        if ("paused" in c && c.paused !== false) throw bad("a client can only be unpaused here");
        if ("per_client_threshold_eur" in c && c.per_client_threshold_eur !== null && !isDecimal(c.per_client_threshold_eur)) throw bad("per_client_threshold_eur must be a decimal string or null");
        const s = cs(sub, id);
        if (c.always_ask === false && user.alwaysAsk.includes(id)) loosening = true;
        if (c.paused === false && s.pausedUntil !== undefined && s.pausedUntil > now()) loosening = true;
        if ("per_client_threshold_eur" in c) {
          const next = c.per_client_threshold_eur === null ? user.thresholdEur : Number(c.per_client_threshold_eur);
          if (next > (s.perClientThresholdEur ?? user.thresholdEur)) loosening = true;
        }
      }
    }
    return { loosening };
  }

  function applyChange(sub: string, change: any) {
    const user = userMap.get(sub)!;
    if ("threshold_eur" in change) user.thresholdEur = Number(change.threshold_eur);
    if ("biometric_required" in change) acct(sub).biometricRequired = change.biometric_required;
    if ("block_unverified_clients" in change) acct(sub).blockUnverified = change.block_unverified_clients;
    for (const [id, c] of Object.entries<any>(change.clients ?? {})) {
      const s = cs(sub, id);
      if ("always_ask" in c) user.alwaysAsk = c.always_ask ? [...new Set([...user.alwaysAsk, id])] : user.alwaysAsk.filter((x) => x !== id);
      if ("per_client_threshold_eur" in c) s.perClientThresholdEur = c.per_client_threshold_eur === null ? undefined : Number(c.per_client_threshold_eur);
      if (c.paused === false) s.pausedUntil = undefined;
    }
  }

  function putSettings(req: IncomingMessage, body: Record<string, any>) {
    const d = deviceAuth(req);
    const { loosening } = classifyChange(d.sub, body.change);
    if (loosening) {
      // Loosening is a consent from an active device: a signed biatec_settings_change (required changes, section 19).
      if (d.activeAt > now()) throw new HttpError(403, "access_denied", "this device is not active yet (rule 7)");
      const c = body.consent;
      const refuse = (m: string) => new HttpError(403, "insufficient_user_authentication", m, { acr_values: "biatec:consent", reason: "consent_required" });
      if (!c || typeof c !== "object" || !c.signed || typeof c.signature !== "string") throw refuse("loosening needs a device-signed biatec_settings_change");
      const s = c.signed;
      if (s.type !== "biatec_settings_change" || s.sub !== d.sub || s.kid !== d.kid || typeof s.nonce !== "string" || !s.nonce || typeof s.issued_at !== "number") throw refuse("the signed change is malformed");
      if (jcs(s.change) !== jcs(body.change)) throw refuse("the signed change differs from the requested one");
      if (Math.abs(now() - s.issued_at) > CONSENT_VALIDITY_SECONDS) throw refuse("the signed change expired");
      let ok = false;
      try {
        ok = verifyEs256(d.publicKey, Buffer.from(jcs(s), "utf8"), c.signature);
      } catch {
        ok = false;
      }
      if (!ok) throw refuse("the signature does not verify");
      if (settingsNonces.has(d.kid + "|" + s.nonce)) throw refuse("this signed change was already used");
      settingsNonces.add(d.kid + "|" + s.nonce);
    }
    applyChange(d.sub, body.change);
    return { status: 200, body: { applied: true, loosened: loosening, settings: settingsView(d.sub) } };
  }

  function listDevices(req: IncomingMessage) {
    const me = deviceAuth(req);
    const list = [...devices.values()].filter((x) => x.sub === me.sub && !x.cancelled).map((x) => ({ kid: x.kid, current: x.kid === me.kid, active: x.activeAt <= now(), active_at: x.activeAt }));
    return { status: 200, body: { devices: list } };
  }

  function cancelDevice(req: IncomingMessage, kid: string) {
    const me = deviceAuth(req);
    const target = devices.get(kid);
    if (!target || target.sub !== me.sub || target.cancelled) throw new HttpError(404, "not_found");
    if (target.activeAt <= now()) throw new HttpError(409, "conflict", "only a pending enrollment can be cancelled");
    target.cancelled = true;
    target.push = undefined;
    return { status: 200, body: { kid, cancelled: true } };
  }

  // --- Routing --------------------------------------------------------------------------------------------------

  async function readBody(req: IncomingMessage): Promise<Record<string, any>> {
    const chunks: Buffer[] = [];
    let size = 0;
    for await (const c of req) {
      size += (c as Buffer).length;
      if (size > 1_000_000) throw new HttpError(413, "invalid_request", "body too large");
      chunks.push(c as Buffer);
    }
    const text = Buffer.concat(chunks).toString("utf8");
    if (!text) return {};
    const type = String(req.headers["content-type"] ?? "");
    try {
      if (type.includes("application/x-www-form-urlencoded")) return parseForm(text);
      const v = JSON.parse(text);
      return v;
    } catch {
      throw new HttpError(400, "invalid_request", "malformed body");
    }
  }

  async function route(req: IncomingMessage): Promise<{ status: number; body: unknown }> {
    const url = new URL(req.url ?? "/", "http://mock");
    const path = url.pathname;
    const m = req.method ?? "GET";
    let x: RegExpMatchArray | null;
    if (m === "GET" && path === "/.well-known/jwks.json") return { status: 200, body: jwks };
    if (m === "GET" && path === "/.well-known/openid-configuration") {
      return { status: 200, body: { issuer, jwks_uri: "/.well-known/jwks.json", token_endpoint: "/token", backchannel_authentication_endpoint: "/connect/ciba", backchannel_token_delivery_modes_supported: ["poll"], grant_types_supported: ["urn:openid:params:grant-type:ciba"], acr_values_supported: ["biatec:consent"], authorization_details_types_supported: ["biatec_transaction", "biatec_limits_change"] } };
    }
    if (m === "POST" && path === "/connect/ciba") return ciba(req, await readBody(req));
    if (m === "POST" && path === "/token") return tokenEndpoint(req, await readBody(req));
    if (m === "PUT" && path === "/wallet/limits") return putLimits(req, await readBody(req));
    if (m === "POST" && (x = path.match(/^\/wallet\/([^/]+)\/([^/]+)\/sign$/))) return sign(req, { network: x[1], address: x[2] }, await readBody(req));
    if (m === "GET" && (x = path.match(/^\/consent\/receipts\/([^/]+)$/))) return getReceipt(req, x[1]);
    if (m === "POST" && path === "/devices") return enroll(req, await readBody(req));
    // Rule 7: the recovery code exists only after the device is active, never in the enrollment response.
    if (m === "POST" && path === "/devices/recovery-code") {
      const d = deviceAuth(req);
      if (d.activeAt > now()) throw new HttpError(403, "access_denied", "the recovery code is issued only once the device is active (rule 7)", { reason: "device_inactive" });
      if (d.recoveryAcknowledged) throw new HttpError(409, "conflict", "the recovery code was already shown");
      d.recoveryCode ??= randomBytes(6).toString("hex").toUpperCase().replace(/(.{4})(?=.)/g, "$1-");
      return { status: 200, body: { recovery_code: d.recoveryCode } };
    }
    if (m === "POST" && path === "/devices/recovery-code/ack") {
      const d = deviceAuth(req);
      if (d.activeAt > now() || !d.recoveryCode) throw new HttpError(409, "conflict", "no recovery code is pending");
      d.recoveryAcknowledged = true;
      d.recoveryCode = undefined;
      return { status: 200, body: { acknowledged: true } };
    }
    if (m === "PUT" && path === "/devices/push") {
      const d = deviceAuth(req);
      setPush(d, pushTarget(await readBody(req)));
      return { status: 200, body: { kid: d.kid, provider: d.push!.provider } };
    }
    if (m === "GET" && path === "/devices") return listDevices(req);
    if (m === "POST" && (x = path.match(/^\/devices\/([^/]+)\/cancel$/))) return cancelDevice(req, decodeURIComponent(x[1]));
    if (m === "GET" && path === "/devices/settings") return { status: 200, body: settingsView(deviceAuth(req).sub) };
    if (m === "PUT" && path === "/devices/settings") return putSettings(req, await readBody(req));
    if (m === "GET" && path === "/devices/requests") {
      const d = deviceAuth(req);
      return { status: 200, body: { requests: [...pending.values()].filter((p) => p.request.sub === d.sub && p.status === "pending" && !expired(p)).map(deviceView) } };
    }
    if (m === "GET" && (x = path.match(/^\/consent\/requests\/([^/]+)$/))) {
      const p = deviceOwns(deviceAuth(req), x[1]);
      return { status: 200, body: deviceView(p) };
    }
    if (m === "POST" && (x = path.match(/^\/consent\/requests\/([^/]+)\/receipt$/))) return postReceipt(req, x[1], await readBody(req));
    // Test helpers
    if (m === "GET" && (x = path.match(/^\/mock\/devices\/([^/]+)$/))) {
      const d = devices.get(x[1]);
      if (!d) throw new HttpError(404, "not_found");
      return { status: 200, body: { kid: d.kid, sub: d.sub, active: d.activeAt <= now(), active_at: d.activeAt, attestation: d.attestation, device_token: d.token, public_key: d.publicKey } };
    }
    // Test helper: the 24 hour client pause (rule 10) that the user lifts from the trusted-clients screen.
    if (m === "POST" && (x = path.match(/^\/mock\/users\/([^/]+)\/clients\/([^/]+)\/pause$/))) {
      if (!userMap.has(x[1]) || !clientMap.has(x[2])) throw new HttpError(404, "not_found");
      const s = cs(x[1], x[2]);
      s.pausedUntil = now() + 24 * 3600;
      return { status: 200, body: { paused_until: s.pausedUntil } };
    }
    if (m === "GET" && path === "/mock/push-outbox") {
      const kid = url.searchParams.get("kid");
      return { status: 200, body: { messages: pushOutbox.filter((p) => !kid || p.kid === kid) } };
    }
    if (m === "POST" && (x = path.match(/^\/mock\/devices\/([^/]+)\/activate$/))) {
      const d = devices.get(x[1]);
      if (!d) throw new HttpError(404, "not_found");
      d.activeAt = now();
      return { status: 200, body: { kid: d.kid, active: true } };
    }
    throw new HttpError(404, "not_found");
  }

  const server: Server = createServer((req, res: ServerResponse) => {
    route(req).then(
      (r) => send(res, r.status, r.body, {}),
      (e) => {
        if (e instanceof HttpError) send(res, e.status, e.body, e.headers);
        else {
          console.error(e);
          send(res, 500, { error: "server_error" }, {});
        }
      },
    );
  });
  function send(res: ServerResponse, status: number, body: unknown, headers: Record<string, string>) {
    res.writeHead(status, { "Content-Type": "application/json", "Cache-Control": "no-store", ...headers });
    res.end(JSON.stringify(body));
  }

  return { server, issuer, jwks, keys, state: { pending, consumed, devices, users: userMap, tokens, pushOutbox } };
}
