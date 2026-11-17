// Mock Biatec OIDC consent server. It implements only what the consent protocol needs:
//   POST /connect/ciba                        direct entry (backchannel authentication)
//   POST /wallet/:network/:address/sign       step-up entry (RFC 9470 challenge) and signing with a consent token
//   PUT  /wallet/limits                       step-up entry for loosening limits
//   POST /token                               CIBA token grant, returns consent_receipt
//   GET  /consent/receipts/:auth_req_id       receipt retrieval by the owning client
//   POST /devices                             device enrollment (rule 7), optionally with a platform attestation
//   GET  /authorize, POST /token (authorization_code)  the authenticator's in-app login, PKCE (T-242)
//   POST /devices/{kid}/activate              an active device activates a pending one with a signed biatec_device_approval (T-242)
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
// 7 (the first device is active at once, a later one without an approval or a recovery code is inactive for 24 h), 8 (no device, no above-threshold actions), 9 (offline-verifiable receipts).
import { createDeviceHub } from "./device-hub.ts";
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import { createHash, randomBytes, randomInt, timingSafeEqual } from "node:crypto";
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020Module from "ajv/dist/2020.js";
import { clientFingerprint, detailsHash, isDisplaySafe, isValidIban, isRelyingPartyText, isValidRelyingPartyDetails, jcs, pairingCode, sha256Hex, signCompactJws, thumbprint, verifyApproval, verifyDeviceSigned, verifyEnrollmentProof, verifyEs256, verifyLink, verifyReceipt, ATTESTATION_TYP } from "../../scripts/lib.mjs";
import { GroupError, decodeGroup, decimalToCents, describeAlgorandGroup, describeJsonGroup, mockSigned, transactionDetails, type Described } from "./group.ts";
import { AssertionError, CIBA_GRANT, CLIENT_ASSERTION_TYPE, MetadataError, clientUriHost, validateJwks, verifyAssertion, type PublicJwk } from "./rp.ts";
import { clients, staticTokens, users, type SeedClient, type SeedUser } from "./seed.ts";

const Ajv2020 = Ajv2020Module as unknown as typeof Ajv2020Module.default;
const pkgRoot = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const loadJson = (...p: string[]) => JSON.parse(readFileSync(join(pkgRoot, ...p), "utf8"));

export const CONSENT_VALIDITY_SECONDS = 300;
export const MAX_CODE_ATTEMPTS = 3;
export const INACTIVE_DEVICE_SECONDS = 24 * 3600;
/** A login that enrolls a device must have authenticated this recently (`auth_time`, T-242): the authenticator asks for `max_age=0`, the server enforces it. */
export const LOGIN_MAX_AGE_SECONDS = 300;
/** Wrong recovery codes per account before activation by recovery code is locked (429), and for how long. */
export const RECOVERY_CODE_MAX_FAILURES = 5;
export const RECOVERY_LOCKOUT_SECONDS = 900;
/** Redirect URIs the mock accepts for the authenticator's login (T-242): the app link and the debug custom scheme fallback. */
export const AUTHENTICATOR_CLIENT_ID = "biatec-authenticator";
export const AUTHENTICATOR_REDIRECT_URIS = ["https://oidc.biatec.io/app/authenticator/callback", "io.biatec.authenticator:/oauth2redirect"];
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
  /** An operator ceiling for the user's own threshold, in EUR (MOCK_MAX_THRESHOLD_EUR in `server.ts`). Unset: no operator ceiling, only the technical one of 1000000000 EUR. */
  maxThresholdEur?: number;
  /** Registrations (`POST /register`) per source address per hour (default 100). */
  maxRegistrationsPerHour?: number;
  /** New requests one relying party may open per minute, over all its users (default 60, spec 14.4). */
  maxRelyingPartyRequestsPerMinute?: number;
  /** Open (pending) requests of one relying party for one user (default 5, spec 14.4). */
  maxRelyingPartyPendingPerUser?: number;
  /**
   * Fetches a document the real server would fetch with its hardened fetcher: the well-known file `https://<host of client_uri>/.well-known/biatec-client.json`
   * and a `jwks_uri`. Synchronous; return the parsed JSON, or null. The default serves only what `serve(url, json)` (the returned object, or the helper `POST /mock/serve`) was given: the mock never
   * leaves the machine.
   */
  fetchJson?: (url: string) => unknown;
}

/** Link of a relying party (protocol 1.2, spec 14.3). */
interface Link {
  id: string;
  clientId: string;
  secretHash: string;
  code: string;
  attempts: number;
  userLabel?: string;
  nonce: string;
  expiresAt: number;
  /** pending until the user decides; `closed` after three wrong codes (the party sees `expired`). */
  status: "pending" | "linked" | "denied" | "closed";
  /** What the server showed the device (the device fetched the link): the fingerprint and the proven domain, or null. */
  shown?: { fingerprint: string; domain: string | null };
  subject?: string;
  account?: string;
  approval?: { signed: Record<string, any>; signature: string; attestation: string };
  lastPollAt?: number;
  interval: number;
}

/** A device has an approval key (its `kid`, `publicKey`, `attestation`) and optionally a presence key; each is attested for its own use. */
interface PresenceKey { kid: string; publicKey: Record<string, string>; attestation: string }
interface Device { /** The label the new device sent (shown on the other devices). */ name?: string; /** The enrollment nonce (the challenge): the pairing code and a device approval are bound to it. */ enrollNonce?: string; kid: string; sub: string; publicKey: Record<string, string>; token: string; activeAt: number; cancelled: boolean; attestation: string; presence?: PresenceKey; recovery?: { code: string; state: "issued" | "acknowledged" | "consumed" }; push?: PushTarget; platformAttestation?: { kind: string; attestedAt: number } }
interface PushTarget { provider: "apns"; token: string }
// What a real deployment hands to FCM or APNs. The data payload is the request id and nothing else.
interface PushMessage { kid: string; provider: PushTarget["provider"]; token: string; data: { auth_req_id: string } | { event: "device_enrolled"; kid: string } }
interface Pending {
  request: Record<string, any>;
  /** The account (the user's `sub` at this server). `request.sub` is the pairwise subject of the requesting client, which for a relying party differs. */
  account: string;
  /** A relying party's request (protocol 1.2). */
  rp?: boolean;
  code: string;
  attempts: number;
  clientId: string;
  status: "pending" | "approved" | "denied" | "code_failed";
  /** A relying party's request closed because its link was removed: the token endpoint says `access_denied` with `reason: "unlinked"`, and there is no receipt. */
  closedBy?: "unlinked";
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
interface Token { /** Unix seconds of the user's last authentication (login tokens); absent = just now. */ authTime?: number; sub: string; clientId: string; scope: string; kind: "access" | "login" | "consent"; /** Redeemed by a relying party: scope openid only, refused by every endpoint. */ rp?: boolean; exp?: number; authReqId?: string; /** The group or body hash a consent token is bound to. */ consentHash?: string }

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
  if (typeof out.authorization_details === "string") { try { out.authorization_details = JSON.parse(out.authorization_details); } catch { /* left as text: refused as invalid_authorization_details */ } } // RFC 9396: JSON text in a form
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
  const maxThresholdEur = options.maxThresholdEur;
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
  const tokens = new Map<string, Token>(Object.entries(staticTokens).map(([k, { authAgeSeconds, ...v }]) => [k, { ...v, ...(authAgeSeconds ? { authTime: now() - authAgeSeconds } : {}) }]));
  // The authorization code flow of the authenticator's in-app login (T-242): single-use codes bound to the PKCE challenge.
  const authCodes = new Map<string, { sub: string; scope: string; challenge: string; redirectUri: string; expires: number; authTime: number }>();
  const recoveryFailures = new Map<string, { count: number; lockedUntil: number }>();
  const statementNonces = new Map<string, Set<string>>(); // approver kid -> nonces of the device approvals it signed (single use)
  const devices = new Map<string, Device>();
  // Single-use nonces for the platform attestation (required-changes row 25): who asked, and until when.
  const attestationChallenges = new Map<string, { sub: string; expires: number }>();
  const pending = new Map<string, Pending>();
  const consumed = new Set<string>();
  // Relying parties (protocol 1.2, spec chapter 14).
  const served = new Map<string, unknown>(); // url -> JSON the mock's stand-in for the hardened fetcher returns (serve(), POST /mock/serve)
  const fetchJson = options.fetchJson ?? ((url: string) => served.get(url) ?? null);
  const links = new Map<string, Link>();
  const consumedLinks = new Set<string>(); // link ids that got a decision (a link statement is single use)
  const subjects = new Map<string, { account: string; clientId: string }>(); // pairwise subject -> the account it was given for
  const usedAssertions = new Map<string, number>(); // client_id|jti -> when it may be forgotten
  const registrations = new Map<string, number[]>(); // source address -> times of its registrations
  const rpOpened = new Map<string, number[]>(); // relying party -> times of the requests it opened
  const maxRegistrationsPerHour = options.maxRegistrationsPerHour ?? 100;
  const maxRpPerMinute = options.maxRelyingPartyRequestsPerMinute ?? 60;
  const maxRpPending = options.maxRelyingPartyPendingPerUser ?? 5;
  const JWKS_CACHE_SECONDS = 3600;
  const pushOutbox: PushMessage[] = [];
  const hub = createDeviceHub({ now, lifetimeSeconds: options.hubLifetimeSeconds, pingSeconds: options.hubPingSeconds });
  const closedAnnounced = new Set<string>();
  /** Tells the connected devices of the user that a request is no longer open (once per request). */
  function announceClosed(p: Pending) {
    if (closedAnnounced.has(p.request.auth_req_id)) return;
    closedAnnounced.add(p.request.auth_req_id);
    hub.notify(p.account, "consent_closed", { auth_req_id: p.request.auth_req_id });
  }
  // Consent settings beyond what the seed holds (T-084). Keys: sub, or sub|clientId.
  const accountSettings = new Map<string, { biometricRequired: boolean; blockUnverified: boolean; askOnTestNetworks: boolean }>();
  const clientSettings = new Map<string, { perClientThresholdEur?: number; pausedUntil?: number; pausedOrigin?: string; label?: string }>();
  // When the user linked each client (key sub|clientId): the first approved request, or the start of the mock for a seeded client.
  const linkedAt = new Map<string, number>();
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
    for (const c of u.usedClients) { firstSigned.set(u.sub + "|" + c, 0); linkedAt.set(u.sub + "|" + c, now()); }
    for (const c of u.pausedClients ?? []) clientSettings.set(u.sub + "|" + c, { pausedUntil: now() + 10 * 365 * 86400 });
    if (u.blockUnprovenClients) accountSettings.set(u.sub, { biometricRequired: true, blockUnverified: true, askOnTestNetworks: true });
  }

  const activeDevices = (sub: string) => [...devices.values()].filter((d) => d.sub === sub && !d.cancelled && d.activeAt <= now());
  /** The device that owns a key (its approval kid or its presence kid), whatever its state. */
  const deviceOfKey = (kid: string) => [...devices.values()].find((d) => d.kid === kid || d.presence?.kid === kid);
  const isActive = (d: Device | undefined) => d !== undefined && !d.cancelled && d.activeAt <= now();

  // Helpers
  /** The pairwise subject of an account for a client (stable: the same account through the same client always gets the same one). */
  const pairwiseSubject = (clientId: string, account: string) => "pw_" + createHash("sha256").update("biatec-mock-pairwise:" + clientId + ":" + account, "utf8").digest("hex").slice(0, 16);
  /** A relying party is proven only while the well-known file of the host of its `client_uri` lists its client id (the real server re-checks daily; the mock reads it on every use). */
  let requestEpoch = 0; // incremented for every request: syncProof reads a relying party's well-known file at most once in each
  function syncProof(c: SeedClient): SeedClient {
    if (!c.rp || c.rp.checkedEpoch === requestEpoch) return c; // the proof is read once per request
    c.rp.checkedEpoch = requestEpoch;
    let f: any = null;
    try { f = fetchJson(`https://${c.rp.host}/.well-known/biatec-client.json`); } catch { f = null; }
    c.verified = !!f && typeof f === "object" && Array.isArray(f.client_ids) && f.client_ids.length <= 50 && f.client_ids.includes(c.id);
    c.domain = c.verified ? c.rp.host : undefined;
    return c;
  }
  /** Biatec OIDC vouches for no client: the fingerprint, the proven domain when there is one, and whether this user linked the client. */
  const clientInfo = (c: SeedClient, user: { used: Set<string> }) => {
    syncProof(c);
    return {
      fingerprint: clientFingerprint(c.id),
      domain_proven: c.verified,
      ...(c.verified && c.domain ? { domain: c.domain } : {}),
      linked: user.used.has(c.id),
    };
  };
  const ORIGIN = /^(?!https:\/\/[^/]*:443$)(?!http:\/\/[^/]*:80$)https?:\/\/[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*(:([1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5]))?$/;
  /** The origin a request reports: the body's `origin`, else the Origin header, else the client's own domain. It must be a canonical origin. */
  function requestOrigin(req: IncomingMessage, given: unknown, client: SeedClient): string {
    syncProof(client);
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
    if (!c || c.rp || typeof secret !== "string" || !safeEqual(secret, c.secret)) throw new HttpError(401, "invalid_client", "client authentication failed", {}, { "WWW-Authenticate": 'Basic realm="mock-oidc"' });
    return c;
  }

  const invalidClient = (why: string) => new HttpError(401, "invalid_client", why, {}, { "WWW-Authenticate": 'Client-Assertion realm="mock-oidc"' });
  /** The client assertion of a request: the form or JSON members `client_assertion_type` and `client_assertion` (RFC 7523), or for a request without a body `Authorization: Client-Assertion <jwt>`. */
  function assertionOf(req: IncomingMessage, body: Record<string, any>): string | undefined {
    const h = req.headers.authorization;
    if (h?.startsWith("Client-Assertion ")) return h.slice(17).trim();
    if (body.client_assertion === undefined && body.client_assertion_type === undefined) return undefined;
    if (body.client_assertion_type !== CLIENT_ASSERTION_TYPE || typeof body.client_assertion !== "string") throw invalidClient("client_assertion_type must be " + CLIENT_ASSERTION_TYPE + " with a client_assertion");
    return body.client_assertion;
  }

  /** Authenticates a relying party by its `private_key_jwt` assertion (RFC 7523: iss = sub = client_id, aud the token endpoint or the issuer, exp at most 5 minutes ahead, jti single use, ES256 or PS256). */
  function authRp(jwt: string, bodyClientId?: unknown): SeedClient {
    let claimed: unknown;
    try { claimed = JSON.parse(Buffer.from(jwt.split(".")[1] ?? "", "base64url").toString("utf8")).iss; } catch { claimed = undefined; }
    const c = typeof claimed === "string" ? clientMap.get(claimed) : undefined;
    if (!c?.rp) throw invalidClient("client assertion: unknown client");
    // A client_id sent next to the assertion must be the one the assertion is for (RFC 7523 section 3): a conflict is refused, never resolved in either one's favour.
    if (bodyClientId !== undefined && bodyClientId !== c.id) throw invalidClient("client_id conflicts with the iss and sub of the client assertion");
    if (c.rp.jwksUri && now() - c.rp.keysFetchedAt > JWKS_CACHE_SECONDS) {
      try { c.rp.keys = validateJwks(fetchJson(c.rp.jwksUri)); c.rp.keysFetchedAt = now(); } catch { /* keep the cached keys */ }
    }
    const aud = [issuer, issuer.replace(/\/+$/, "") + "/token"];
    try {
      const { jti, exp } = verifyAssertion(jwt, c.rp.keys, c.id, aud, now());
      if (usedAssertions.has(c.id + "|" + jti)) throw new AssertionError("client assertion: jti was used already");
      usedAssertions.set(c.id + "|" + jti, exp);
    } catch (e) {
      if (e instanceof AssertionError) throw invalidClient(e.message);
      throw e;
    }
    return syncProof(c);
  }

  /** A seeded client by its secret (Basic or form), a relying party by its assertion. */
  function authAny(req: IncomingMessage, body: Record<string, any>): SeedClient {
    const jwt = assertionOf(req, body);
    return jwt === undefined ? authClient(req, body) : authRp(jwt, body.client_id);
  }

  /** A consent token (acr biatec:consent) is accepted only by the wallet signing endpoints (`allowConsent`); everywhere else it is refused (decision D8). */
  function bearer(req: IncomingMessage, allowConsent = false): Token & { value: string } {
    const h = req.headers.authorization;
    const value = h?.startsWith("Bearer ") ? h.slice(7) : "";
    const t = tokens.get(value);
    if (!t || (t.exp !== undefined && t.exp <= now())) throw new HttpError(401, "invalid_token", "missing, unknown or expired bearer token", {}, { "WWW-Authenticate": 'Bearer error="invalid_token"' });
    // The token a relying party redeems is useless at every endpoint of this service (spec 14.1): not even the signing endpoint takes it.
    if (t.rp) throw new HttpError(401, "invalid_token", "the token of a relying party request is accepted by no endpoint", {}, { "WWW-Authenticate": 'Bearer error="invalid_token"' });
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

  const stillOpen = (p: Pending, account: string, clientId: string) => p.account === account && p.clientId === clientId && p.status === "pending" && !p.tokenIssued && !expired(p);

  /** Spec 14.4: at most five open requests per user and relying party, and 60 requests a minute per relying party; the threshold, trust and first-use rules do not apply. */
  function enforceRpLimits(account: string, clientId: string) {
    const open = [...pending.values()].filter((p) => stillOpen(p, account, clientId)).length;
    if (open >= maxRpPending) throw new HttpError(429, "too_many_pending_requests", `at most ${maxRpPending} requests of one relying party can be open for one user`, { limit: maxRpPending, scope: { client: clientId } });
    const recent = (rpOpened.get(clientId) ?? []).filter((x) => now() - x < 60);
    if (recent.length >= maxRpPerMinute) throw new HttpError(429, "rate_limited", `at most ${maxRpPerMinute} requests per minute`, {}, { "Retry-After": String(Math.max(1, recent[0] + 60 - now())) });
  }

  /** Rule 10: at most three pending requests per client, user and origin, and an anti-spam rate per client and user. */
  function enforceClientLimits(account: string, clientId: string, origin: string | undefined) {
    const open = [...pending.values()].filter((p) => stillOpen(p, account, clientId) && p.request.origin === origin).length;
    if (open >= maxPending) throw new HttpError(429, "too_many_pending_requests", `at most ${maxPending} requests of one client can be open for one user from one origin`, { limit: maxPending, scope: { client: clientId, origin } });
    const rateKey = account + "|" + clientId;
    const recent = (openedAt.get(rateKey) ?? []).filter((x) => now() - x < 3600);
    if (recent.length >= maxPerHour) throw new HttpError(429, "rate_limited", `at most ${maxPerHour} requests per hour`, {}, { "Retry-After": String(Math.max(1, recent[0] + 3600 - now())) });
    openedAt.set(rateKey, [...recent, now()]);
  }

  function openRequest(opts: { entry: "step-up" | "direct"; user: SeedUser & { used: Set<string> }; client: SeedClient; details: Record<string, any>; bindingMessage: string; requestedExpiry: number; origin: string | undefined; scope: string; limitsBody?: Record<string, string>; /** A relying party's request (protocol 1.2): the pairwise subject it knows the user by, and the hash its details are bound to. */ rp?: { subject: string; boundHash: string } }) {
    if (opts.rp) enforceRpLimits(opts.user.sub, opts.client.id);
    else enforceClientLimits(opts.user.sub, opts.client.id, opts.origin);
    const authReqId = "ciba_" + randomBytes(16).toString("hex");
    const code = newCode();
    // Rendered, and so signed, only for a client with a proven domain and only in the direct entry (spec section 5).
    syncProof(opts.client);
    const message = opts.entry === "direct" && opts.client.verified ? opts.bindingMessage : "";
    const request = {
      auth_req_id: authReqId,
      entry: opts.entry,
      authorization_details: [opts.details],
      client: clientInfo(opts.client, opts.user),
      binding_message: message,
      requested_expiry: opts.requestedExpiry,
      expiry: now() + opts.requestedExpiry,
      bound_hash: opts.rp?.boundHash ?? opts.details.group_hash ?? opts.details.request_hash,
      ...(opts.origin !== undefined ? { origin: opts.origin } : {}), // a relying party's request has an origin only when the server had one
      sub: opts.rp?.subject ?? opts.user.sub,
    };
    validate("consent-request", request, "internal consent request");
    if (opts.rp) rpOpened.set(opts.client.id, [...(rpOpened.get(opts.client.id) ?? []).filter((x) => now() - x < 60), now()]); // counted once the request is stored
    pending.set(authReqId, { request, account: opts.user.sub, ...(opts.rp ? { rp: true } : {}), code, attempts: 0, clientId: opts.client.id, status: "pending", tokenIssued: false, scope: opts.scope, limitsBody: opts.limitsBody, interval: POLL_INTERVAL_SECONDS, spend: "none" });
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
    syncProof(client);
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

  /** What is wrong with a relying party's details, for the `invalid_authorization_details` description (the mock names the first rule broken; nothing is stripped or corrected). */
  function whyInvalidDetails(details: unknown): string | null {
    const d = (Array.isArray(details) ? details[0] : undefined) as Record<string, any> | undefined;
    if (!Array.isArray(details) || details.length !== 1 || !d || typeof d !== "object") return "authorization_details is an array with exactly one object";
    if ("origin" in d) return "origin is added by the server and is never sent by the relying party";
    const v = ajv.getSchema("https://schemas.biatec.io/consent/v1/authorization-details.schema.json#/$defs/v1_2")!;
    if (!v(details)) return "schema: " + ajv.errorsText(v.errors);
    if (d.type === "biatec_payment" && d.creditor_account && "iban" in d.creditor_account && !isValidIban(d.creditor_account.iban)) return "creditor_account.iban: the check digits are wrong";
    return isValidRelyingPartyDetails(d) ? null : "a text rule (spec section 15), the amount or the date is violated: refused, never stripped";
  }

  /** `POST /connect/ciba` of a linked relying party (spec 14.4): scope openid and one payment, login or action object. Always goes to the phone. */
  function cibaRelyingParty(req: IncomingMessage, client: SeedClient, params: Record<string, any>) {
    if (params.scope !== "openid") throw new HttpError(403, "unauthorized_client", "a relying party asks with scope openid and authorization_details: it never gets sign, manage-limits or any wallet scope");
    if (typeof params.login_hint !== "string" || params.login_hint === "") throw new HttpError(400, "invalid_request", "login_hint is the subject of the link");
    // Nothing opens before the user linked this party on the phone, whatever the content: an unknown, unlinked or unlinked-again subject is one answer.
    const link = subjects.get(params.login_hint);
    const user = link && link.clientId === client.id ? userMap.get(link.account) : undefined;
    if (!user || !user.used.has(client.id)) throw new HttpError(400, "unknown_user_id", "login_hint does not identify a linked user");
    const details = params.authorization_details;
    // One validation pass on the way through: the request schema (which holds the 1.2 details schemas and forbids an origin inside the object), then the rules of spec 15 and the IBAN.
    // Only a refusal runs the diagnosis that names the first rule broken.
    const generic = validator("backchannel-authentication-request");
    if (!generic(params) || !isValidRelyingPartyDetails(details[0])) {
      const schemaText = ajv.errorsText(generic.errors);
      const why = whyInvalidDetails(details);
      if (why) throw new HttpError(400, "invalid_authorization_details", why);
      throw new HttpError(400, "invalid_request", "backchannel authentication request: " + schemaText);
    }
    const given = params.origin ?? req.headers.origin;
    if (given !== undefined && (typeof given !== "string" || given.length > 253 || !ORIGIN.test(given))) throw new HttpError(400, "invalid_request", "origin must be an absolute http(s) origin in canonical form (lower-case host, no path, no default port)");
    if (activeDevices(user.sub).length === 0) throw new HttpError(403, "consent_unavailable", "phone approval is required but the user has no active enrolled device", { reason: "no_active_device" });
    standingRefusals(user.sub, client);
    // The server adds `origin` only when it has one and never rewrites a member; the bound hash is the hash of the object as stored (spec 3.6).
    const stored = { ...details[0], ...(given !== undefined ? { origin: given } : {}) };
    const r = openRequest({ entry: "direct", user, client, details: stored, bindingMessage: "", requestedExpiry: params.requested_expiry ?? CONSENT_VALIDITY_SECONDS, origin: given, scope: "openid", rp: { subject: params.login_hint, boundHash: detailsHash(stored) } });
    const res = { auth_req_id: r.authReqId, expires_in: r.expiresIn, interval: POLL_INTERVAL_SECONDS, binding_code: r.code };
    validate("backchannel-authentication-response", res, "internal response");
    return { status: 200, body: res };
  }

  function ciba(req: IncomingMessage, body: Record<string, any>) {
    const client = authAny(req, body);
    const { client_id: _a, client_secret: _b, client_assertion: _c, client_assertion_type: _d, ...params } = body;
    if (client.rp) return cibaRelyingParty(req, client, params);
    if (params.scope === "openid") throw new HttpError(403, "unauthorized_client", "authorization_details of a payment, login or action are for linked relying parties (private_key_jwt clients)");
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

  /**
   * The authenticator's in-app login (T-242): authorization code with PKCE (S256), a public client. The mock logs the user in at once (`login_hint` is an email address, alice
   * by default) and always authenticates afresh, which is what `prompt=login` and `max_age=0` ask for. The code goes back to the redirect URI in a 302.
   */
  function authorize(url: URL): { status: number; body: unknown; headers?: Record<string, string> } {
    const q = (k: string) => url.searchParams.get(k) ?? "";
    const redirect = q("redirect_uri");
    if (q("client_id") !== AUTHENTICATOR_CLIENT_ID) throw new HttpError(400, "invalid_client", "unknown client_id");
    if (!AUTHENTICATOR_REDIRECT_URIS.includes(redirect)) throw new HttpError(400, "invalid_request", "redirect_uri is not registered for this client");
    const back = (params: Record<string, string>) => {
      const u = new URL(redirect);
      for (const [k, v] of Object.entries({ ...params, ...(q("state") ? { state: q("state") } : {}) })) u.searchParams.set(k, v);
      return { status: 302, body: {}, headers: { Location: u.toString() } };
    };
    if (q("response_type") !== "code") return back({ error: "unsupported_response_type" });
    if (q("code_challenge_method") !== "S256" || !/^[A-Za-z0-9_-]{43}$/.test(q("code_challenge"))) return back({ error: "invalid_request", error_description: "PKCE with S256 is required" });
    const user = [...userMap.values()].find((u) => u.email === (q("login_hint") || "alice@example.com"));
    if (!user) return back({ error: "access_denied", error_description: "unknown login_hint" });
    const granted = q("scope").split(" ").filter((x) => x === "openid" || x === "enroll-device");
    const code = "code_" + randomBytes(24).toString("base64url");
    for (const [c, v] of authCodes) if (v.expires < now()) authCodes.delete(c);
    authCodes.set(code, { sub: user.sub, scope: granted.join(" "), challenge: q("code_challenge"), redirectUri: redirect, expires: now() + 60, authTime: now() });
    return back({ code });
  }

  function codeGrant(body: Record<string, any>) {
    if (body.client_id !== AUTHENTICATOR_CLIENT_ID) throw new HttpError(400, "invalid_client", "unknown client_id");
    const c = typeof body.code === "string" ? authCodes.get(body.code) : undefined;
    if (typeof body.code === "string") authCodes.delete(body.code); // single use, also when it fails below
    if (!c || c.expires < now() || c.redirectUri !== body.redirect_uri) throw new HttpError(400, "invalid_grant", "the code is unknown, used, expired or for another redirect_uri");
    const verifier = typeof body.code_verifier === "string" ? body.code_verifier : "";
    if (!/^[A-Za-z0-9._~-]{43,128}$/.test(verifier) || !safeEqual(createHash("sha256").update(verifier).digest("base64url"), c.challenge)) throw new HttpError(400, "invalid_grant", "PKCE verification failed");
    const value = "login_" + randomBytes(24).toString("base64url");
    tokens.set(value, { sub: c.sub, clientId: AUTHENTICATOR_CLIENT_ID, scope: c.scope, kind: "login", authTime: c.authTime, exp: now() + LOGIN_MAX_AGE_SECONDS });
    // No refresh token and no ID token: the access token is held in memory for the enrollment and dropped.
    return { status: 200, body: { access_token: value, token_type: "Bearer", expires_in: LOGIN_MAX_AGE_SECONDS, scope: c.scope } };
  }

  function tokenEndpoint(req: IncomingMessage, body: Record<string, any>) {
    if (body.grant_type === "authorization_code") return codeGrant(body);
    const client = authAny(req, body);
    if (body.grant_type !== CIBA_GRANT) throw new HttpError(400, client.rp ? "unauthorized_client" : "unsupported_grant_type", client.rp ? "a relying party can use only the CIBA grant" : undefined);
    const p = typeof body.auth_req_id === "string" ? pending.get(body.auth_req_id) : undefined;
    if (!p || p.clientId !== client.id) throw new HttpError(400, "invalid_grant", "unknown auth_req_id");
    if (p.tokenIssued) throw new HttpError(400, "invalid_grant", "auth_req_id was already redeemed");
    if (p.status === "denied" && p.closedBy === "unlinked") throw new HttpError(400, "access_denied", "the link was removed (by the user or by the relying party) while the request was open: this is not a denial by the user, and there is no receipt", { reason: "unlinked" });
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
    tokens.set(value, { sub: p.request.sub, clientId: client.id, scope: p.scope, kind: "consent", exp, authReqId: p.request.auth_req_id, consentHash: p.request.bound_hash, ...(p.rp ? { rp: true } : {}) });
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
    } else clientId = authAny(req, {}).id;
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
  function takePlatformAttestation(sub: string, body: Record<string, any>, spentNonce?: string) {
    const a = body.platform_attestation;
    if (!a || (a.kind !== "play_integrity" && a.kind !== "app_attest") || typeof a.token !== "string" || !a.token || a.token.length > 65536 || (a.key_id !== undefined && (typeof a.key_id !== "string" || a.key_id.length > 256))) {
      throw new HttpError(400, "invalid_request", "platform_attestation needs a kind (play_integrity or app_attest) and a token");
    }
    const nonce = body.attestation_challenge;
    if (spentNonce !== undefined) {
      // Two keys: the enrollment proof already consumed this challenge (it stays consumed); the platform attestation must be bound to the same nonce.
      if (nonce !== spentNonce) throw new HttpError(400, "invalid_request", "attestation_challenge must be the nonce of the enrollment proof");
      return { kind: a.kind as string, attestedAt: now() };
    }
    const c = typeof nonce === "string" ? attestationChallenges.get(nonce) : undefined;
    if (!c || c.sub !== sub || c.expires < now()) throw new HttpError(400, "invalid_request", "attestation_challenge is unknown, used, expired or another user's");
    attestationChallenges.delete(nonce); // single use, also when the rest of the request fails
    return { kind: a.kind as string, attestedAt: now() };
  }

  /**
   * A login token that may enroll a device: scope `enroll-device` (else 403) and a fresh `auth_time` (else `401 insufficient_user_authentication` with `max_age`, so the
   * app restarts the login once). A token without `auth_time` (the seeded `login-*` tokens) counts as authenticated just now.
   */
  function enrollmentToken(req: IncomingMessage): Token & { value: string } {
    const t = bearer(req);
    if (t.kind !== "login") throw new HttpError(403, "access_denied", "enrollment needs a fresh full login");
    if (!t.scope.split(" ").includes("enroll-device")) throw new HttpError(403, "insufficient_scope", "the login token needs the enroll-device scope", {}, { "WWW-Authenticate": 'Bearer error="insufficient_scope", scope="enroll-device"' });
    if (t.authTime !== undefined && now() - t.authTime > LOGIN_MAX_AGE_SECONDS) {
      throw new HttpError(401, "insufficient_user_authentication", "the login is too old: sign in again", { max_age: 0 }, { "WWW-Authenticate": 'Bearer error="insufficient_user_authentication", max_age=0' });
    }
    return t;
  }

  function attestationChallenge(req: IncomingMessage) {
    const sub = req.headers.authorization?.startsWith("Device ") ? (() => {
      const d = deviceAuth(req);
      if (d.cancelled) throw new HttpError(403, "access_denied", "this device was cancelled");
      return d.sub;
    })() : (() => {
      return enrollmentToken(req).sub;
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
    const t = enrollmentToken(req);
    const pk = body.public_key;
    if (typeof body.kid !== "string" || !body.kid || !pk || pk.kty !== "EC" || pk.crv !== "P-256" || typeof pk.x !== "string" || typeof pk.y !== "string") throw new HttpError(400, "invalid_request", "kid and an EC P-256 public_key are required");
    if (deviceOfKey(body.kid)) throw new HttpError(409, "conflict", "kid already enrolled");
    const hadActive = activeDevices(t.sub).length > 0;
    // Rule 7 (T-252): the first device of an account (it has no other device, active or pending; a cancelled enrollment is not one) is active at once.
    // A later device waits, unless a recovery code (below) or a device approval (`POST /devices/{kid}/activate`) activates it.
    const first = ![...devices.values()].some((d) => d.sub === t.sub && !d.cancelled);
    let activeAt = first ? now() : now() + INACTIVE_DEVICE_SECONDS;
    // Naming the kid of an active device proves nothing (T-242): activation is a signed device approval (`POST /devices/{kid}/activate`) or a recovery code.
    if (body.approved_by_kid !== undefined) throw new HttpError(400, "invalid_request", "approved_by_kid is not accepted: an active device activates a new one with POST /devices/{kid}/activate");
    if (body.name !== undefined && (typeof body.name !== "string" || body.name.length < 1 || body.name.length > 80)) throw new HttpError(400, "invalid_request", "name is 1 to 80 characters");
    let recoveryFrom: Device | undefined;
    if (body.activation !== undefined) {
      const a = body.activation;
      if (!a || typeof a !== "object" || a.kind !== "recovery_code" || typeof a.code !== "string") throw new HttpError(400, "invalid_request", "activation is { kind: \"recovery_code\", code }");
      const f = recoveryFailures.get(t.sub);
      if (f && f.lockedUntil > now()) throw new HttpError(429, "too_many_attempts", "too many wrong recovery codes: try again later", {}, { "Retry-After": String(f.lockedUntil - now()) });
      const given = a.code.trim().toUpperCase();
      recoveryFrom = [...devices.values()].find((d) => d.sub === t.sub && !d.cancelled && d.activeAt <= now() && d.recovery !== undefined && d.recovery.state !== "consumed" && safeEqual(d.recovery.code, given));
      if (!recoveryFrom) {
        const next = { count: (f?.count ?? 0) + 1, lockedUntil: 0 };
        if (next.count >= RECOVERY_CODE_MAX_FAILURES) { next.count = 0; next.lockedUntil = now() + RECOVERY_LOCKOUT_SECONDS; }
        recoveryFailures.set(t.sub, next);
        throw new HttpError(403, "invalid_recovery_code", "the recovery code is wrong");
      }
      // The code is burnt and the failure counter reset only once the device was added (below): any other refusal leaves both untouched.
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
      if (!c || c.sub !== t.sub || c.expires < now()) throw new HttpError(400, "invalid_challenge", "the enrollment challenge in the proof is unknown, used, expired or another user's");
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
    }
    // Without a platform attestation the device is enrolled and flagged; with a malformed or replayed one it is refused.
    // The two fields go together: one alone is refused (an orphan challenge would otherwise be ignored and left unspent).
    if ((body.platform_attestation === undefined) !== (body.attestation_challenge === undefined)) throw new HttpError(400, "invalid_request", "attestation_challenge and platform_attestation are sent together");
    const platformAttestation = body.platform_attestation === undefined ? undefined : takePlatformAttestation(t.sub, body, enrollNonce);
    // A device with a second, presence key sends it with its kid; its own attestation then says `presence`. (The enrollment proof, the pairing
    // code and activation by another device are T-242's; the mock only models the keys and their attested use.)
    let presence: { kid: string; publicKey: Record<string, string> } | undefined;
    if (body.presence_kid !== undefined || body.presence_public_key !== undefined) {
      const pp = body.presence_public_key;
      if (typeof body.presence_kid !== "string" || !body.presence_kid || body.presence_kid === body.kid || !pp || pp.kty !== "EC" || pp.crv !== "P-256" || typeof pp.x !== "string" || typeof pp.y !== "string") throw new HttpError(400, "invalid_request", "presence_kid and an EC P-256 presence_public_key go together and name another key");
      if (deviceOfKey(body.presence_kid)) throw new HttpError(409, "conflict", "presence_kid already enrolled");
      presence = { kid: body.presence_kid, publicKey: { kty: "EC", crv: "P-256", x: pp.x, y: pp.y } };
    }
    const d = addDevice(t.sub, body.kid, { kty: "EC", crv: "P-256", x: pk.x, y: pk.y }, activeAt, undefined, presence);
    if (recoveryFrom) {
      // Used up: a recovery code activates one device.
      recoveryFailures.delete(t.sub);
      recoveryFrom.recovery!.state = "consumed"; // never issued or accepted again, whatever is asked of the device afterwards
    }
    if (typeof body.name === "string") d.name = body.name;
    d.enrollNonce = enrollNonce;
    setPush(d, push);
    announceEnrolled(d);
    d.platformAttestation = platformAttestation;
    return { status: 201, body: { kid: d.kid, active: d.activeAt <= now(), active_at: d.activeAt, attestation: d.attestation, ...(d.presence ? { presence_kid: d.presence.kid, presence_attestation: d.presence.attestation } : {}), device_token: d.token, cancel_notice_sent_to_existing_devices: hadActive && activeAt > now(), ...(platformAttestation ? { attestation_refresh_after: now() + ATTESTATION_REFRESH_SECONDS } : {}) } };
  }

  /** Tells the user's other devices that a device was enrolled or activated: the channel event, and a push message for each active device with a push token. */
  function announceEnrolled(d: Device) {
    hub.notify(d.sub, "device_enrolled", { kid: d.kid });
    for (const o of activeDevices(d.sub)) if (o.push && o.kid !== d.kid) pushOutbox.push({ kid: o.kid, ...o.push, data: { event: "device_enrolled", kid: d.kid } });
    if (pushOutbox.length > 500) pushOutbox.splice(0, pushOutbox.length - 500);
  }

  function deviceView(p: Pending) {
    return p.request; // the consent request object, never the binding code
  }

  function deviceOwns(d: Device, id: string): Pending {
    const p = pending.get(id);
    if (!p || p.account !== d.sub) throw new HttpError(404, "not_found");
    return p;
  }

  function postReceipt(req: IncomingMessage, id: string, receipt: Record<string, any>) {
    const d = deviceAuth(req);
    const p = deviceOwns(d, id);
    if (d.activeAt > now()) throw new HttpError(403, "access_denied", "this device is not active yet (rule 7)");
    validate("consent-receipt", receipt, "receipt");
    if (receipt.version !== (p.rp ? "1.2" : "1.1")) throw new HttpError(400, "invalid_receipt", p.rp ? "a protocol 1.2 receipt is required for a relying party's request (version \"1.2\", details_hash, client_fingerprint, device.key_use)" : "a protocol 1.1 receipt is required (version \"1.1\", client_fingerprint, device.key_use)");
    const s = receipt.signed;
    const decision = s.decision;
    if (p.status === "approved" || p.status === "denied" || (p.status === "code_failed" && p.receipt)) throw new HttpError(409, "conflict", "this request already has a decision");
    if (expired(p)) throw new HttpError(410, "expired_token", "the request expired");
    // Rule 2: code_failed is terminal; only the code_failed receipt itself is accepted afterwards.
    if (p.status === "code_failed" && decision !== "code_failed") throw new HttpError(403, "access_denied", "attempts exhausted", { reason: "code_failed" });
    // Rule 9: both signatures, and the key must be one the device enrolled (its approval key or its presence key); the attestation says which use.
    const key = receipt.device.kid === d.kid ? { kid: d.kid, publicKey: d.publicKey } : d.presence && receipt.device.kid === d.presence.kid ? d.presence : undefined;
    if (p.rp && key) {
      // The attestation of a 1.2 receipt names the pairwise subject the party knows the user by (spec section 8): Biatec OIDC mints it when the receipt is stored, so the one the device
      // holds (for its account) is replaced. The signature is the device's and is verified below; `device.key_use` must name the use of the key.
      const use = key.kid === d.kid ? "approve" : "presence";
      if (receipt.device.key_use !== use) throw new HttpError(400, "invalid_receipt", "device.key_use does not name the use of the key");
      receipt = { ...receipt, device: { ...receipt.device, attestation: attest(p.request.sub, key.kid, key.publicKey, use) } };
    }
    if (!key || jcs(receipt.device.public_key) !== jcs(key.publicKey) || !verifyReceipt(receipt, jwks, { issuer, now: now() })) {
      throw new HttpError(400, "invalid_receipt", "receipt signature or attestation does not verify");
    }
    // The receipt must restate the stored request (rule 1); it never supplies the thing being consented to.
    const r = p.request;
    if (s.auth_req_id !== r.auth_req_id || s.client_fingerprint !== r.client.fingerprint || s.sub !== r.sub || s.expiry !== r.expiry || s.binding_message !== r.binding_message || jcs(s.authorization_details) !== jcs(r.authorization_details) || (p.rp && s.details_hash !== r.bound_hash)) {
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
    const approvedUser = userMap.get(p.account)!;
    if (!approvedUser.used.has(p.clientId)) linkedAt.set(p.account + "|" + p.clientId, now());
    approvedUser.used.add(p.clientId);
    const trustedKey = p.account + "|" + p.clientId;
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

  const appList = (sub: string, clientId: string) =>
    [...(trustedApplications.get(sub + "|" + clientId) ?? [])].map((k) => {
      const i = k.lastIndexOf(":");
      return { network: k.slice(0, i), app_id: Number(k.slice(i + 1)) };
    });
  /** The origin the mock reports for a client's pause when the helper gave none: the client's own domain. */
  const defaultOrigin = (c: SeedClient) => "https://" + (c.domain ?? "client.invalid");

  /**
   * `GET /devices/settings` (issue 30, item 3). `clients` lists the clients that are linked or hold any setting of the user's (a label, a trusted application, "always ask",
   * a threshold of their own, a pause); the app shows the linked ones. The mock vouches for no name: `label` is the user's own text or null, and the client is told
   * apart by its `fingerprint` and, when proven, its `domain`.
   */
  function settingsView(sub: string) {
    const user = userMap.get(sub)!;
    return {
      sub,
      biometric_required: acct(sub).biometricRequired,
      threshold_eur: money(user.thresholdEur),
      default_threshold_eur: money(DEFAULT_THRESHOLD_EUR),
      ...(maxThresholdEur !== undefined ? { max_threshold_eur: money(maxThresholdEur) } : {}),
      block_unproven_clients: acct(sub).blockUnverified,
      ask_on_test_networks: acct(sub).askOnTestNetworks,
      clients: [...clientMap.values()]
        .filter((c) => {
          const s = clientSettings.get(sub + "|" + c.id) ?? {}; // a lookup that creates nothing
          return user.used.has(c.id) || user.alwaysAsk.includes(c.id) || s.label !== undefined || s.perClientThresholdEur !== undefined || (s.pausedUntil !== undefined && s.pausedUntil > now()) || appList(sub, c.id).length > 0;
        })
        .map((c) => {
          syncProof(c); // only the clients this user holds something for: a relying party is read lazily, never all of them on every call
          const s = clientSettings.get(sub + "|" + c.id) ?? {};
          const paused = s.pausedUntil !== undefined && s.pausedUntil > now();
          return {
            client_id: c.id,
            label: s.label ?? null,
            fingerprint: clientFingerprint(c.id),
            domain_proven: c.verified,
            ...(c.verified && c.domain ? { domain: c.domain } : {}),
            linked: user.used.has(c.id),
            linked_at: user.used.has(c.id) ? linkedAt.get(sub + "|" + c.id) ?? null : null,
            always_ask: user.alwaysAsk.includes(c.id),
            per_client_threshold_eur: s.perClientThresholdEur === undefined ? null : money(s.perClientThresholdEur),
            paused_until: paused ? s.pausedUntil : null,
            pauses: paused ? [{ origin: s.pausedOrigin ?? defaultOrigin(c), paused_until: s.pausedUntil }] : [],
            trusted_applications: appList(sub, c.id),
          };
        }),
    };
  }

  /** A decimal string of at most two decimals (the schema's amount). */
  const isAmount = (v: unknown): v is string => typeof v === "string" && /^(0|[1-9][0-9]{0,14})(\.[0-9]{1,2})?$/.test(v);
  const MAX_TECHNICAL_THRESHOLD_EUR = 1_000_000_000;

  /**
   * Validates the change shape and reports whether it loosens anything. The shape is the one of the signed `settings-change-signed` schema (spec 8.2):
   * a rule broken answers 400, never a partial change. Loosening is decided here, by the server, not by the app: raising a threshold, setting a per-client
   * threshold above the effective one (or `null` when that raises it), turning off `always_ask`, `block_unproven_clients`, `biometric_required` or
   * `ask_on_test_networks`, adding a trusted application, lifting a pause. Removing a trusted application, unlinking and a label are tightening. A mixed change is loosening.
   */
  function classifyChange(sub: string, change: any): { loosening: boolean } {
    const user = userMap.get(sub)!;
    const bad = (m: string) => new HttpError(400, "invalid_request", m);
    if (!change || typeof change !== "object" || Array.isArray(change)) throw bad("change must be an object");
    const allowed = new Set(["threshold_eur", "biometric_required", "block_unproven_clients", "ask_on_test_networks", "clients"]);
    for (const k of Object.keys(change)) if (!allowed.has(k)) throw bad("unknown setting " + k);
    if (Object.keys(change).length === 0) throw bad("change is empty");
    const ceiling = maxThresholdEur ?? MAX_TECHNICAL_THRESHOLD_EUR;
    const checkAmount = (name: string, v: unknown) => {
      if (!isAmount(v)) throw bad(name + " must be a decimal string of at most two decimals");
      if (Number(v) > ceiling) throw bad(`${name} must not be more than ${money(ceiling)} EUR${maxThresholdEur !== undefined ? " (the limit of this service)" : ""}`);
    };
    let loosening = false;
    if ("threshold_eur" in change) {
      checkAmount("threshold_eur", change.threshold_eur);
      if (Number(change.threshold_eur) > user.thresholdEur) loosening = true;
    }
    for (const k of ["biometric_required", "block_unproven_clients", "ask_on_test_networks"]) {
      if (k in change && typeof change[k] !== "boolean") throw bad(k + " must be a boolean");
    }
    if (change.biometric_required === false && acct(sub).biometricRequired) loosening = true;
    if (change.block_unproven_clients === false && acct(sub).blockUnverified) loosening = true;
    if (change.ask_on_test_networks === false && acct(sub).askOnTestNetworks) loosening = true;
    if ("clients" in change) {
      if (!change.clients || typeof change.clients !== "object" || Array.isArray(change.clients) || Object.keys(change.clients).length === 0) throw bad("clients must be a non-empty object");
      for (const [id, c] of Object.entries<any>(change.clients)) {
        const client = clientMap.get(id);
        if (!client) throw bad("unknown client " + id);
        if (!c || typeof c !== "object" || Array.isArray(c) || Object.keys(c).length === 0) throw bad("empty change for " + id);
        for (const k of Object.keys(c)) if (!["always_ask", "per_client_threshold_eur", "label", "trusted_applications_add", "trusted_applications_remove", "paused", "origin", "unlink"].includes(k)) throw bad("unknown client setting " + k);
        const s = cs(sub, id);
        if ("unlink" in c) {
          if (c.unlink !== true || Object.keys(c).length !== 1) throw bad("unlink must be true and the only member of the change for the client");
          if (!user.used.has(id)) throw bad("only a linked client can be unlinked");
          continue; // tightening
        }
        if ("always_ask" in c && typeof c.always_ask !== "boolean") throw bad("always_ask must be a boolean");
        if ("paused" in c !== "origin" in c) throw bad("paused and origin are given together");
        if ("paused" in c && c.paused !== false) throw bad("a client can only be unpaused here (a pause is set by the user's denials, POST /devices/pauses)");
        if ("origin" in c && !(typeof c.origin === "string" && c.origin.length <= 253 && ORIGIN.test(c.origin))) throw bad("origin must be a canonical http(s) origin");
        if ("per_client_threshold_eur" in c && c.per_client_threshold_eur !== null) checkAmount("per_client_threshold_eur", c.per_client_threshold_eur);
        if ("label" in c) {
          if (!isDisplaySafe(c.label)) throw bad("label must be 1 to 40 characters of plain text (Unicode NFC, no control, bidirectional, zero-width or invisible characters, no padding)");
          if (!user.used.has(id)) throw bad("only a linked client can be labelled");
        }
        for (const k of ["trusted_applications_add", "trusted_applications_remove"]) {
          if (!(k in c)) continue;
          const l = c[k];
          const key = (a: any) => a.network + ":" + a.app_id;
          if (!Array.isArray(l) || l.length < 1 || l.length > 16 || !l.every((a) => a && typeof a === "object" && ["algorand-mainnet", "algorand-testnet"].includes(a.network) && Number.isSafeInteger(a.app_id) && a.app_id >= 1 && Object.keys(a).length === 2) || new Set(l.map(key)).size !== l.length) {
            throw bad(k + " must be 1 to 16 unique { network, app_id } entries of an Algorand network");
          }
          if (k === "trusted_applications_add" && !user.used.has(id)) throw bad("only a linked client can have trusted applications");
        }
        if (c.trusted_applications_add && c.trusted_applications_remove) {
          const removed = new Set(c.trusted_applications_remove.map((a: any) => a.network + ":" + a.app_id));
          if (c.trusted_applications_add.some((a: any) => removed.has(a.network + ":" + a.app_id))) throw bad("the same application cannot be added and removed in one change");
        }
        if ("paused" in c && !(s.pausedUntil !== undefined && s.pausedUntil > now() && c.origin === (s.pausedOrigin ?? defaultOrigin(client)))) throw bad("this client has no pause for that origin");
        if (c.always_ask === false && user.alwaysAsk.includes(id)) loosening = true;
        if (c.paused === false) loosening = true;
        if (c.trusted_applications_add) loosening = true;
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
    if ("block_unproven_clients" in change) acct(sub).blockUnverified = change.block_unproven_clients;
    if ("ask_on_test_networks" in change) acct(sub).askOnTestNetworks = change.ask_on_test_networks;
    for (const [id, c] of Object.entries<any>(change.clients ?? {})) {
      const s = cs(sub, id);
      const key = sub + "|" + id;
      if (c.unlink === true) {
        // Unlinking forgets everything the user held for the client except a running pause: the next request is first use again.
        // A relying party's open requests are closed too (it is not linked any more).
        unlinkClient(sub, id);
        continue;
      }
      if ("always_ask" in c) user.alwaysAsk = c.always_ask ? [...new Set([...user.alwaysAsk, id])] : user.alwaysAsk.filter((x) => x !== id);
      if ("per_client_threshold_eur" in c) s.perClientThresholdEur = c.per_client_threshold_eur === null ? undefined : Number(c.per_client_threshold_eur);
      if ("label" in c) s.label = c.label;
      if (c.paused === false) { s.pausedUntil = undefined; s.pausedOrigin = undefined; }
      const set = trustedApplications.get(key) ?? new Set<string>();
      for (const a of c.trusted_applications_add ?? []) set.add(`${a.network}:${a.app_id}`);
      for (const a of c.trusted_applications_remove ?? []) set.delete(`${a.network}:${a.app_id}`);
      if (set.size > 0) trustedApplications.set(key, set);
      else trustedApplications.delete(key);
    }
  }

  function putSettings(req: IncomingMessage, body: Record<string, any>) {
    const d = deviceAuth(req);
    // Rule 7: a device that is not active yet (24 h after enrollment) changes nothing, tightening included.
    if (d.activeAt > now()) throw new HttpError(403, "access_denied", "this device is not active yet (rule 7)");
    const { loosening } = classifyChange(d.sub, body.change);
    if (loosening) {
      // Loosening is a consent from an active device: a `biatec_settings_change` signed with the APPROVAL key (spec 8.2, `verifyDeviceSigned`).
      const c = body.consent;
      const refuse = (m: string) => new HttpError(403, "insufficient_user_authentication", m, { acr_values: "biatec:consent", reason: "consent_required" });
      if (!c || typeof c !== "object" || !c.signed || typeof c.signature !== "string" || !c.device || typeof c.device.public_key !== "object" || c.device.public_key === null) throw refuse("loosening needs a device-signed biatec_settings_change");
      // The statement is the caller's: its key is this device's approval key (the key in the envelope is checked against the stored one, not trusted).
      let sameKey = false;
      try { sameKey = c.device.kid === d.kid && thumbprint(c.device.public_key) === thumbprint(d.publicKey as never); } catch { sameKey = false; }
      if (!sameKey) throw refuse("the statement is not signed by the approval key of the calling device");
      if (jcs(c.signed.change) !== jcs(body.change)) throw refuse("the signed change differs from the requested one");
      const used = statementNonces.get(d.kid) ?? new Set<string>();
      if (!verifyDeviceSigned(c, jwks, { issuer, now: now(), usedNonces: used }, { approverActive: true, sub: d.sub })) {
        throw refuse("the signed change does not verify (approval key, attestation, subject, nonce, issued_at, well-formed change)");
      }
      used.add(c.signed.nonce); // one atomic step in this single-threaded mock: checked above, recorded now
      statementNonces.set(d.kid, used);
    }
    applyChange(d.sub, body.change);
    hub.notify(d.sub, "settings_changed", {});
    return { status: 200, body: { applied: true, loosened: loosening, settings: settingsView(d.sub) } };
  }

  /** `GET /consent/settings`: the calling client's view of the user's consent settings (issue 27, required-changes row 20). A consent token is refused. */
  function consentSettings(req: IncomingMessage, url: URL) {
    const t = bearer(req);
    const user = userMap.get(t.sub)!;
    const client = syncProof(clientMap.get(t.clientId)!);
    const s = cs(t.sub, client.id);
    const origin = url.searchParams.get("origin");
    if (origin !== null && !ORIGIN.test(origin)) throw new HttpError(400, "invalid_request", "origin must be an absolute http(s) origin in canonical form");
    const coolingOffUntil = registeredAt(client) + COOLING_OFF_SECONDS;
    const apps = appList(t.sub, client.id);
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
    const list = [...devices.values()].filter((x) => x.sub === me.sub && !x.cancelled).map((x) => ({
      kid: x.kid, current: x.kid === me.kid, active: x.activeAt <= now(), active_at: x.activeAt, ...(x.name ? { name: x.name } : {}),
      // What a device needs to activate this one (T-242): its two key thumbprints and the enrollment nonce, and the pairing code they give. Shown for a pending device that has two keys.
      ...(x.activeAt > now() && x.presence && x.enrollNonce ? { pairing: pairingOf(x) } : {}),
    }));
    return { status: 200, body: { devices: list } };
  }

  function pairingOf(x: Device) {
    const approval = thumbprint(x.publicKey as never);
    const presence = thumbprint(x.presence!.publicKey as never);
    return { new_approval_jwk_thumbprint: approval, new_presence_jwk_thumbprint: presence, nonce: x.enrollNonce!, pairing_code: pairingCode(approval, presence, x.enrollNonce!) };
  }

  /**
   * `POST /devices/{kid}/activate` (T-242): an ACTIVE device of the same account activates a pending one by sending a `biatec_device_approval` signed with its APPROVAL key,
   * covering the new device's two keys, the enrollment nonce and the pairing code (`verifyDeviceSigned`). The nonce is single use per approver.
   */
  function activateDevice(req: IncomingMessage, kid: string, body: Record<string, any>) {
    const me = deviceAuth(req);
    if (!isActive(me)) throw new HttpError(403, "access_denied", "this device is not active yet (rule 7)");
    const target = devices.get(kid);
    // Another account's device is "not found": the approver must belong to the account of the device being enrolled.
    if (!target || target.cancelled || target.sub !== me.sub) throw new HttpError(404, "not_found");
    if (target.activeAt <= now()) throw new HttpError(409, "conflict", "this device is already active");
    if (!target.presence || !target.enrollNonce) throw new HttpError(409, "conflict", "this enrollment has no second key or nonce: it cannot be approved by a device");
    const env = body.approval;
    const refuse = (m: string) => new HttpError(403, "invalid_approval", m);
    if (!env || typeof env !== "object" || !env.signed || !env.device || typeof env.device.public_key !== "object" || env.device.public_key === null) throw refuse("a signed biatec_device_approval is required");
    // The statement is the caller's: its key is this device's approval key (the key in the envelope is checked against the stored one, not trusted).
    let sameKey = false;
    try { sameKey = env.device.kid === me.kid && thumbprint(env.device.public_key) === thumbprint(me.publicKey as never); } catch { sameKey = false; }
    if (!sameKey || env.signed.approver_kid !== me.kid) throw refuse("the approval is not signed by the key of the calling device");
    const used = statementNonces.get(me.kid) ?? new Set<string>();
    const p = pairingOf(target);
    const ok = verifyDeviceSigned(env, jwks, { issuer, now: now(), usedNonces: used }, {
      approverActive: true, sub: target.sub,
      approval: { new_kid: target.kid, new_approval_jwk_thumbprint: p.new_approval_jwk_thumbprint, new_presence_jwk_thumbprint: p.new_presence_jwk_thumbprint, nonce: target.enrollNonce },
    });
    if (!ok) throw refuse("the signed approval does not verify (approval key, pairing code, keys, nonce, issued_at)");
    used.add(env.signed.nonce); // one atomic step in this single-threaded mock: checked above, recorded now
    statementNonces.set(me.kid, used);
    target.activeAt = now();
    announceEnrolled(target);
    return { status: 200, body: { kid: target.kid, active: true, active_at: target.activeAt, approved_by: me.kid } };
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

  // --- Relying parties (protocol 1.2, spec chapter 14; Biatec OIDC issue 38) ---------------------------------------------

  /** `POST /register` (RFC 7591): only a `private_key_jwt` relying party registers itself; no client secret is ever issued. Rate limited per source address. */
  function register(req: IncomingMessage, body: Record<string, any>) {
    const addr = req.socket.remoteAddress ?? "unknown";
    const recent = (registrations.get(addr) ?? []).filter((x) => now() - x < 3600);
    if (recent.length >= maxRegistrationsPerHour) throw new HttpError(429, "rate_limited", `at most ${maxRegistrationsPerHour} registrations per hour from one address`, {}, { "Retry-After": String(Math.max(1, recent[0] + 3600 - now())) });
    registrations.set(addr, [...recent, now()]);
    const meta = (m: string) => new HttpError(400, "invalid_client_metadata", m);
    if (!body || typeof body !== "object" || Array.isArray(body)) throw meta("the body is a JSON object of client metadata");
    if (body.token_endpoint_auth_method !== "private_key_jwt") throw meta("a relying party registers with token_endpoint_auth_method private_key_jwt (no client secret is issued)");
    if (body.grant_types !== undefined && !(Array.isArray(body.grant_types) && body.grant_types.length > 0 && body.grant_types.every((g: unknown) => g === CIBA_GRANT))) throw meta("grant_types contains only " + CIBA_GRANT);
    if (body.redirect_uris !== undefined && !(Array.isArray(body.redirect_uris) && body.redirect_uris.length === 0)) throw new HttpError(400, "invalid_redirect_uri", "a relying party has no redirect URIs");
    if (body.scope !== undefined && body.scope !== "openid") throw meta("a relying party never gets a wallet scope: scope is openid or absent");
    let host: string;
    let keys: PublicJwk[];
    try {
      host = clientUriHost(body.client_uri);
      if ((body.jwks === undefined) === (body.jwks_uri === undefined)) throw new MetadataError("exactly one of jwks and jwks_uri");
      if (body.jwks_uri !== undefined) {
        if (typeof body.jwks_uri !== "string" || !body.jwks_uri.startsWith("https://") || body.jwks_uri.length > 2048) throw new MetadataError("jwks_uri is an https URL");
        keys = validateJwks(fetchJson(body.jwks_uri));
      } else keys = validateJwks(body.jwks);
    } catch (e) {
      if (e instanceof MetadataError) throw meta(e.message);
      throw e;
    }
    const id = "rp_" + randomBytes(12).toString("base64url");
    clientMap.set(id, { id, secret: "", entityId: body.client_uri, verified: false, allowManageLimits: false, rp: { keys, ...(body.jwks_uri ? { jwksUri: body.jwks_uri } : {}), keysFetchedAt: now(), clientUri: body.client_uri, host, registeredAt: now() } });
    return {
      status: 201,
      body: { client_id: id, client_id_issued_at: now(), token_endpoint_auth_method: "private_key_jwt", grant_types: [CIBA_GRANT], client_uri: body.client_uri, ...(body.jwks_uri ? { jwks_uri: body.jwks_uri } : { jwks: { keys } }) },
    };
  }

  const LINK_SECONDS = 300;
  const MAX_OPEN_LINKS_PER_CLIENT = 50;
  /** The public approval and presence keys of an account's active devices (`link-status` devices): what a relying party pins. */
  const pinnedDevices = (account: string) =>
    activeDevices(account).flatMap((d) => [
      { kid: d.kid, key_use: "approve" as const, jwk: { ...d.publicKey } },
      ...(d.presence ? [{ kid: d.presence.kid, key_use: "presence" as const, jwk: { ...d.presence.publicKey } }] : []),
    ]);
  const linkExpired = (l: Link) => l.expiresAt <= now();

  /** `POST /consent/links` (client authentication): the party starts a link inside its own authenticated session. */
  function createLink(req: IncomingMessage, body: Record<string, any>) {
    const client = authAny(req, body);
    if (!client.rp) throw new HttpError(403, "unauthorized_client", "links are for relying parties (private_key_jwt clients)");
    const { client_assertion: _a, client_assertion_type: _b, client_id: _c, ...rest } = body;
    if (Object.keys(rest).some((k) => k !== "user_label")) throw new HttpError(400, "invalid_request", "the body is { user_label? }");
    if (rest.user_label !== undefined && !isRelyingPartyText(rest.user_label, 40)) throw new HttpError(400, "invalid_request", "user_label is 1 to 40 characters of plain text (spec section 15): refused, never stripped");
    if ([...links.values()].filter((l) => l.clientId === client.id && l.status === "pending" && !linkExpired(l)).length >= MAX_OPEN_LINKS_PER_CLIENT) throw new HttpError(429, "rate_limited", `at most ${MAX_OPEN_LINKS_PER_CLIENT} open links per relying party`);
    const id = "lnk_" + randomBytes(12).toString("hex");
    const secret = randomBytes(32).toString("base64url");
    const l: Link = { id, clientId: client.id, secretHash: sha256Hex(secret), code: newCode(), attempts: 0, ...(rest.user_label !== undefined ? { userLabel: rest.user_label } : {}), nonce: "link-nonce-" + randomBytes(12).toString("hex"), expiresAt: now() + LINK_SECONDS, status: "pending", interval: POLL_INTERVAL_SECONDS };
    links.set(id, l);
    const res = { link_id: id, link_uri: `biatec-authenticator://link?iss=${encodeURIComponent(issuer)}&id=${id}&s=${secret}`, binding_code: l.code, expires_in: LINK_SECONDS, interval: POLL_INTERVAL_SECONDS };
    validate("link-created", res, "internal link answer");
    return { status: 201, body: res };
  }

  /** `GET /consent/links/{id}`: the device of an active device with the link secret (link-request), or the party that created the link (link-status). */
  function getLink(req: IncomingMessage, id: string) {
    const l = links.get(id);
    if (req.headers.authorization?.startsWith("Device ")) {
      const d = deviceAuth(req);
      const secret = req.headers["x-link-secret"];
      if (!l || typeof secret !== "string" || !safeEqual(sha256Hex(secret), l.secretHash)) throw new HttpError(404, "not_found");
      if (!isActive(d)) throw new HttpError(403, "access_denied", "this device is not active yet (rule 7)");
      if (l.status !== "pending") throw new HttpError(409, "conflict", "this link was decided already");
      if (linkExpired(l)) throw new HttpError(410, "expired_token", "the link expired");
      const c = clientMap.get(l.clientId)!;
      syncProof(c);
      const domain = c.verified && c.domain ? c.domain : null;
      l.shown = { fingerprint: clientFingerprint(c.id), domain };
      const res = { link_id: l.id, client: { fingerprint: l.shown.fingerprint, domain_proven: domain !== null, ...(domain !== null ? { domain } : {}) }, ...(l.userLabel !== undefined ? { user_label: l.userLabel } : {}), nonce: l.nonce, expires_at: l.expiresAt };
      validate("link-request", res, "internal link request");
      return { status: 200, body: res };
    }
    const client = authAny(req, {});
    if (!l || l.clientId !== client.id) throw new HttpError(404, "not_found");
    const status = l.status === "closed" || (l.status === "pending" && linkExpired(l)) ? "expired" : l.status;
    if (status === "pending") {
      // Polling faster than the interval is told to slow down, and the interval grows (as for the token endpoint).
      const tooFast = l.lastPollAt !== undefined && now() - l.lastPollAt < l.interval;
      l.lastPollAt = now();
      if (tooFast) {
        l.interval += SLOW_DOWN_STEP_SECONDS;
        throw new HttpError(400, "slow_down", "poll less often", { interval: l.interval });
      }
    }
    const res = status === "linked" ? { status, subject: l.subject, devices: pinnedDevices(l.account!), approval: l.approval } : status === "denied" ? { status, approval: l.approval } : { status };
    validate("link-status", res, "internal link status");
    return { status: 200, body: res };
  }

  /** `POST /consent/links/{id}/receipt`: the device posts the `biatec_link` statement (spec 14.3): `{ signed, signature }`, signed by the approval key for an approve. */
  function postLinkReceipt(req: IncomingMessage, id: string, body: Record<string, any>) {
    const d = deviceAuth(req);
    const l = links.get(id);
    const secret = req.headers["x-link-secret"];
    if (!l || typeof secret !== "string" || !safeEqual(sha256Hex(secret), l.secretHash)) throw new HttpError(404, "not_found"); // the secret is required here as for the fetch (spec 14.3)
    if (!isActive(d)) throw new HttpError(403, "access_denied", "this device is not active yet (rule 7)");
    if (l.status === "closed") throw new HttpError(403, "access_denied", "the binding code was entered wrongly three times", { reason: "code_failed" });
    if (l.status !== "pending" || consumedLinks.has(l.id)) throw new HttpError(409, "conflict", "this link was decided already");
    if (linkExpired(l)) throw new HttpError(410, "expired_token", "the link expired");
    if (!body || typeof body !== "object" || Object.keys(body).sort().join() !== "signature,signed" || typeof body.signature !== "string") throw new HttpError(400, "invalid_request", "the body is { signed, signature }");
    const signed = body.signed;
    validate("link-signed", signed, "link statement");
    // The statement is the caller's: it names a key of this device, and what it restates must be what the server showed.
    const key = signed.kid === d.kid ? { kid: d.kid, publicKey: d.publicKey } : d.presence && signed.kid === d.presence.kid ? d.presence : undefined;
    if (!key) throw new HttpError(400, "invalid_receipt", "the statement is not signed by a key of the calling device");
    if (!l.shown || signed.link_id !== l.id || signed.client_fingerprint !== l.shown.fingerprint || signed.domain !== l.shown.domain || signed.nonce !== l.nonce) {
      throw new HttpError(400, "receipt_mismatch", "the statement does not restate what the server showed (link, fingerprint, domain, nonce): the link request must be fetched first");
    }
    const use = key.kid === d.kid ? "approve" : "presence";
    const subject = pairwiseSubject(l.clientId, d.sub);
    const device = { kid: key.kid, key_use: use, public_key: key.publicKey, attestation: attest(subject, key.kid, key.publicKey, use) };
    // Signature, the approval key for an approve (attested use), the freshness of `issued_at`; the binding code is compared below, so that wrong codes can be counted.
    if (!verifyLink({ signed, alg: "ES256", signature: body.signature, device }, jwks, { issuer, now: now(), consumed: consumedLinks }, { link_id: l.id, client_fingerprint: l.shown.fingerprint, domain: l.shown.domain, nonce: l.nonce, binding_code: signed.binding_code, sub: subject })) {
      throw new HttpError(400, "invalid_receipt", "the link statement does not verify (signature, approval key, issued_at within 300 seconds)");
    }
    if (signed.decision === "deny") {
      l.status = "denied";
      l.approval = { signed, signature: body.signature, attestation: device.attestation };
      consumedLinks.add(l.id);
      return { status: 200, body: { status: "denied" } };
    }
    if (!safeEqual(signed.binding_code, l.code)) {
      l.attempts += 1;
      if (l.attempts >= MAX_CODE_ATTEMPTS) {
        l.status = "closed";
        throw new HttpError(403, "access_denied", "the third wrong binding code closes the link", { reason: "code_failed", attempts_remaining: 0 });
      }
      throw new HttpError(400, "wrong_code", "the binding code does not match", { attempts_remaining: MAX_CODE_ATTEMPTS - l.attempts });
    }
    l.status = "linked";
    l.account = d.sub;
    l.subject = subject;
    l.approval = { signed, signature: body.signature, attestation: device.attestation };
    consumedLinks.add(l.id);
    subjects.set(subject, { account: d.sub, clientId: l.clientId });
    // The user linked the client: it is a client the user knows (as after a first approved request), and the other devices learn that settings changed.
    const user = userMap.get(d.sub)!;
    if (!user.used.has(l.clientId)) linkedAt.set(d.sub + "|" + l.clientId, now());
    user.used.add(l.clientId);
    firstSigned.set(d.sub + "|" + l.clientId, firstSigned.get(d.sub + "|" + l.clientId) ?? now());
    hub.notify(d.sub, "settings_changed", {});
    return { status: 200, body: { status: "linked" } };
  }

  /** What leaving a link means for the requests a relying party still has open: they are closed (denied, without a receipt). */
  function closeRelyingPartyRequests(account: string, clientId: string) {
    for (const p of pending.values()) {
      if (p.rp && p.account === account && p.clientId === clientId && p.status === "pending") {
        p.status = "denied";
        p.closedBy = "unlinked";
        announceClosed(p);
      }
    }
  }

  /** The link of a subject of the calling relying party, or 404 (the same answer for a subject that never existed). */
  function linkedSubject(req: IncomingMessage, subject: string) {
    const client = authAny(req, {});
    const m = subjects.get(subject);
    const user = m && m.clientId === client.id ? userMap.get(m.account) : undefined;
    if (!m || !user || !user.used.has(client.id)) throw new HttpError(404, "not_found");
    return { client, account: m.account, user };
  }

  /** `DELETE /consent/links/subject/{subject}`: the party removes its own link; the user removes it on the phone. */
  function deleteLink(req: IncomingMessage, subject: string) {
    const { client, account } = linkedSubject(req, subject);
    unlinkClient(account, client.id);
    return { status: 200, body: { unlinked: true } };
  }

  /** `GET /consent/links/subject/{subject}/devices`: the current approval and presence keys of the account's active devices (a new phone shows up here, spec 14.6). */
  function subjectDevices(req: IncomingMessage, subject: string) {
    const { account } = linkedSubject(req, subject);
    const list = pinnedDevices(account);
    const v = ajv.getSchema("https://schemas.biatec.io/consent/v1/link-status.schema.json#/$defs/devices")!;
    if (!v(list)) throw new HttpError(500, "server_error", "internal devices list: " + ajv.errorsText(v.errors));
    return { status: 200, body: { devices: list } };
  }

  /** Unlinking (by the party or on the phone): the client is a stranger again (first use), and its open relying-party requests are closed. */
  function unlinkClient(account: string, clientId: string) {
    const user = userMap.get(account)!;
    const key = account + "|" + clientId;
    user.used.delete(clientId);
    user.alwaysAsk = user.alwaysAsk.filter((x) => x !== clientId);
    trustedApplications.delete(key);
    linkedAt.delete(key);
    firstSigned.delete(key); // the first day with the client starts again: the next request is truly first use
    const s = clientSettings.get(key);
    if (s) {
      s.label = undefined;
      s.perClientThresholdEur = undefined;
    }
    closeRelyingPartyRequests(account, clientId);
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
    requestEpoch++;
    const url = new URL(req.url ?? "/", "http://mock");
    const path = url.pathname;
    const m = req.method ?? "GET";
    let x: RegExpMatchArray | null;
    if (m === "GET" && path === "/.well-known/jwks.json") return { status: 200, body: jwks };
    if (m === "GET" && path === "/.well-known/openid-configuration") {
      return { status: 200, body: { issuer, jwks_uri: "/.well-known/jwks.json", authorization_endpoint: "/authorize", token_endpoint: "/token", code_challenge_methods_supported: ["S256"], scopes_supported: ["openid", "enroll-device"], backchannel_authentication_endpoint: "/connect/ciba", backchannel_token_delivery_modes_supported: ["poll"], grant_types_supported: ["urn:openid:params:grant-type:ciba", "authorization_code"], acr_values_supported: ["biatec:consent"], authorization_details_types_supported: ["biatec_transaction", "biatec_limits_change", "biatec_payment", "biatec_login", "biatec_action"], registration_endpoint: "/register", token_endpoint_auth_methods_supported: ["client_secret_basic", "client_secret_post", "private_key_jwt"], token_endpoint_auth_signing_alg_values_supported: ["ES256", "PS256"] } };
    }
    if (m === "POST" && path === "/connect/ciba") return ciba(req, await readBody(req));
    // Relying parties (protocol 1.2, spec chapter 14)
    if (m === "POST" && path === "/register") return register(req, await readBody(req));
    if (m === "POST" && path === "/consent/links") return createLink(req, await readBody(req));
    if (m === "GET" && (x = path.match(/^\/consent\/links\/subject\/([^/]+)\/devices$/))) return subjectDevices(req, decodeURIComponent(x[1]));
    if (m === "DELETE" && (x = path.match(/^\/consent\/links\/subject\/([^/]+)$/))) return deleteLink(req, decodeURIComponent(x[1]));
    if (m === "GET" && (x = path.match(/^\/consent\/links\/([^/]+)$/))) return getLink(req, x[1]);
    if (m === "POST" && (x = path.match(/^\/consent\/links\/([^/]+)\/receipt$/))) return postLinkReceipt(req, x[1], await readBody(req));
    // Biatec OIDC issue 38 names the receipt retrieval of the party `GET /consent/requests/{auth_req_id}/receipt`: the same answer as `GET /consent/receipts/{auth_req_id}`.
    if (m === "GET" && (x = path.match(/^\/consent\/requests\/([^/]+)\/receipt$/))) return getReceipt(req, x[1]);
    if (m === "GET" && path === "/authorize") return authorize(url);
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
      // One code per device, for its whole life: issued (re-served until the user confirmed saving it), acknowledged (never shown again, still accepted at enrollment), consumed (used by an activation: gone for good, not regenerated).
      if (d.recovery && d.recovery.state !== "issued") throw new HttpError(409, "conflict", d.recovery.state === "consumed" ? "the recovery code was used" : "the recovery code was already shown");
      d.recovery ??= { code: randomBytes(6).toString("hex").toUpperCase().replace(/(.{4})(?=.)/g, "$1-"), state: "issued" };
      return { status: 200, body: { recovery_code: d.recovery.code } };
    }
    if (m === "POST" && path === "/devices/recovery-code/ack") {
      const d = deviceAuth(req);
      if (d.activeAt > now() || d.recovery?.state !== "issued") throw new HttpError(409, "conflict", "no recovery code is pending");
      d.recovery.state = "acknowledged"; // the server keeps it: it activates a later device
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
    if (m === "POST" && (x = path.match(/^\/devices\/([^/]+)\/activate$/))) return activateDevice(req, decodeURIComponent(x[1]), await readBody(req));
    if (m === "POST" && (x = path.match(/^\/devices\/([^/]+)\/cancel$/))) return cancelDevice(req, decodeURIComponent(x[1]));
    if (m === "GET" && path === "/consent/settings") return consentSettings(req, url);
    if (m === "GET" && path === "/devices/settings") return { status: 200, body: settingsView(deviceAuth(req).sub) };
    if (m === "PUT" && path === "/devices/settings") return putSettings(req, await readBody(req));
    if (m === "GET" && path === "/devices/requests") {
      const d = deviceAuth(req);
      return { status: 200, body: { requests: [...pending.values()].filter((p) => p.account === d.sub && p.status === "pending" && !expired(p)).map(deviceView) } };
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
      s.pausedOrigin = undefined;
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
    if (m === "POST" && path === "/mock/serve") {
      // What the real server fetches with its hardened fetcher: the well-known file of a relying party and a jwks_uri. Body { url, json } (json null removes it).
      const b = await readBody(req);
      if (typeof b.url !== "string" || !b.url.startsWith("https://")) throw new HttpError(400, "invalid_request", "url is an https URL");
      if (b.json === null || b.json === undefined) served.delete(b.url);
      else served.set(b.url, b.json);
      return { status: 200, body: { served: b.json !== null && b.json !== undefined } };
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
      (r) => send(res, r.status, r.body, (r as { headers?: Record<string, string> }).headers ?? {}),
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
    // Single-use assertions are remembered until they expire, links until an hour after they ended: nothing grows without bound.
    for (const [k, until] of usedAssertions) if (until <= now()) usedAssertions.delete(k);
    for (const [id, l] of links) if (l.expiresAt + 3600 <= now()) { links.delete(id); consumedLinks.delete(id); }
    for (const m of [registrations, rpOpened]) for (const [k, times] of m) { const recent = times.filter((x) => now() - x < 3600); if (recent.length === 0) m.delete(k); else if (recent.length !== times.length) m.set(k, recent); }
  }, 1000);
  sweep.unref();
  server.on("close", () => clearInterval(sweep));
  function send(res: ServerResponse, status: number, body: unknown, headers: Record<string, string>) {
    res.writeHead(status, { "Content-Type": "application/json", "Cache-Control": "no-store", "X-Biatec-Min-App-Build": String(minAppBuild), ...headers });
    res.end(JSON.stringify(body));
  }

  /** Serves a document to the mock's stand-in fetcher: the well-known file of a relying party (`https://<host>/.well-known/biatec-client.json`) or a `jwks_uri`. `null` removes it. */
  const serve = (url: string, json: unknown) => {
    if (json === null) served.delete(url);
    else served.set(url, json);
  };
  return { server, issuer, jwks, keys, hub, serve, pairwiseSubject, state: { pending, consumed, devices, users: userMap, tokens, pushOutbox, accountSettings, clientSettings, trustedApplications, firstSigned, noDeviceSpends } };
}
