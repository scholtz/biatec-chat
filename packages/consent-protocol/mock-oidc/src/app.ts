// Mock Biatec OIDC consent server. It implements only what the consent protocol needs:
//   POST /connect/ciba                        direct entry (backchannel authentication)
//   POST /wallet/:network/:address/sign       step-up entry (RFC 9470 challenge) and signing with a consent token
//   PUT  /wallet/limits                       step-up entry for loosening limits
//   POST /token                               CIBA token grant, returns consent_receipt
//   GET  /consent/receipts/:auth_req_id       receipt retrieval by the owning client
//   POST /devices                             device enrollment (rule 7), optionally with a platform attestation
//   POST /devices/challenge, PUT /devices/attestation   single-use nonce for a platform attestation, and its refresh (T-234)
//   PUT  /devices/push                        register or replace the device's APNs token (fcm: 400 unsupported_push_provider, T-244)
//   POST /devices/hub-ticket, WS /hubs/device  the device channel (SignalR JSON hub protocol, src/device-hub.ts, T-244)
//   GET  /devices/requests, GET /consent/requests/:id, POST /consent/requests/:id/receipt   authenticator side
//   GET  /consent/settings                    the calling client's view of the user's consent settings (bearer; row 20)
//   GET  /.well-known/jwks.json, /.well-known/openid-configuration
//   GET /devices, POST /devices/:kid/cancel   device list; cancelling a pending enrollment (rule 7, T-084)
//   GET/PUT /devices/settings                 consent settings; loosening needs a device-signed biatec_settings_change (T-084)
//   GET/POST /mock/...                        test helpers (activate a device, inspect state, push outbox)
// Protocol 1.1: unsigned groups are real Algorand transactions (algosdk), the group id is recomputed, authorization_details lists every
// transfer and application, receipts are 1.1 (client_fingerprint, two device keys with attested key use). Refusals as AlgorandGoogleDriveAccount
// issue 28: consent_unavailable, client_paused, unproven_client_blocked, daily_cap_exceeded, consent_unsupported_operation,
// consent_unsupported_network, consent_hash_mismatch, too_many_pending_requests, rate_limited. A consent is reserved, then spent.
// Push: opening a request queues one message per active device with a push token; its data is only the
// auth_req_id (GET /mock/push-outbox shows what would be sent). Nothing is sent to FCM or APNs.
// Rules enforced: 2 (binding code, 3 attempts), 4 (first use), 5 (300 s, single use, group-hash binding),
// 7 (24 h inactive first device), 8 (no device, no above-threshold actions), 9 (offline-verifiable receipts).
import { createDeviceHub } from "./device-hub.ts";
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import { createHash, randomBytes, randomInt, timingSafeEqual } from "node:crypto";
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020Module from "ajv/dist/2020.js";
import { jcs, sha256Hex, signCompactJws, thumbprint, verifyApproval, verifyEnrollmentProof, verifyEs256, verifyReceipt, ATTESTATION_TYP } from "../../scripts/lib.mjs";
import { GroupError, decodeGroup, decimalToCents, describeAlgorandGroup, describeJsonGroup, mockSigned, transactionDetails, type Described } from "./group.ts";
import { clients, staticTokens, users, type SeedClient, type SeedUser } from "./seed.ts";

const Ajv2020 = Ajv2020Module as unknown as typeof Ajv2020Module.default;
const pkgRoot = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const loadJson = (...p: string[]) => JSON.parse(readFileSync(join(pkgRoot, ...p), "utf8"));

export const CONSENT_VALIDITY_SECONDS = 300;
export const MAX_CODE_ATTEMPTS = 3;
export const INACTIVE_DEVICE_SECONDS = 24 * 3600;
/** Seconds a client must wait between token polls of one request (the `interval` the challenge announces). */
export const POLL_INTERVAL_SECONDS = 2;
/** RFC 8628 3.5 and CIBA 10.2: a client that polls too fast is told `slow_down` and must add this to its interval. */
export const SLOW_DOWN_STEP_SECONDS = 5;
/** Open (pending, unexpired) requests one user can have with one client from one origin at a time (rule 10); a further one is refused. */
export const MAX_PENDING_PER_CLIENT_ORIGIN = 3;
/** New requests one client may open for one user per hour: an anti-spam limit only, with no per-second burst limit (rule 10). */
export const MAX_REQUESTS_PER_HOUR = 60;
/** Reduced daily cap of a user with no active device during the first 24 hours of the client or of the user's use of it (rule 4), in EUR. */
export const NO_DEVICE_DAILY_CAP_EUR = 100;
/** A new client asks the phone for every request for this long after it registered (rule 4). */
export const COOLING_OFF_SECONDS = 24 * 3600;
/** The threshold of an account that never set one. */
export const DEFAULT_THRESHOLD_EUR = 1000;
/** `maxConsentSeconds` below this is refused (required-changes row 16). */
export const MIN_CONSENT_SECONDS = 30;
/** Push registrations one device may make per minute (`PUT /devices/push`): a stolen token cannot keep re-pointing the notifications. */
export const MAX_PUSH_REGISTRATIONS_PER_MINUTE = 6;
/** How far a device proof's `iat` may be from the clock, and so how long its `jti` is remembered. */
export const DEVICE_PROOF_WINDOW_SECONDS = 60;
export const DEVICE_PROOF_TYP = "biatec-device-proof+jwt";
export const ISSUER = "https://oidc.biatec.test";

export interface MockOptions {
  /** Unix seconds. Defaults to the wall clock. */
  now?: () => number;
  issuer?: string;
  /** Binding code generator, for deterministic tests. */
  newBindingCode?: () => string;
  /** Overrides vectors/keys.json (the test-only Biatec OIDC signing key). */
  oidcKey?: Record<string, string>;
  /**
   * Serves the /mock/* test helpers and gives the seeded devices the predictable tokens `device-token-<user>` that tests rely on.
   * Off by default: anyone who can reach a mock with helpers on can approve consent as a seeded user (BCHAT-006). Tests that embed
   * the mock pass `testHelpers: true`; `server.ts` turns it on only for MOCK_TEST_HELPERS=1.
   */
  testHelpers?: boolean;
  /**
   * The lowest authenticator build number the mock accepts, sent on every answer as `X-Biatec-Min-App-Build` (required-changes row 24). An
   * authenticator older than this opens no secure area and asks the user to update. Defaults to 1 (every build).
   */
  minAppBuild?: number;
  /**
   * Every device call must carry `X-Device-Proof`: a compact ES256 JWS, signed by the device key, with `typ` biatec-device-proof+jwt, `kid` of the
   * device and the claims htm (method), htu (URL), iat, jti (single use) and ath (base64url SHA-256 of the device token). The bearer
   * `Authorization: Device <token>` alone is then not enough (BCHAT-030). Off by default; `server.ts` turns it on with MOCK_DEVICE_PROOF=1.
   */
  deviceProof?: boolean;
  /**
   * Two device keys, enforced (D9, T-241): `POST /devices` needs `presence_kid`, `presence_public_key`, `approval_proof` (the enrollment proof signed
   * by the new approval key, carrying the nonce of a challenge from `POST /devices/challenge`) and `presence_proof` (the same payload signed by the
   * presence key), verified with `verifyEnrollmentProof`; the device proof (`X-Device-Proof`, when `deviceProof` is on) must be signed by the
   * PRESENCE key, and one signed by the approval key is refused. An approve receipt signed by the presence key is refused with or without this flag
   * (attested `biatec_key_use`). Off by default so the other tests keep their single-key enrollments; `server.ts` turns it on with MOCK_TWO_KEYS=1.
   */
  twoKeys?: boolean;
  /** Unsigned groups are base64(JSON) descriptors instead of Algorand transactions (src/group.ts). For the e2e harness only; MOCK_JSON_GROUPS=1 in `server.ts`. */
  jsonGroups?: boolean;
  /** Pending requests per user, client and origin (default 3, rule 10). */
  maxPendingPerClientOrigin?: number;
  /** New requests per user and client per hour (default 60, rule 10). */
  maxRequestsPerHour?: number;
  /** A device channel connection is closed (allowReconnect true) after this many seconds (default 1800; MOCK_HUB_LIFETIME_SECONDS in `server.ts`). */
  hubLifetimeSeconds?: number;
  /** Seconds between pings the channel sends (default 15). */
  hubPingSeconds?: number;
}

/** A device has an approval key (its `kid`, `publicKey`, `attestation`) and optionally a presence key; each is attested for its own use. */
interface PresenceKey { kid: string; publicKey: Record<string, string>; attestation: string }
interface Device { kid: string; sub: string; publicKey: Record<string, string>; token: string; activeAt: number; cancelled: boolean; attestation: string; presence?: PresenceKey; recoveryCode?: string; recoveryAcknowledged?: boolean; push?: PushTarget; platformAttestation?: { kind: string; attestedAt: number } }
interface PushTarget { provider: "apns"; token: string }
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
  /** Seconds between token polls; raised by every slow_down. */
  interval: number;
  lastPollAt?: number;
  scope: string;
  /** For limits requests: the body to apply once Biatec OIDC accepts the consent. */
  limitsBody?: Record<string, string>;
  /** Reserve then spend (issue 28): a consent is reserved before signing, spent when signing succeeded, released on a handled failure. */
  spend: "none" | "reserved" | "spent";
}
interface Token { sub: string; clientId: string; scope: string; kind: "access" | "login" | "consent"; exp?: number; authReqId?: string; /** The group or body hash a consent token is bound to. */ consentHash?: string }

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

/** How long an attestation challenge lives, and how long an attestation stays good before the app refreshes it (seconds). */
export const ATTESTATION_CHALLENGE_SECONDS = 300;
export const ATTESTATION_REFRESH_SECONDS = 30 * 24 * 3600;

export function createMockOidc(options: MockOptions = {}) {
  const now = options.now ?? (() => Math.floor(Date.now() / 1000));
  const issuer = options.issuer ?? ISSUER;
  const testHelpers = options.testHelpers ?? false;
  const jsonGroups = options.jsonGroups ?? false;
  const maxPending = options.maxPendingPerClientOrigin ?? MAX_PENDING_PER_CLIENT_ORIGIN;
  const maxPerHour = options.maxRequestsPerHour ?? MAX_REQUESTS_PER_HOUR;
  const minAppBuild = options.minAppBuild ?? 1;
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
  // Single-use nonces for the platform attestation (required-changes row 25): who asked, and until when.
  const attestationChallenges = new Map<string, { sub: string; expires: number }>();
  const pending = new Map<string, Pending>();
  const consumed = new Set<string>();
  const pushOutbox: PushMessage[] = [];
  const hub = createDeviceHub({ now, lifetimeSeconds: options.hubLifetimeSeconds, pingSeconds: options.hubPingSeconds });
  const closedAnnounced = new Set<string>();
  /** Tells the connected devices of the user that a request is no longer open (once per request). */
  function announceClosed(p: Pending) {
    if (closedAnnounced.has(p.request.auth_req_id)) return;
    closedAnnounced.add(p.request.auth_req_id);
    hub.notify(p.request.sub, "consent_closed", { auth_req_id: p.request.auth_req_id });
  }
  // Consent settings beyond what the seed holds (T-084). Keys: sub, or sub|clientId.
  const accountSettings = new Map<string, { biometricRequired: boolean; blockUnverified: boolean; askOnTestNetworks: boolean }>();
  const clientSettings = new Map<string, { perClientThresholdEur?: number; pausedUntil?: number }>();
  const settingsNonces = new Set<string>();
  // The user's own trust, kept per user and client: applications trusted by approving with `remember` (key: `network:app_id`).
  const trustedApplications = new Map<string, Set<string>>();
  // When the user first got a signature from a client (rule 4's first day with the client; 0 for a seeded client used long ago), and what
  // a user with no active device spent in the last 24 hours (the reduced cap). Keys: sub|clientId.
  const firstSigned = new Map<string, number>();
  const noDeviceSpends = new Map<string, { at: number; cents: bigint }[]>();
  // When each request was opened, per sub|clientId, for the hourly rate limit.
  const openedAt = new Map<string, number[]>();
  const registeredAt = (c: SeedClient) => now() - (c.registeredHoursAgo === undefined ? 10 * 365 * 86400 : c.registeredHoursAgo * 3600);

  /** The attestation of a device key, minted by Biatec OIDC for `sub`: it binds the key and its use (`approve` or `presence`). */
  const attest = (sub: string, kid: string, pub: Record<string, string>, use: "approve" | "presence") =>
    signCompactJws(oidcKey, { alg: "ES256", kid: oidcKey.kid, typ: ATTESTATION_TYP }, {
      iss: issuer, sub, device_kid: kid, device_jwk_thumbprint: thumbprint(pub as never), biatec_key_use: use, iat: now(), exp: now() + 365 * 86400,
    });
  const addDevice = (sub: string, kid: string, publicKey: Record<string, string>, activeAt: number, token = randomBytes(24).toString("base64url"), presence?: { kid: string; publicKey: Record<string, string> }) => {
    const d: Device = { kid, sub, publicKey, token, activeAt, cancelled: false, attestation: attest(sub, kid, publicKey, "approve") };
    if (presence) d.presence = { ...presence, attestation: attest(sub, presence.kid, presence.publicKey, "presence") };
    devices.set(kid, d);
    return d;
  };
  // Seeded active devices use the vector keys (the approval key `device`, the `presence` key), so receipts signed with vectors/keys.json verify.
  const publicOf = (k: Record<string, string>) => {
    const { d: _d1, kid: _k1, alg: _a1, use: _u1, ...pub } = k;
    return pub;
  };
  const devicePub = publicOf(keys.device);
  const presencePub = publicOf(keys.presence);
  for (const u of users) {
    if (!u.activeDevice) continue;
    const name = u.email.split("@")[0];
    addDevice(u.sub, name + "-device", devicePub, 0, testHelpers ? "device-token-" + name : undefined, { kid: name + "-device-presence", publicKey: presencePub });
  }
  for (const u of users) {
    for (const c of u.usedClients) firstSigned.set(u.sub + "|" + c, 0);
    for (const c of u.pausedClients ?? []) clientSettings.set(u.sub + "|" + c, { pausedUntil: now() + 10 * 365 * 86400 });
    if (u.blockUnprovenClients) accountSettings.set(u.sub, { biometricRequired: true, blockUnverified: true, askOnTestNetworks: true });
  }

  const activeDevices = (sub: string) => [...devices.values()].filter((d) => d.sub === sub && !d.cancelled && d.activeAt <= now());
  /** The device that owns a key (its approval kid or its presence kid), whatever its state. */
  const deviceOfKey = (kid: string) => [...devices.values()].find((d) => d.kid === kid || d.presence?.kid === kid);
  const isActive = (d: Device | undefined) => d !== undefined && !d.cancelled && d.activeAt <= now();

  // Helpers
  /** First eight bytes of the SHA-256 of the client_id, 16 lowercase hex characters: what the user sees (and a receipt signs) for a client. */
  const fingerprintOf = (clientId: string) => createHash("sha256").update(clientId, "utf8").digest("hex").slice(0, 16);
  /** Biatec OIDC vouches for no client: the fingerprint, the proven domain when there is one, and whether this user linked the client. */
  const clientInfo = (c: SeedClient, user: { used: Set<string> }) => ({
    fingerprint: fingerprintOf(c.id),
    domain_proven: c.verified,
    ...(c.verified && c.domain ? { domain: c.domain } : {}),
    linked: user.used.has(c.id),
  });
  const ORIGIN = /^(?!https:\/\/[^/]*:443$)(?!http:\/\/[^/]*:80$)https?:\/\/[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*(:([1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5]))?$/;
  /** The origin a request reports: the body's `origin`, else the Origin header, else the client's own domain. It must be a canonical origin. */
  function requestOrigin(req: IncomingMessage, given: unknown, client: SeedClient): string {
    const o = given ?? req.headers.origin ?? "https://" + (client.domain ?? "client.invalid");
    if (typeof o !== "string" || o.length > 253 || !ORIGIN.test(o)) throw new HttpError(400, "invalid_request", "origin must be an absolute http(s) origin in canonical form (lower-case host, no path, no default port)");
    return o;
  }

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

  /** A consent token (acr biatec:consent) is accepted only by the wallet signing endpoints (`allowConsent`); everywhere else it is refused (decision D8). */
  function bearer(req: IncomingMessage, allowConsent = false): Token & { value: string } {
    const h = req.headers.authorization;
    const value = h?.startsWith("Bearer ") ? h.slice(7) : "";
    const t = tokens.get(value);
    if (!t || (t.exp !== undefined && t.exp <= now())) throw new HttpError(401, "invalid_token", "missing, unknown or expired bearer token", {}, { "WWW-Authenticate": 'Bearer error="invalid_token"' });
    if (t.kind === "consent" && !allowConsent) throw new HttpError(401, "invalid_token", "a consent token is accepted only by the wallet signing endpoints", {}, { "WWW-Authenticate": 'Bearer error="invalid_token"' });
    return { ...t, value };
  }

  const usedProofs = new Map<string, number>(); // jti -> when it may be forgotten
  const pushRegistrations = new Map<string, number[]>(); // kid -> times of the last registrations

  function badProof(why: string): never {
    throw new HttpError(401, "invalid_token", "device proof: " + why, {}, { "WWW-Authenticate": 'Device error="invalid_proof"' });
  }

  /** DPoP-style proof of possession of the device key (BCHAT-030): the bearer token alone does not authenticate a device call. */
  function checkDeviceProof(req: IncomingMessage, d: Device) {
    const raw = req.headers["x-device-proof"];
    if (typeof raw !== "string") badProof("missing");
    const parts = raw.split(".");
    if (parts.length !== 3) badProof("not a compact JWS");
    let header: any, payload: any;
    try {
      header = JSON.parse(Buffer.from(parts[0], "base64url").toString("utf8"));
      payload = JSON.parse(Buffer.from(parts[1], "base64url").toString("utf8"));
    } catch {
      badProof("not JSON");
    }
    // Two keys: the proof is the presence key's (it needs no prompt); the approval key never signs one.
    const proofKey = options.twoKeys ? d.presence : { kid: d.kid, publicKey: d.publicKey };
    if (!proofKey) badProof("this device has no presence key");
    if (header?.alg !== "ES256" || header?.typ !== DEVICE_PROOF_TYP || header?.kid !== proofKey.kid) badProof("wrong header");
    let signatureOk = false;
    try {
      signatureOk = verifyEs256(proofKey.publicKey, Buffer.from(parts[0] + "." + parts[1], "utf8"), parts[2]);
    } catch {
      signatureOk = false;
    }
    if (!signatureOk) badProof("the signature does not verify with this device's key");
    if (payload?.htm !== req.method) badProof("wrong method");
    let htu = "";
    try {
      const u = new URL(String(payload?.htu));
      htu = u.pathname + u.search;
    } catch {
      badProof("htu is not a URL");
    }
    if (htu !== req.url) badProof("wrong URL"); // the path and query the client called, exactly (scheme and host are the client's view of this server)
    if (typeof payload?.ath !== "string" || !safeEqual(payload.ath, createHash("sha256").update(d.token).digest("base64url"))) badProof("not bound to this device token");
    if (typeof payload?.iat !== "number" || Math.abs(now() - payload.iat) > DEVICE_PROOF_WINDOW_SECONDS) badProof("stale or future iat");
    if (typeof payload?.jti !== "string" || payload.jti.length < 16 || payload.jti.length > 128) badProof("jti");
    for (const [j, until] of usedProofs) if (until <= now()) usedProofs.delete(j);
    if (usedProofs.has(payload.jti)) badProof("replayed");
    usedProofs.set(payload.jti, now() + 2 * DEVICE_PROOF_WINDOW_SECONDS);
  }

  function deviceAuth(req: IncomingMessage): Device {
    const h = req.headers.authorization;
    const value = h?.startsWith("Device ") ? h.slice(7) : "";
    const d = [...devices.values()].find((x) => !x.cancelled && safeEqual(x.token, value));
    if (!d) throw new HttpError(401, "invalid_token", "unknown device token");
    if (options.deviceProof) checkDeviceProof(req, d);
    return d;
  }

  const expired = (p: Pending) => p.request.expiry <= now();

  function openRequest(opts: { entry: "step-up" | "direct"; user: SeedUser & { used: Set<string> }; client: SeedClient; details: Record<string, any>; bindingMessage: string; requestedExpiry: number; origin: string; scope: string; limitsBody?: Record<string, string> }) {
    // Rule 10: at most three pending requests per client, user and origin, and an anti-spam rate per client and user.
    const open = [...pending.values()].filter((p) => p.request.sub === opts.user.sub && p.clientId === opts.client.id && p.request.origin === opts.origin && p.status === "pending" && !p.tokenIssued && !expired(p)).length;
    if (open >= maxPending) throw new HttpError(429, "too_many_pending_requests", `at most ${maxPending} requests of one client can be open for one user from one origin`, { limit: maxPending, scope: { client: opts.client.id, origin: opts.origin } });
    const rateKey = opts.user.sub + "|" + opts.client.id;
    const recent = (openedAt.get(rateKey) ?? []).filter((t) => now() - t < 3600);
    if (recent.length >= maxPerHour) throw new HttpError(429, "rate_limited", `at most ${maxPerHour} requests per hour`, {}, { "Retry-After": String(Math.max(1, recent[0] + 3600 - now())) });
    openedAt.set(rateKey, [...recent, now()]);
    const authReqId = "ciba_" + randomBytes(16).toString("hex");
    const code = newCode();
    // Rendered, and so signed, only for a client with a proven domain and only in the direct entry (spec section 5).
    const message = opts.entry === "direct" && opts.client.verified ? opts.bindingMessage : "";
    const request = {
      auth_req_id: authReqId,
      entry: opts.entry,
      authorization_details: [opts.details],
      client: clientInfo(opts.client, opts.user),
      binding_message: message,
      requested_expiry: opts.requestedExpiry,
      expiry: now() + opts.requestedExpiry,
      bound_hash: opts.details.group_hash ?? opts.details.request_hash,
      origin: opts.origin,
      sub: opts.user.sub,
    };
    validate("consent-request", request, "internal consent request");
    pending.set(authReqId, { request, code, attempts: 0, clientId: opts.client.id, status: "pending", tokenIssued: false, scope: opts.scope, limitsBody: opts.limitsBody, interval: POLL_INTERVAL_SECONDS, spend: "none" });
    for (const d of activeDevices(opts.user.sub)) if (d.push) pushOutbox.push({ kid: d.kid, ...d.push, data: { auth_req_id: authReqId } });
    if (pushOutbox.length > 500) pushOutbox.splice(0, pushOutbox.length - 500); // a long-running mock keeps the recent ones
    hub.notify(opts.user.sub, "consent_request", { auth_req_id: authReqId });
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

  // --- The group, the user's trust and the policy ----------------------------------------------------------------------------

  /** Applications this user trusted for this client on this network (by approving with `remember`): the user's own list, never an operator's. */
  function trustedFor(sub: string, clientId: string, network: string): Set<number> {
    const out = new Set<number>();
    for (const k of trustedApplications.get(sub + "|" + clientId) ?? []) {
      const i = k.lastIndexOf(":");
      if (k.slice(0, i) === network) out.add(Number(k.slice(i + 1)));
    }
    return out;
  }

  /** Decodes and describes the group the client handed over; every refusal of the decoder becomes its HTTP answer. */
  function describeGroup(items: string[], address: string, network: string, grp: string | undefined, origin: string, sub: string, clientId: string) {
    try {
      let described: Described;
      let txns: ReturnType<typeof describeAlgorandGroup>["txns"] | undefined;
      if (jsonGroups) described = describeJsonGroup(decodeGroup(items, address), network);
      else {
        const r = describeAlgorandGroup(items, address, grp);
        described = r.described;
        txns = r.txns;
      }
      return { described, txns, ...transactionDetails(described, network, origin, trustedFor(sub, clientId, network)) };
    } catch (e) {
      if (e instanceof GroupError) throw new HttpError(e.status, e.code, e.message);
      throw e;
    }
  }

  /** Refusals that hold whatever the value: the user paused this client (rule 10), or blocks clients without a proven domain. */
  function standingRefusals(sub: string, client: SeedClient) {
    const paused = clientSettings.get(sub + "|" + client.id)?.pausedUntil;
    if (paused !== undefined && paused > now()) throw new HttpError(403, "client_paused", "the user paused this client", { paused_until: paused });
    if (acct(sub).blockUnverified && !client.verified) throw new HttpError(403, "unproven_client_blocked", "the user blocks clients that have not proven their domain");
  }

  const unsupportedNetwork = () => new HttpError(403, "consent_unsupported_network", "consent is not supported on this network");

  const thresholdOf = (user: SeedUser, clientId: string) => clientSettings.get(user.sub + "|" + clientId)?.perClientThresholdEur ?? user.thresholdEur;

  /**
   * The policy decision (issue 27, D3): refuse, sign without the phone (`allow`), or ask the phone (`consent`). It only reads.
   * A user with an active device is asked for: `requireConsent`, "always ask", a client not linked yet, a client in its first 24 hours, a value
   * above the threshold (the user's own, no ceiling, 0 asks for everything) and an unknown value. A user with no active device cannot be asked:
   * inside the client's first day the reduced cap applies (an unknown value never fits), otherwise whatever would ask is refused.
   */
  function decide(user: SeedUser & { used: Set<string> }, client: SeedClient, network: string, g: { details: Record<string, any>; valueEur: string | null }, requireConsent: boolean): "allow" | "consent" {
    standingRefusals(user.sub, client);
    const known = g.details.value_known === true && g.valueEur !== null;
    const testnet = network.endsWith("testnet");
    // On a test network the user may choose not to be asked about value (every other reason still applies).
    const overValue = !(testnet && !acct(user.sub).askOnTestNetworks) && (!known || thresholdOf(user, client.id) <= 0 || Number(g.valueEur) > thresholdOf(user, client.id)); // a threshold of 0 asks for every action, a value of 0 included
    const coolingOff = registeredAt(client) + COOLING_OFF_SECONDS > now();
    const alwaysAsk = user.alwaysAsk.includes(client.id);
    if (activeDevices(user.sub).length > 0) {
      if (!(requireConsent || alwaysAsk || !user.used.has(client.id) || coolingOff || overValue)) return "allow";
      if (network.startsWith("bitcoin")) throw unsupportedNetwork();
      return "consent";
    }
    const noDevice = () => new HttpError(403, "consent_unavailable", "phone approval is required but the user has no active enrolled device (rule 8)", { reason: "no_active_device" });
    if (requireConsent || alwaysAsk) throw noDevice();
    const key = user.sub + "|" + client.id;
    const first = firstSigned.get(key);
    if (coolingOff || first === undefined || now() < first + COOLING_OFF_SECONDS) {
      const spent = (noDeviceSpends.get(key) ?? []).filter((s) => now() - s.at < 86400).reduce((sum, s) => sum + s.cents, 0n);
      if (!known || spent + decimalToCents(g.valueEur!) > BigInt(NO_DEVICE_DAILY_CAP_EUR) * 100n) {
        throw new HttpError(403, "daily_cap_exceeded", `without an active device at most ${NO_DEVICE_DAILY_CAP_EUR} EUR a day can be signed during the first day with a client`, { cap_eur: money(NO_DEVICE_DAILY_CAP_EUR), reason: "no_active_device" });
      }
      return "allow";
    }
    if (overValue) throw noDevice();
    return "allow";
  }

  /** What a signature changes: the user's first day with the client starts, and a user with no device spends from the reduced cap. */
  function recordSigned(user: SeedUser, client: SeedClient, valueEur: string | null) {
    const key = user.sub + "|" + client.id;
    if (!firstSigned.has(key)) firstSigned.set(key, now());
    if (activeDevices(user.sub).length === 0 && valueEur !== null) noDeviceSpends.set(key, [...(noDeviceSpends.get(key) ?? []).filter((s) => now() - s.at < 86400), { at: now(), cents: decimalToCents(valueEur) }]);
  }

  // --- Handlers -------------------------------------------------------------------------------------------------------------

  function ciba(req: IncomingMessage, body: Record<string, any>) {
    const client = authClient(req, body);
    const { client_id: _a, client_secret: _b, ...params } = body;
    validate("backchannel-authentication-request", params, "backchannel authentication request");
    if (params.scope === "manage-limits" && !client.allowManageLimits) throw new HttpError(403, "unauthorized_client", "manage-limits is reserved for operator-allowlisted clients");
    const user = [...userMap.values()].find((u) => u.email === params.login_hint || u.sub === params.login_hint);
    if (!user) throw new HttpError(400, "unknown_user_id", "login_hint does not identify a user");
    const origin = requestOrigin(req, params.origin, client);
    if (activeDevices(user.sub).length === 0) throw new HttpError(403, "access_denied", "the user has no active enrolled device (rule 8)", { reason: "no_active_device" });
    standingRefusals(user.sub, client);
    let details: Record<string, any>;
    let limitsBody: Record<string, string> | undefined;
    if (params.scope === "sign") {
      if (String(params.network).startsWith("bitcoin") && !jsonGroups) throw unsupportedNetwork();
      details = describeGroup(params.unsigned_group, params.address, params.network, undefined, origin, user.sub, client.id).details;
    } else {
      limitsBody = params.limits_request;
      details = limitsDetails(user, limitsBody!);
    }
    const r = openRequest({ entry: "direct", user, client, details, bindingMessage: params.binding_message ?? "", requestedExpiry: params.requested_expiry ?? CONSENT_VALIDITY_SECONDS, origin, scope: params.scope, limitsBody });
    const res = { auth_req_id: r.authReqId, expires_in: r.expiresIn, interval: POLL_INTERVAL_SECONDS, binding_code: r.code };
    validate("backchannel-authentication-response", res, "internal response");
    return { status: 200, body: res };
  }

  function requireScope(t: Token, scope: string) {
    if (!t.scope.split(" ").includes(scope)) throw new HttpError(403, "insufficient_scope", `the token lacks the ${scope} scope`, {}, { "WWW-Authenticate": `Bearer error="insufficient_scope", scope="${scope}"` });
  }

  function challenge(t: Token, details: Record<string, any>, scope: string, origin: string, requestedExpiry: number, limitsBody?: Record<string, string>) {
    const user = userMap.get(t.sub)!;
    const client = clientMap.get(t.clientId)!;
    if (activeDevices(user.sub).length === 0) {
      throw new HttpError(403, "consent_unavailable", "phone approval is required but the user has no active enrolled device (rule 8)", { reason: "no_active_device" });
    }
    const r = openRequest({ entry: "step-up", user, client, details, bindingMessage: "", requestedExpiry, origin, scope, limitsBody });
    const body = { error: "insufficient_user_authentication", error_description: "consent required", acr_values: "biatec:consent", auth_req_id: r.authReqId, expires_in: r.expiresIn, interval: POLL_INTERVAL_SECONDS, binding_code: r.code };
    validate("stepup-challenge", body, "internal challenge");
    return new HttpError(401, body.error, body.error_description, { acr_values: body.acr_values, auth_req_id: body.auth_req_id, expires_in: body.expires_in, interval: body.interval, binding_code: body.binding_code }, {
      "WWW-Authenticate": 'Bearer error="insufficient_user_authentication", error_description="consent required", acr_values="biatec:consent"',
    });
  }

  const consentNotUsable = () =>
    new HttpError(401, "insufficient_user_authentication", "consent not usable", {}, { "WWW-Authenticate": 'Bearer error="insufficient_user_authentication", error_description="consent not usable"' });

  /**
   * The record behind a consent token, when it can still be used (issue 28, step 9): approved for this client, account and action, for this
   * network and sender when given, not expired, not reserved or spent, and its receipt verifies against the stored request. Anything else is
   * `401 consent not usable` and the client starts over with its ordinary token. Nothing is spent here: the caller reserves, signs, then spends.
   */
  function usableConsent(t: Token, scope: string, where?: { network: string; sender: string }): Pending {
    const p = t.authReqId ? pending.get(t.authReqId) : undefined;
    if (!p || p.status !== "approved" || !p.receipt || p.clientId !== t.clientId || p.request.sub !== t.sub) throw consentNotUsable();
    if (p.scope !== scope) throw new HttpError(403, "insufficient_scope", "the consent was granted for another action");
    const d = p.request.authorization_details?.[0];
    if (where && (d?.network !== where.network || d?.sender !== where.sender)) throw consentNotUsable();
    if (p.spend !== "none" || consumed.has(t.authReqId!) || p.request.expiry <= now()) throw consentNotUsable();
    const signer = deviceOfKey(p.receipt.device.kid);
    if (!verifyApproval(p.receipt, jwks, p.request, p.code, consumed, { issuer, now: now(), deviceActive: isActive(signer) })) throw consentNotUsable();
    return p;
  }

  /** Reserve, run the signing, then spend; a handled refusal releases the reservation, an unexpected failure leaves it reserved (D7). */
  function spendConsent<T>(p: Pending, run: () => T): T {
    p.spend = "reserved";
    let out: T;
    try {
      out = run();
    } catch (e) {
      if (e instanceof HttpError) p.spend = "none";
      throw e;
    }
    p.spend = "spent";
    consumed.add(p.request.auth_req_id);
    return out;
  }

  function sign(req: IncomingMessage, params: Record<string, string>, body: Record<string, any>) {
    const t = bearer(req, true);
    requireScope(t, "sign");
    const { network, address } = params;
    if (!/^(algorand|bitcoin)-(mainnet|testnet)$/.test(network)) throw new HttpError(404, "unknown_network");
    // `transactions` is the member of the real endpoint; `unsigned_group` is the name of the CIBA parameter and is accepted too.
    const items = body.transactions ?? body.unsigned_group;
    if (!Array.isArray(items) || items.length === 0 || !items.every((x: unknown) => typeof x === "string")) {
      throw new HttpError(400, "invalid_request", "transactions must be a non-empty array of strings");
    }
    if (body.grp !== undefined && typeof body.grp !== "string") throw new HttpError(400, "invalid_request", "grp must be a base64 string");
    let expirySeconds = CONSENT_VALIDITY_SECONDS;
    if (body.maxConsentSeconds !== undefined) {
      if (!Number.isInteger(body.maxConsentSeconds) || body.maxConsentSeconds < MIN_CONSENT_SECONDS) throw new HttpError(400, "invalid_request", "maxConsentSeconds too small");
      expirySeconds = Math.min(expirySeconds, body.maxConsentSeconds);
    }
    const user = userMap.get(t.sub)!;
    const client = clientMap.get(t.clientId)!;
    const origin = requestOrigin(req, body.origin, client);
    if (network.startsWith("bitcoin") && !jsonGroups) throw unsupportedNetwork(); // the mock cannot decode a PSBT
    const g = describeGroup(items, address, network, body.grp, origin, t.sub, t.clientId);
    const finish = () => {
      const signed = g.txns ? g.txns.map(mockSigned) : items.map((x: string) => "signed:" + x);
      recordSigned(user, client, g.valueEur);
      return { status: 200, body: { signedTransactions: signed, signed_group: signed, group_hash: g.described.groupHash, valuedEur: g.valueEur } };
    };
    if (t.kind === "consent") {
      // The elevated token is bound to the group it was granted for: another group is refused and the consent stays usable.
      if (t.consentHash !== g.described.groupHash) throw new HttpError(403, "consent_hash_mismatch", "elevated token is bound to a different group");
      const p = usableConsent(t, "sign", { network, sender: address });
      standingRefusals(user.sub, client);
      return spendConsent(p, finish);
    }
    if (decide(user, client, network, g, body.requireConsent === true) === "allow") return finish();
    throw challenge(t, g.details, "sign", origin, expirySeconds);
  }

  function putLimits(req: IncomingMessage, body: Record<string, any>) {
    const t = bearer(req, true);
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
      if (t.consentHash !== details.request_hash) throw new HttpError(403, "consent_hash_mismatch", "elevated token is bound to a different request body");
      return spendConsent(usableConsent(t, "manage-limits"), apply);
    }
    // Tightening applies immediately; loosening is a consent-elevated action (section 12).
    if (!isLoosening(user, body as Record<string, string>) && body.requireConsent !== true) return apply();
    throw challenge(t, details, "manage-limits", requestOrigin(req, undefined, clientMap.get(t.clientId)!), CONSENT_VALIDITY_SECONDS, body as Record<string, string>);
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
    if (p.status === "pending") {
      // Polling faster than the interval is told to slow down, and the interval grows (the poll still counts as the latest one).
      const tooFast = p.lastPollAt !== undefined && now() - p.lastPollAt < p.interval;
      p.lastPollAt = now();
      if (tooFast) {
        p.interval += SLOW_DOWN_STEP_SECONDS;
        throw new HttpError(400, "slow_down", "poll less often", { interval: p.interval });
      }
      throw new HttpError(400, "authorization_pending", undefined, { interval: p.interval }); // the current interval, so a client that missed a slow_down learns it
    }
    const value = "consent_" + randomBytes(24).toString("base64url");
    const exp = Math.min(now() + CONSENT_VALIDITY_SECONDS, p.request.expiry);
    tokens.set(value, { sub: p.request.sub, clientId: client.id, scope: p.scope, kind: "consent", exp, authReqId: p.request.auth_req_id, consentHash: p.request.bound_hash });
    p.tokenIssued = true;
    const res = { access_token: value, token_type: "Bearer", expires_in: Math.max(1, exp - now()), scope: p.scope, consent_receipt: p.receipt };
    validate("token-response", res, "internal token response");
    return { status: 200, body: res };
  }

  function getReceipt(req: IncomingMessage, id: string) {
    let clientId: string;
    let bearerSub: string | undefined;
    if (req.headers.authorization?.startsWith("Bearer ")) {
      const t = bearer(req);
      clientId = t.clientId;
      bearerSub = t.sub;
    } else clientId = authClient(req, {}).id;
    const p = pending.get(id);
    if (!p || p.clientId !== clientId) throw new HttpError(404, "not_found");
    // A bearer token belongs to a subject: a receipt is handed only to the subject it was issued for (not to any user of the same client).
    if (bearerSub !== undefined && bearerSub !== p.request.sub) throw new HttpError(404, "not_found");
    if (!p.receipt) throw new HttpError(404, "not_found", "no receipt yet");
    return { status: 200, body: p.receipt };
  }

  function pushTarget(v: any): PushTarget {
    if (v && v.provider === "fcm") throw new HttpError(400, "unsupported_push_provider", "this server does not push through Firebase; use the device channel (POST /devices/hub-ticket) or apns");
    if (!v || v.provider !== "apns" || typeof v.token !== "string" || !v.token || v.token.length > 4096) throw new HttpError(400, "invalid_request", "push needs provider apns and a token");
    return { provider: v.provider, token: v.token };
  }

  // A push token reaches one phone, so it belongs to one device record: registering it moves it there, and a
  // phone re-enrolled for another user stops receiving the previous user's requests.
  function setPush(d: Device, push: PushTarget | undefined) {
    if (push) for (const o of devices.values()) if (o !== d && o.push?.provider === push.provider && o.push.token === push.token) o.push = undefined;
    d.push = push;
  }

  /** The platform attestation sent with an enrollment or a refresh: a kind this product knows, a token, and the nonce this user was given. */
  function takePlatformAttestation(sub: string, body: Record<string, any>) {
    const a = body.platform_attestation;
    if (!a || (a.kind !== "play_integrity" && a.kind !== "app_attest") || typeof a.token !== "string" || !a.token || a.token.length > 65536 || (a.key_id !== undefined && (typeof a.key_id !== "string" || a.key_id.length > 256))) {
      throw new HttpError(400, "invalid_request", "platform_attestation needs a kind (play_integrity or app_attest) and a token");
    }
    const nonce = body.attestation_challenge;
    const c = typeof nonce === "string" ? attestationChallenges.get(nonce) : undefined;
    if (!c || c.sub !== sub || c.expires < now()) throw new HttpError(400, "invalid_request", "attestation_challenge is unknown, used, expired or another user's");
    attestationChallenges.delete(nonce); // single use, also when the rest of the request fails
    return { kind: a.kind as string, attestedAt: now() };
  }

  function attestationChallenge(req: IncomingMessage) {
    const sub = req.headers.authorization?.startsWith("Device ") ? (() => {
      const d = deviceAuth(req);
      if (d.cancelled) throw new HttpError(403, "access_denied", "this device was cancelled");
      return d.sub;
    })() : (() => {
      const t = bearer(req);
      if (t.kind !== "login") throw new HttpError(403, "access_denied", "a challenge needs a fresh full login or an enrolled device");
      return t.sub;
    })();
    for (const [n, c] of attestationChallenges) if (c.expires < now()) attestationChallenges.delete(n);
    if (attestationChallenges.size > 1000) throw new HttpError(429, "too_many_requests", "too many open challenges");
    const nonce = randomBytes(24).toString("base64url");
    attestationChallenges.set(nonce, { sub, expires: now() + ATTESTATION_CHALLENGE_SECONDS });
    return { status: 200, body: { nonce, expires_in: ATTESTATION_CHALLENGE_SECONDS } };
  }

  function putAttestation(req: IncomingMessage, body: Record<string, any>) {
    const d = deviceAuth(req);
    if (d.cancelled) throw new HttpError(403, "access_denied", "this device was cancelled");
    d.platformAttestation = takePlatformAttestation(d.sub, body);
    return { status: 200, body: { kid: d.kid, attestation_refresh_after: now() + ATTESTATION_REFRESH_SECONDS } };
  }

  function enroll(req: IncomingMessage, body: Record<string, any>) {
    const t = bearer(req);
    if (t.kind !== "login") throw new HttpError(403, "access_denied", "enrollment needs a fresh full login");
    const pk = body.public_key;
    if (typeof body.kid !== "string" || !body.kid || !pk || pk.kty !== "EC" || pk.crv !== "P-256" || typeof pk.x !== "string" || typeof pk.y !== "string") throw new HttpError(400, "invalid_request", "kid and an EC P-256 public_key are required");
    if (deviceOfKey(body.kid)) throw new HttpError(409, "conflict", "kid already enrolled");
    const hadActive = activeDevices(t.sub).length > 0;
    let activeAt = now() + INACTIVE_DEVICE_SECONDS;
    if (typeof body.approved_by_kid === "string") {
      const approver = devices.get(body.approved_by_kid);
      if (!approver || approver.sub !== t.sub || approver.cancelled || approver.activeAt > now()) throw new HttpError(400, "invalid_request", "approved_by_kid is not an active device of this user");
      activeAt = now();
    }
    const push = body.push === undefined ? undefined : pushTarget(body.push);
    // Two keys enforced: the proof of possession of both, bound to a nonce of this user's challenge. Checked before the platform attestation
    // takes the challenge (the same nonce binds both); the nonce is spent below, whatever else fails after this point.
    let enrollNonce: string | undefined;
    if (options.twoKeys) {
      const pp = body.presence_public_key;
      if (typeof body.presence_kid !== "string" || !pp || typeof body.approval_proof !== "string" || typeof body.presence_proof !== "string") {
        throw new HttpError(400, "invalid_request", "presence_kid, presence_public_key, approval_proof and presence_proof are required (two device keys)");
      }
      let nonce: unknown;
      try { nonce = JSON.parse(Buffer.from(body.approval_proof.split(".")[1] ?? "", "base64url").toString("utf8")).nonce; } catch { nonce = undefined; }
      const c = typeof nonce === "string" ? attestationChallenges.get(nonce) : undefined;
      if (!c || c.sub !== t.sub || c.expires < now()) throw new HttpError(400, "invalid_request", "the enrollment challenge in the proof is unknown, used, expired or another user's");
      enrollNonce = nonce as string;
      attestationChallenges.delete(enrollNonce); // single use, also when the proof fails
      let ok = false;
      try {
        ok = verifyEnrollmentProof(body.approval_proof, {
          approval_public_key: { kty: "EC", crv: "P-256", x: pk.x, y: pk.y }, kid: body.kid, presence_kid: body.presence_kid,
          presence_public_key: { kty: "EC", crv: "P-256", x: pp.x, y: pp.y }, presence_proof: body.presence_proof, nonce: enrollNonce, usedNonces: new Set(), now: now(),
        });
      } catch { ok = false; }
      if (!ok) throw new HttpError(400, "invalid_proof", "the enrollment proof does not verify (approval key, presence key, nonce and iat)");
      // The platform attestation, when sent, is bound to the same nonce: put it back for takePlatformAttestation.
      attestationChallenges.set(enrollNonce, c);
    }
    // Without a platform attestation the device is enrolled and flagged; with a malformed or replayed one it is refused.
    // The two fields go together: one alone is refused (an orphan challenge would otherwise be ignored and left unspent).
    if ((body.platform_attestation === undefined) !== (body.attestation_challenge === undefined)) throw new HttpError(400, "invalid_request", "attestation_challenge and platform_attestation are sent together");
    const platformAttestation = body.platform_attestation === undefined ? undefined : takePlatformAttestation(t.sub, body);
    // A device with a second, presence key sends it with its kid; its own attestation then says `presence`. (The enrollment proof, the pairing
    // code and activation by another device are T-242's; the mock only models the keys and their attested use.)
    let presence: { kid: string; publicKey: Record<string, string> } | undefined;
    if (body.presence_kid !== undefined || body.presence_public_key !== undefined) {
      const pp = body.presence_public_key;
      if (typeof body.presence_kid !== "string" || !body.presence_kid || body.presence_kid === body.kid || !pp || pp.kty !== "EC" || pp.crv !== "P-256" || typeof pp.x !== "string" || typeof pp.y !== "string") throw new HttpError(400, "invalid_request", "presence_kid and an EC P-256 presence_public_key go together and name another key");
      if (deviceOfKey(body.presence_kid)) throw new HttpError(409, "conflict", "presence_kid already enrolled");
      presence = { kid: body.presence_kid, publicKey: { kty: "EC", crv: "P-256", x: pp.x, y: pp.y } };
    }
    if (enrollNonce) attestationChallenges.delete(enrollNonce);
    const d = addDevice(t.sub, body.kid, { kty: "EC", crv: "P-256", x: pk.x, y: pk.y }, activeAt, undefined, presence);
    setPush(d, push);
    hub.notify(d.sub, "device_enrolled", { kid: d.kid });
    d.platformAttestation = platformAttestation;
    return { status: 201, body: { kid: d.kid, active: d.activeAt <= now(), active_at: d.activeAt, attestation: d.attestation, ...(d.presence ? { presence_kid: d.presence.kid, presence_attestation: d.presence.attestation } : {}), device_token: d.token, cancel_notice_sent_to_existing_devices: hadActive && activeAt > now(), ...(platformAttestation ? { attestation_refresh_after: now() + ATTESTATION_REFRESH_SECONDS } : {}) } };
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
    if (receipt.version !== "1.1") throw new HttpError(400, "invalid_receipt", "a protocol 1.1 receipt is required (version \"1.1\", client_fingerprint, device.key_use)");
    const s = receipt.signed;
    const decision = s.decision;
    if (p.status === "approved" || p.status === "denied" || (p.status === "code_failed" && p.receipt)) throw new HttpError(409, "conflict", "this request already has a decision");
    if (expired(p)) throw new HttpError(410, "expired_token", "the request expired");
    // Rule 2: code_failed is terminal; only the code_failed receipt itself is accepted afterwards.
    if (p.status === "code_failed" && decision !== "code_failed") throw new HttpError(403, "access_denied", "attempts exhausted", { reason: "code_failed" });
    // Rule 9: both signatures, and the key must be one the device enrolled (its approval key or its presence key); the attestation says which use.
    const key = receipt.device.kid === d.kid ? { kid: d.kid, publicKey: d.publicKey } : d.presence && receipt.device.kid === d.presence.kid ? d.presence : undefined;
    if (!key || jcs(receipt.device.public_key) !== jcs(key.publicKey) || !verifyReceipt(receipt, jwks, { issuer, now: now() })) {
      throw new HttpError(400, "invalid_receipt", "receipt signature or attestation does not verify");
    }
    // The receipt must restate the stored request (rule 1); it never supplies the thing being consented to.
    const r = p.request;
    if (s.auth_req_id !== r.auth_req_id || s.client_fingerprint !== r.client.fingerprint || s.sub !== r.sub || s.expiry !== r.expiry || s.binding_message !== r.binding_message || jcs(s.authorization_details) !== jcs(r.authorization_details)) {
      throw new HttpError(400, "receipt_mismatch", "receipt does not match the stored request");
    }
    if (decision === "deny") {
      p.status = "denied";
      p.receipt = receipt;
      announceClosed(p);
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
        announceClosed(p);
        throw new HttpError(403, "access_denied", "the third wrong binding code ends the request", { reason: "code_failed", attempts_remaining: 0 });
      }
      throw new HttpError(400, "wrong_code", "the binding code does not match", { attempts_remaining: MAX_CODE_ATTEMPTS - p.attempts });
    }
    if (!verifyApproval(receipt, jwks, r, p.code, consumed, { issuer, now: now(), deviceActive: isActive(d) })) throw new HttpError(400, "invalid_receipt", "approval does not verify");
    p.status = "approved";
    p.receipt = receipt;
    announceClosed(p);
    // Approving links the client (rule 4: only a client the user approved before is not asked again for a value below the threshold),
    // and `remember` is the user's own trust in the applications of this request, kept for this client.
    userMap.get(p.request.sub)!.used.add(p.clientId);
    const trustedKey = p.request.sub + "|" + p.clientId;
    for (const id of s.remember?.applications ?? []) trustedApplications.set(trustedKey, (trustedApplications.get(trustedKey) ?? new Set()).add(`${r.authorization_details[0].network}:${id}`));
    return { status: 200, body: { status: "approved" } };
  }

  // --- Device list and consent settings (T-084) ------------------------------------------------------------------

  const acct = (sub: string) => {
    let a = accountSettings.get(sub);
    if (!a) accountSettings.set(sub, (a = { biometricRequired: true, blockUnverified: false, askOnTestNetworks: true }));
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
    // Rule 7: a device that is not active yet (24 h after enrollment) changes nothing, tightening included.
    if (d.activeAt > now()) throw new HttpError(403, "access_denied", "this device is not active yet (rule 7)");
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
    hub.notify(d.sub, "settings_changed", {});
    return { status: 200, body: { applied: true, loosened: loosening, settings: settingsView(d.sub) } };
  }

  /** `GET /consent/settings`: the calling client's view of the user's consent settings (issue 27, required-changes row 20). A consent token is refused. */
  function consentSettings(req: IncomingMessage, url: URL) {
    const t = bearer(req);
    const user = userMap.get(t.sub)!;
    const client = clientMap.get(t.clientId)!;
    const s = cs(t.sub, client.id);
    const origin = url.searchParams.get("origin");
    if (origin !== null && !ORIGIN.test(origin)) throw new HttpError(400, "invalid_request", "origin must be an absolute http(s) origin in canonical form");
    const coolingOffUntil = registeredAt(client) + COOLING_OFF_SECONDS;
    const apps = [...(trustedApplications.get(t.sub + "|" + client.id) ?? [])].map((k) => {
      const i = k.lastIndexOf(":");
      return { network: k.slice(0, i), app_id: Number(k.slice(i + 1)) };
    });
    const owned = [...devices.values()].filter((d) => d.sub === t.sub && !d.cancelled);
    return {
      status: 200,
      body: {
        threshold_eur: money(user.thresholdEur),
        default_threshold_eur: money(DEFAULT_THRESHOLD_EUR),
        ask_on_test_networks: acct(t.sub).askOnTestNetworks,
        device_enrolled: owned.length > 0,
        device_active: owned.some((d) => d.activeAt <= now()),
        client: {
          linked: user.used.has(client.id),
          always_ask: user.alwaysAsk.includes(client.id),
          paused_until: s.pausedUntil !== undefined && s.pausedUntil > now() ? s.pausedUntil : null,
          cooling_off_until: coolingOffUntil > now() ? coolingOffUntil : null,
          first_use: !user.used.has(client.id),
          per_client_threshold_eur: s.perClientThresholdEur === undefined ? null : money(s.perClientThresholdEur),
          domain_proven: client.verified,
          trusted_applications: apps,
        },
      },
    };
  }

  function listDevices(req: IncomingMessage) {
    const me = deviceAuth(req);
    const list = [...devices.values()].filter((x) => x.sub === me.sub && !x.cancelled).map((x) => ({ kid: x.kid, current: x.kid === me.kid, active: x.activeAt <= now(), active_at: x.activeAt }));
    return { status: 200, body: { devices: list } };
  }

  function cancelDevice(req: IncomingMessage, kid: string) {
    const me = deviceAuth(req);
    // Only an active device can cancel another pending enrollment: a pending one must not be able to cancel the others (it may cancel itself).
    if (me.activeAt > now() && kid !== me.kid) throw new HttpError(403, "access_denied", "this device is not active yet (rule 7)");
    const target = devices.get(kid);
    if (!target || target.sub !== me.sub || target.cancelled) throw new HttpError(404, "not_found");
    if (target.activeAt <= now()) throw new HttpError(409, "conflict", "only a pending enrollment can be cancelled");
    target.cancelled = true;
    target.push = undefined;
    hub.closeDevice(kid, false);
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
    if (m === "POST" && path === "/devices/challenge") return attestationChallenge(req);
    if (m === "PUT" && path === "/devices/attestation") return putAttestation(req, await readBody(req));
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
      const target = pushTarget(await readBody(req)); // a malformed registration does not use up the quota
      const recent = (pushRegistrations.get(d.kid) ?? []).filter((t) => now() - t < 60);
      if (recent.length >= MAX_PUSH_REGISTRATIONS_PER_MINUTE) throw new HttpError(429, "too_many_requests", "push registrations are limited");
      pushRegistrations.set(d.kid, [...recent, now()]);
      setPush(d, target);
      return { status: 200, body: { kid: d.kid, provider: d.push!.provider } };
    }
    if (m === "POST" && path === "/devices/hub-ticket") {
      const d = deviceAuth(req);
      return { status: 200, body: hub.issueTicket(d.sub, d.kid) };
    }
    if (m === "GET" && path === "/devices") return listDevices(req);
    if (m === "POST" && (x = path.match(/^\/devices\/([^/]+)\/cancel$/))) return cancelDevice(req, decodeURIComponent(x[1]));
    if (m === "GET" && path === "/consent/settings") return consentSettings(req, url);
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
    // Test helpers (MOCK_TEST_HELPERS=1 for the standalone server)
    if (!testHelpers && path.startsWith("/mock/")) throw new HttpError(404, "not_found");
    if (m === "GET" && (x = path.match(/^\/mock\/devices\/([^/]+)$/))) {
      const d = devices.get(x[1]);
      if (!d) throw new HttpError(404, "not_found");
      return { status: 200, body: { kid: d.kid, sub: d.sub, active: d.activeAt <= now(), active_at: d.activeAt, attestation: d.attestation, device_token: d.token, public_key: d.publicKey, ...(d.presence ? { presence: { kid: d.presence.kid, public_key: d.presence.publicKey, attestation: d.presence.attestation } } : {}) } };
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
      hub.notify(d.sub, "device_enrolled", { kid: d.kid });
      return { status: 200, body: { kid: d.kid, active: true } };
    }
    if (m === "POST" && path === "/mock/hub/close") {
      const b = await readBody(req);
      if (typeof b.kid !== "string") throw new HttpError(400, "invalid_request", "kid is required");
      hub.closeDevice(b.kid, b.allow_reconnect !== false);
      return { status: 200, body: { closed: true } };
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
  hub.attach(server);
  // A request that runs out is announced when the mock's clock passes its expiry (the mock otherwise only notices when asked).
  const sweep = setInterval(() => {
    for (const p of pending.values()) if (p.status === "pending" && expired(p)) announceClosed(p);
  }, 1000);
  sweep.unref();
  server.on("close", () => clearInterval(sweep));
  function send(res: ServerResponse, status: number, body: unknown, headers: Record<string, string>) {
    res.writeHead(status, { "Content-Type": "application/json", "Cache-Control": "no-store", "X-Biatec-Min-App-Build": String(minAppBuild), ...headers });
    res.end(JSON.stringify(body));
  }

  return { server, issuer, jwks, keys, hub, state: { pending, consumed, devices, users: userMap, tokens, pushOutbox, accountSettings, clientSettings, trustedApplications, firstSigned, noDeviceSpends } };
}
