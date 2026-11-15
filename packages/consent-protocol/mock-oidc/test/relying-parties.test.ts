// Protocol 1.2, relying parties (T-246, spec chapter 14, Biatec OIDC issue 38): registration with `private_key_jwt`, the client assertion, linking, the three request
// types, the receipt with the pinned device key, and every refusal. Mock outputs are validated against the schemas (ajv, strict).
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { createHmac, generateKeyPairSync } from "node:crypto";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { AddressInfo } from "node:net";
import Ajv2020Module from "ajv/dist/2020.js";
import { clientFingerprint, detailsHash, jcs, signEs256, verifyApproval, verifyLink, verifyLinkPinned, verifyReceipt, verifyReceiptPinned } from "../../scripts/lib.mjs";
import { createMockOidc } from "../src/app.ts";
import { account, buildGroup, usdcTransfer } from "../src/build.ts";
import { approveLink, approveRequest, clientAssertion, parseLinkUri, generateRpKey, httpJson, proveDomain, registerRelyingParty, rpCall, testDevice, vectorKeys, type RelyingParty, type TestDevice } from "../src/test-device.ts";

const Ajv2020 = Ajv2020Module as unknown as typeof Ajv2020Module.default;
const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const load = (...p: string[]) => JSON.parse(readFileSync(join(root, ...p), "utf8"));
const ajv = new Ajv2020({ allErrors: true, strict: true });
for (const f of readdirSync(join(root, "schemas")).filter((n) => n.endsWith(".schema.json"))) ajv.addSchema(load("schemas", f));
const valid = (name: string, data: unknown) => {
  const v = ajv.getSchema(`https://schemas.biatec.io/consent/v1/${name}.schema.json`)!;
  assert.ok(v(data), name + " " + ajv.errorsText(v.errors));
};

let skew = 0;
const now = () => Math.floor(Date.now() / 1000) + skew;
const mock = createMockOidc({ now, testHelpers: true, maxRegistrationsPerHour: 1_000_000 });
let base = "";
before(async () => {
  await new Promise<void>((r) => mock.server.listen(0, "127.0.0.1", r));
  base = `http://127.0.0.1:${(mock.server.address() as AddressInfo).port}`;
});
after(() => mock.server.close());

const CODE = "483920";
let seq = 0;
const PAYMENT = { type: "biatec_payment", instructed_amount: { amount: "1250.00", currency: "EUR" }, creditor_name: "Muster Energie GmbH", creditor_account: { iban: "DE89370400440532013000" }, remittance: "Invoice 2026-0042", execution_date: "2026-10-20" };
const LOGIN = { type: "biatec_login", context: { ip: "203.0.113.42", location: "Bratislava, Slovakia", user_agent: "Firefox 133 on Windows 11" } };
const ACTION = { type: "biatec_action", title: "Add payee", lines: [{ label: "Payee", value: "Muster Energie GmbH" }, { label: "Account", value: "DE89 3704 0044 0532 0130 00" }] };
const ORIGIN = "https://netbanking.bank.example";

/** A registered relying party (a key in memory) whose domain is proven (or not). */
async function newRp(o: { prove?: boolean; alg?: "ES256" | "PS256" } = {}) {
  const host = `bank${++seq}.example`;
  const { rp, reply } = await registerRelyingParty(base, mock.issuer, { clientUri: `https://${host}`, alg: o.alg });
  assert.equal(reply.status, 201, JSON.stringify(reply.json));
  rp.clock = now;
  if (o.prove !== false) assert.equal((await proveDomain(rp, host)).status, 200);
  return { rp, host };
}

/** Registration, link, the phone approves: a linked relying party and what it pinned. */
async function linked(o: { prove?: boolean; user?: string; alg?: "ES256" | "PS256" } = {}) {
  const { rp, host } = await newRp(o);
  const created = await rpCall(rp, "POST", "/consent/links", { body: { user_label: "Alex M. (account ending 4421)" } });
  assert.equal(created.status, 201, JSON.stringify(created.json));
  valid("link-created", created.json);
  const dev = await testDevice(base, o.user ?? "alice");
  const { linkRequest, posted } = await approveLink(base, dev, created.json.link_uri, { code: created.json.binding_code, now: now() });
  assert.equal(posted!.status, 200, JSON.stringify(posted!.json));
  const status = await rpCall(rp, "GET", `/consent/links/${created.json.link_id}`);
  assert.equal(status.status, 200, JSON.stringify(status.json));
  valid("link-status", status.json);
  assert.equal(status.json.status, "linked");
  return { rp, host, dev, created: created.json, linkRequest: linkRequest.json, status: status.json, subject: status.json.subject as string, pins: status.json.devices as { kid: string; key_use: "approve" | "presence"; jwk: { kty: string; crv: string; x: string; y: string } }[] };
}
type Linked = Awaited<ReturnType<typeof linked>>;

const open = (l: Linked, details: object, extra: Record<string, unknown> = {}) => rpCall(l.rp, "POST", "/connect/ciba", { body: { scope: "openid", login_hint: l.subject, authorization_details: [details], ...extra } });
const tokenOf = (l: Linked, id: string) => rpCall(l.rp, "POST", "/token", { form: { grant_type: "urn:openid:params:grant-type:ciba", auth_req_id: id } });

/** Opens a request, the phone approves it (or not), and the party redeems the token. */
async function requestFlow(l: Linked, details: object, opts: { origin?: string; decision?: "approve" | "deny"; presence?: boolean } = {}) {
  const o = await open(l, details, opts.origin ? { origin: opts.origin } : {});
  assert.equal(o.status, 200, JSON.stringify(o.json));
  valid("backchannel-authentication-response", o.json);
  const a = await approveRequest(base, l.dev, o.json.auth_req_id, { code: o.json.binding_code, decision: opts.decision, presence: opts.presence, now: now() });
  return { opened: o.json, ...a };
}

/** The party's own values for verifyReceiptPinned: the id it got, the subject, its fingerprint, the code it displayed, the ids it already acted on. */
const pinOpts = (l: Linked, o: { auth_req_id: string; binding_code: string }, extra: Record<string, unknown> = {}) => ({ auth_req_id: o.auth_req_id, sub: l.subject, clientFingerprint: clientFingerprint(l.rp.clientId), bindingCode: o.binding_code, consumed: new Set<string>(), origin: null as string | null, now: now(), ...extra });

// ---------------------------------------------------------------------------------------------------------------
// Registration

test("registration: a private_key_jwt client is created, no secret is issued, the answer names no wallet capability", async () => {
  const { rp, reply } = await registerRelyingParty(base, mock.issuer, { clientUri: "https://reg1.example" });
  assert.equal(reply.status, 201);
  assert.match(reply.json.client_id, /^rp_/);
  assert.equal(reply.json.token_endpoint_auth_method, "private_key_jwt");
  assert.deepEqual(reply.json.grant_types, ["urn:openid:params:grant-type:ciba"]);
  assert.equal(reply.json.client_secret, undefined);
  assert.equal(JSON.stringify(reply.json).includes('"d"'), false);
  assert.equal(rp.clientId, reply.json.client_id);
  const cfg = (await httpJson(base, "GET", "/.well-known/openid-configuration")).json;
  assert.ok(cfg.authorization_details_types_supported.includes("biatec_payment"));
  assert.equal(cfg.registration_endpoint, "/register");
});

test("registration refusals: auth method, grant types, redirect URIs, wallet scopes, client_uri, jwks, a private member", async () => {
  const key = generateRpKey().publicJwk;
  const good = { token_endpoint_auth_method: "private_key_jwt", client_uri: "https://reg2.example", jwks: { keys: [key] } };
  const post = (b: Record<string, unknown>) => httpJson(base, "POST", "/register", { body: b });
  assert.equal((await post(good)).status, 201);
  const refused = async (label: string, b: Record<string, unknown>, error = "invalid_client_metadata") => {
    const r = await post(b);
    assert.deepEqual([r.status, r.json?.error], [400, error], label + " " + JSON.stringify(r.json));
    assert.equal(r.json.client_secret, undefined);
  };
  await refused("a public client", { ...good, token_endpoint_auth_method: "none" });
  await refused("a secret client", { ...good, token_endpoint_auth_method: "client_secret_basic" });
  await refused("an authorization_code grant", { ...good, grant_types: ["urn:openid:params:grant-type:ciba", "authorization_code"] });
  await refused("a redirect URI", { ...good, redirect_uris: ["https://reg2.example/cb"] }, "invalid_redirect_uri");
  await refused("scope sign", { ...good, scope: "openid sign" });
  await refused("scope enroll-device", { ...good, scope: "enroll-device" });
  await refused("no client_uri", { ...good, client_uri: undefined });
  await refused("an http client_uri", { ...good, client_uri: "http://reg2.example" });
  await refused("an IP address as client_uri", { ...good, client_uri: "https://203.0.113.9/" });
  await refused("a single-label host", { ...good, client_uri: "https://localhost/" });
  await refused("client_uri with credentials", { ...good, client_uri: "https://user:pw@reg2.example" });
  await refused("neither jwks nor jwks_uri", { ...good, jwks: undefined });
  await refused("both jwks and jwks_uri", { ...good, jwks_uri: "https://reg2.example/jwks.json" });
  await refused("a jwks_uri that is not served", { ...good, jwks: undefined, jwks_uri: "https://reg2.example/jwks.json" });
  await refused("a private member (d)", { ...good, jwks: { keys: [generateRpKey().privateJwk] } });
  await refused("more than 4 keys", { ...good, jwks: { keys: Array.from({ length: 5 }, (_, i) => ({ ...generateRpKey().publicJwk, kid: "k" + i })) } });
  await refused("no keys", { ...good, jwks: { keys: [] } });
  await refused("a symmetric key", { ...good, jwks: { keys: [{ kty: "oct", k: "c2VjcmV0" }] } });
  await refused("a P-384 key", { ...good, jwks: { keys: [{ kty: "EC", crv: "P-384", x: key.x, y: key.y }] } });
  const small = generateKeyPairSync("rsa", { modulusLength: 1024 }).publicKey.export({ format: "jwk" }) as Record<string, string>;
  await refused("an RSA key of 1024 bits", { ...good, jwks: { keys: [small] } });
  const big = generateKeyPairSync("rsa", { modulusLength: 2048 }).publicKey.export({ format: "jwk" }) as Record<string, string>;
  assert.equal((await post({ ...good, client_uri: "https://reg3.example", jwks: { keys: [big] } })).status, 201, "RSA 2048 is accepted");
});

test("registration with a jwks_uri: fetched through the stand-in fetcher, and refreshed after an hour", async () => {
  const k1 = generateRpKey("ES256", "a");
  mock.serve("https://reg4.example/jwks.json", { keys: [k1.publicJwk] });
  const r = await httpJson(base, "POST", "/register", { body: { token_endpoint_auth_method: "private_key_jwt", client_uri: "https://reg4.example", jwks_uri: "https://reg4.example/jwks.json" } });
  assert.equal(r.status, 201, JSON.stringify(r.json));
  const rp: RelyingParty = { base, issuer: mock.issuer, clientId: r.json.client_id, key: k1, clock: now };
  assert.equal((await rpCall(rp, "POST", "/consent/links", { body: {} })).status, 201);
  const k2 = generateRpKey("ES256", "b");
  mock.serve("https://reg4.example/jwks.json", { keys: [k2.publicJwk] });
  assert.equal((await rpCall(rp, "POST", "/consent/links", { body: {} })).status, 201, "the cached keys are used for an hour");
  skew += 3601;
  try {
    assert.equal((await rpCall({ ...rp, key: k1 }, "POST", "/consent/links", { body: {} })).status, 401, "the old key is gone after the refresh");
    assert.equal((await rpCall({ ...rp, key: k2 }, "POST", "/consent/links", { body: {} })).status, 201);
  } finally {
    skew -= 3601;
  }
});

test("registration is rate limited per source address", async () => {
  const limited = createMockOidc({ maxRegistrationsPerHour: 2 });
  await new Promise<void>((r) => limited.server.listen(0, "127.0.0.1", r));
  const b = `http://127.0.0.1:${(limited.server.address() as AddressInfo).port}`;
  try {
    const statuses: number[] = [];
    for (let i = 0; i < 3; i++) statuses.push((await registerRelyingParty(b, limited.issuer, { clientUri: `https://lim${i}.example` })).reply.status);
    assert.deepEqual(statuses, [201, 201, 429]);
  } finally {
    limited.server.close();
  }
});

// ---------------------------------------------------------------------------------------------------------------
// The client assertion (RFC 7523)

test("assertion: a good one works for ES256 and PS256; a replay, a wrong audience, an expired, a far one, none, HS256, another iss and another key are 401", async () => {
  for (const alg of ["ES256", "PS256"] as const) {
    const { rp } = await newRp({ alg });
    const jwt = clientAssertion(rp.key, rp.clientId, mock.issuer, { now: now() });
    assert.equal((await rpCall(rp, "POST", "/consent/links", { body: {}, assertion: jwt })).status, 201, alg);
    const replay = await rpCall(rp, "POST", "/consent/links", { body: {}, assertion: jwt });
    assert.deepEqual([replay.status, replay.json.error], [401, "invalid_client"], alg + " replay");
    assert.match(replay.json.error_description, /jti/);
  }
  const { rp } = await newRp();
  const fails = async (label: string, assertion: string) => {
    const r = await rpCall(rp, "POST", "/consent/links", { body: {}, assertion });
    assert.deepEqual([r.status, r.json?.error], [401, "invalid_client"], label + " " + JSON.stringify(r.json));
  };
  const t = now();
  await fails("the issuer's token endpoint is fine but another audience is not", clientAssertion(rp.key, rp.clientId, "https://evil.example", { now: t }));
  assert.equal((await rpCall(rp, "POST", "/consent/links", { body: {}, assertion: clientAssertion(rp.key, rp.clientId, mock.issuer + "/token", { now: t }) })).status, 201, "aud may be the token endpoint");
  await fails("expired", clientAssertion(rp.key, rp.clientId, mock.issuer, { now: t - 120, exp: t - 60 }));
  await fails("more than 5 minutes ahead", clientAssertion(rp.key, rp.clientId, mock.issuer, { now: t, exp: t + 301 }));
  await fails("iss is not the client", clientAssertion(rp.key, rp.clientId, mock.issuer, { now: t, iss: "someone" }));
  await fails("sub is not the client", clientAssertion(rp.key, rp.clientId, mock.issuer, { now: t, sub: "someone" }));
  await fails("another key", clientAssertion(generateRpKey("ES256", rp.key.kid), rp.clientId, mock.issuer, { now: t }));
  await fails("an unknown client", clientAssertion(rp.key, "rp_unknown", mock.issuer, { now: t, iss: "rp_unknown", sub: "rp_unknown" }));
  const b = (o: object) => Buffer.from(JSON.stringify(o)).toString("base64url");
  const payload = { iss: rp.clientId, sub: rp.clientId, aud: mock.issuer, exp: t + 60, jti: "0123456789abcdef" };
  await fails("alg none", `${b({ alg: "none" })}.${b(payload)}.`);
  const h = `${b({ alg: "HS256", kid: rp.key.kid })}.${b(payload)}`;
  const publicAsSecret = JSON.stringify(rp.key.publicJwk);
  await fails("HS256 with the public key as the secret", `${h}.${createHmac("sha256", publicAsSecret).update(h).digest("base64url")}`);
  await fails("a malformed assertion", "not.a.jwt");
  // no assertion at all
  const none = await httpJson(base, "POST", "/consent/links", { body: {} });
  assert.equal(none.status, 401);
  const wrongType = await httpJson(base, "POST", "/consent/links", { body: { client_assertion: clientAssertion(rp.key, rp.clientId, mock.issuer, { now: t }), client_assertion_type: "urn:other" } });
  assert.equal(wrongType.status, 401);
});

test("a relying party has no secret: Basic with its id and an empty or any secret is refused", async () => {
  const { rp } = await newRp();
  for (const secret of ["", "x", "private_key_jwt"]) {
    const r = await httpJson(base, "POST", "/connect/ciba", { headers: { Authorization: "Basic " + Buffer.from(`${rp.clientId}:${secret}`).toString("base64") }, form: { scope: "openid", login_hint: "x" } });
    assert.equal(r.status, 401, JSON.stringify(secret));
  }
});

test("a private_key_jwt client can never obtain sign, manage-limits or a wallet token", async () => {
  const l = await linked();
  const q = (path: string, body: Record<string, unknown>) => rpCall(l.rp, "POST", path, { body });
  for (const scope of ["sign", "manage-limits", "openid sign", "enroll-device"]) {
    const g = buildGroup([usdcTransfer(account("alice"), account("payee"), 5000)]);
    const r = await q("/connect/ciba", { scope, login_hint: "alice@example.com", resource: "https://api.biatec.io", origin: "https://x.example", network: "algorand-testnet", address: account("alice"), unsigned_group: g.items, limits_request: { per_transaction_eur: "1", per_day_eur: "1" } });
    assert.deepEqual([r.status, r.json.error], [403, "unauthorized_client"], scope);
  }
  // not by login_hint of a wallet user either: the party knows only its own subject
  assert.equal((await open(l, PAYMENT, {})).status, 200);
  assert.equal((await rpCall(l.rp, "POST", "/connect/ciba", { body: { scope: "openid", login_hint: "alice@example.com", authorization_details: [PAYMENT] } })).json.error, "unknown_user_id");
  // other grants and the signing endpoint
  const g = await rpCall(l.rp, "POST", "/token", { form: { grant_type: "authorization_code", code: "x", redirect_uri: "x", code_verifier: "x" } });
  assert.ok(g.status >= 400 && g.status < 500);
  const rg = await rpCall(l.rp, "POST", "/token", { form: { grant_type: "client_credentials", scope: "sign" } });
  assert.deepEqual([rg.status, rg.json.error], [400, "unauthorized_client"]);
  const signAttempt = await rpCall(l.rp, "POST", `/wallet/algorand-testnet/${account("alice")}/sign`, { body: { transactions: buildGroup([usdcTransfer(account("alice"), account("payee"), 1)]).items } });
  assert.equal(signAttempt.status, 401);
  assert.equal((await rpCall(l.rp, "GET", "/consent/settings")).status, 401);
  assert.equal((await rpCall(l.rp, "GET", "/devices")).status, 401);
  assert.equal((await rpCall(l.rp, "POST", "/devices", { body: { kid: "x", public_key: vectorKeys.device } })).status, 401);
  // and a seeded wallet client has no use for a relying party's types
  const w = await httpJson(base, "POST", "/connect/ciba", { headers: { Authorization: "Basic " + Buffer.from("bank-example:bank-example-secret").toString("base64") }, body: { scope: "openid", login_hint: "alice@example.com", authorization_details: [PAYMENT] } });
  assert.deepEqual([w.status, w.json.error], [403, "unauthorized_client"]);
  const wl = await httpJson(base, "POST", "/consent/links", { headers: { Authorization: "Basic " + Buffer.from("bank-example:bank-example-secret").toString("base64") }, body: {} });
  assert.equal(wl.status, 403, "a wallet client has no links");
});

// ---------------------------------------------------------------------------------------------------------------
// Linking

test("link: the round trip, the answers match the schemas, the party verifies the approval with the pinned key and with the attestation", async () => {
  const l = await linked();
  valid("link-request", l.linkRequest);
  assert.deepEqual(l.linkRequest.client, { fingerprint: clientFingerprint(l.rp.clientId), domain_proven: true, domain: l.host });
  assert.equal(l.linkRequest.user_label, "Alex M. (account ending 4421)");
  assert.match(l.created.link_uri, /^biatec-authenticator:\/\/link\?iss=https%3A%2F%2Foidc\.biatec\.test&id=lnk_[0-9a-f]{24}&s=[A-Za-z0-9_-]{43}$/);
  assert.equal(new URL(l.created.link_uri).searchParams.get("iss"), mock.issuer);
  assert.equal(l.created.expires_in, 300);
  assert.equal(l.created.interval, 2);
  assert.equal(l.status.subject, mock.pairwiseSubject(l.rp.clientId, "pw_alice"), "the pairwise subject of this account for this client");
  assert.notEqual(l.status.subject, "pw_alice");
  // the pins: the approval and the presence key of the active device
  assert.deepEqual(l.pins.map((d) => [d.kid, d.key_use]).sort(), [["alice-device", "approve"], ["alice-device-presence", "presence"]]);
  const expect = { link_id: l.created.link_id, client_fingerprint: clientFingerprint(l.rp.clientId), domain: l.host, nonce: l.linkRequest.nonce, binding_code: l.created.binding_code };
  assert.equal(verifyLinkPinned({ signed: l.status.approval.signed, signature: l.status.approval.signature, alg: "ES256" as const }, l.pins, expect, { now: now(), consumed: new Set() }), true, "pinned mode: no Biatec key involved");
  assert.equal(verifyLinkPinned({ signed: l.status.approval.signed, signature: l.status.approval.signature, alg: "ES256" as const }, l.pins, { ...expect, domain: null }, { now: now(), consumed: new Set() }), false, "another domain than the one signed");
  const envelope = { signed: l.status.approval.signed, alg: "ES256", signature: l.status.approval.signature, device: { kid: "alice-device", key_use: "approve", public_key: l.pins[0].jwk, attestation: l.status.approval.attestation } };
  assert.equal(verifyLink(envelope, mock.jwks, { issuer: mock.issuer, consumed: new Set(), now: now() }, { ...expect, sub: l.subject }), true, "attestation mode");
  // the link is single use and the user now has the client
  assert.equal(mock.state.users.get("pw_alice")!.used.has(l.rp.clientId), true);
});

test("link: without a proven domain the statement carries domain null and the card shows the fingerprint", async () => {
  const l = await linked({ prove: false });
  assert.deepEqual(l.linkRequest.client, { fingerprint: clientFingerprint(l.rp.clientId), domain_proven: false });
  assert.equal(l.status.approval.signed.domain, null);
  valid("link-signed", l.status.approval.signed);
});

test("link: a wrong secret, another client, an unknown id, another device account and a pending device cannot read it", async () => {
  const { rp } = await newRp();
  const other = (await newRp()).rp;
  const c = (await rpCall(rp, "POST", "/consent/links", { body: {} })).json;
  const { id, secret } = { id: c.link_id, secret: new URL(c.link_uri).searchParams.get("s")! };
  const dev = await testDevice(base, "alice");
  const dget = (s: string | undefined, d = dev) => httpJson(base, "GET", `/consent/links/${id}`, { headers: { Authorization: d.auth, ...(s ? { "X-Link-Secret": s } : {}) } });
  assert.equal((await dget("A".repeat(43))).status, 404, "wrong secret");
  assert.equal((await dget(undefined)).status, 404, "no secret");
  assert.equal((await rpCall(other, "GET", `/consent/links/${id}`)).status, 404, "another client");
  assert.equal((await rpCall(rp, "GET", "/consent/links/lnk_does_not_exist_0000")).status, 404);
  assert.equal((await httpJson(base, "GET", `/consent/links/${id}`, { headers: { Authorization: "Device nope", "X-Link-Secret": secret } })).status, 401);
  // a device that is not active (enrolled just now: 24 hours) is refused
  const key = generateKeyPairSync("ec", { namedCurve: "P-256" }).publicKey.export({ format: "jwk" }) as Record<string, string>;
  const enrolled = await httpJson(base, "POST", "/devices", { headers: { Authorization: "Bearer login-alice" }, body: { kid: "pending-link-device", public_key: key } });
  assert.equal(enrolled.status, 201, JSON.stringify(enrolled.json));
  assert.equal(enrolled.json.active, false);
  const pendingDev = { ...dev, auth: "Device " + enrolled.json.device_token };
  assert.equal((await dget(secret, pendingDev)).status, 403, "an inactive device");
  assert.equal((await dget(secret)).status, 200);
});

test("link: three wrong codes close it, a presence-key approve, another domain and a stale statement are refused", async () => {
  const dev = await testDevice(base, "alice");
  const start = async (o: { prove?: boolean } = {}) => {
    const { rp } = await newRp(o);
    const c = (await rpCall(rp, "POST", "/consent/links", { body: {} })).json;
    return { rp, c };
  };
  // wrong code
  let { rp, c } = await start();
  const wrong = await approveLink(base, dev, c.link_uri, { code: "000000", now: now() });
  assert.deepEqual([wrong.posted!.status, wrong.posted!.json.error, wrong.posted!.json.attempts_remaining], [400, "wrong_code", 2]);
  const wrong2 = await approveLink(base, dev, c.link_uri, { code: "111111", now: now() });
  assert.equal(wrong2.posted!.json.attempts_remaining, 1);
  const wrong3 = await approveLink(base, dev, c.link_uri, { code: "222222", now: now() });
  assert.deepEqual([wrong3.posted!.status, wrong3.posted!.json.error, wrong3.posted!.json.reason], [403, "access_denied", "code_failed"]);
  const closed = await approveLink(base, dev, c.link_uri, { code: c.binding_code, now: now() });
  assert.equal(closed.linkRequest.status, 409, "the closed link is not served any more");
  assert.equal((await rpCall(rp, "GET", `/consent/links/${c.link_id}`)).json.status, "expired");
  // the presence key never approves
  ({ rp, c } = await start());
  const presence = await approveLink(base, dev, c.link_uri, { code: c.binding_code, presence: true, now: now() });
  assert.deepEqual([presence.posted!.status, presence.posted!.json.error], [400, "invalid_receipt"]);
  assert.equal((await rpCall(rp, "GET", `/consent/links/${c.link_id}`)).json.status, "pending");
  // ... but it may deny
  const denyP = await approveLink(base, dev, c.link_uri, { code: "", decision: "deny", presence: true, now: now() });
  assert.equal(denyP.posted!.status, 200, JSON.stringify(denyP.posted!.json));
  // a statement about another domain than the one shown
  ({ rp, c } = await start());
  const domain = await approveLink(base, dev, c.link_uri, { code: c.binding_code, change: { domain: "evil.example" }, now: now() });
  assert.deepEqual([domain.posted!.status, domain.posted!.json.error], [400, "receipt_mismatch"], "wrong domain refused");
  const nulled = await approveLink(base, dev, c.link_uri, { code: c.binding_code, change: { domain: null }, now: now() });
  assert.equal(nulled.posted!.status, 400, "no domain where one was shown");
  const fp = await approveLink(base, dev, c.link_uri, { code: c.binding_code, change: { client_fingerprint: "0123456789abcdef" }, now: now() });
  assert.equal(fp.posted!.status, 400);
  const nonce = await approveLink(base, dev, c.link_uri, { code: c.binding_code, change: { nonce: "link-nonce-" + "0".repeat(24) }, now: now() });
  assert.equal(nonce.posted!.status, 400);
  const stale = await approveLink(base, dev, c.link_uri, { code: c.binding_code, now: now() - 301 });
  assert.deepEqual([stale.posted!.status, stale.posted!.json.error], [400, "invalid_receipt"], "issued_at more than 300 seconds off");
  const wrongKid = await approveLink(base, dev, c.link_uri, { code: c.binding_code, change: { kid: "other-device" }, now: now() });
  assert.equal(wrongKid.posted!.status, 400);
  assert.equal((await rpCall(rp, "GET", `/consent/links/${c.link_id}`)).json.status, "pending", "nothing above linked anyone");
  const ok = await approveLink(base, dev, c.link_uri, { code: c.binding_code, now: now() });
  assert.equal(ok.posted!.status, 200);
  const again = await approveLink(base, dev, c.link_uri, { code: c.binding_code, now: now() });
  assert.equal(again.linkRequest.status, 409, "a link is single use");
});

test("link: a deny is a result (status denied with the approval), expiry, slow_down and the user_label rules", async () => {
  const dev = await testDevice(base, "alice");
  const { rp } = await newRp();
  const c = (await rpCall(rp, "POST", "/consent/links", { body: { user_label: "Alex" } })).json;
  const poll = () => rpCall(rp, "GET", `/consent/links/${c.link_id}`);
  assert.equal((await poll()).json.status, "pending");
  const fast = await poll();
  assert.deepEqual([fast.status, fast.json.error, fast.json.interval], [400, "slow_down", 7]);
  const denied = await approveLink(base, dev, c.link_uri, { code: "", decision: "deny", now: now() });
  assert.equal(denied.posted!.status, 200, JSON.stringify(denied.posted!.json));
  const s = (await poll()).json;
  valid("link-status", s);
  assert.equal(s.status, "denied");
  assert.equal(s.subject, undefined);
  assert.equal(s.approval.signed.decision, "deny");
  assert.equal(verifyLinkPinned({ signed: s.approval.signed, signature: s.approval.signature, alg: "ES256" as const }, [{ kid: "alice-device", key_use: "approve", jwk: dev.publicKey as { kty: string; crv: string; x: string; y: string } }], { link_id: c.link_id, client_fingerprint: clientFingerprint(rp.clientId), domain: s.approval.signed.domain, nonce: s.approval.signed.nonce }, { now: now(), consumed: new Set() }), true);
  assert.equal(mock.state.users.get("pw_alice")!.used.has(rp.clientId), false, "a deny links nothing");
  // expiry
  const e = (await rpCall(rp, "POST", "/consent/links", { body: {} })).json;
  skew += 301;
  try {
    const x = await rpCall(rp, "GET", `/consent/links/${e.link_id}`);
    assert.equal(x.json.status, "expired");
    valid("link-status", x.json);
    const late = await approveLink(base, dev, e.link_uri, { code: e.binding_code, now: now() });
    assert.equal(late.linkRequest.status, 410);
  } finally {
    skew -= 301;
  }
  // the label: text rules, refused and not stripped
  for (const label of ["a‮b", "a​b", "line\nbreak", "x".repeat(41), "", " pad", "a‍b"]) {
    const r = await rpCall(rp, "POST", "/consent/links", { body: { user_label: label } });
    assert.equal(r.status, 400, JSON.stringify(label));
  }
  assert.equal((await rpCall(rp, "POST", "/consent/links", { body: { user_label: "Alex M." , other: 1 } })).status, 400, "unknown member");
});

// ---------------------------------------------------------------------------------------------------------------
// Requests

for (const [name, details] of [["payment", PAYMENT], ["login", LOGIN], ["action", ACTION]] as const) {
  test(`request: ${name} round trips, details_hash is the hash of what was sent plus the origin, and the party verifies with the pinned key only`, async () => {
    const l = await linked();
    const f = await requestFlow(l, details, { origin: ORIGIN });
    assert.equal(f.posted!.status, 200, JSON.stringify(f.posted!.json));
    valid("consent-request", f.fetched.json);
    const stored = { ...details, origin: ORIGIN };
    assert.deepEqual(f.fetched.json.authorization_details, [stored]);
    assert.equal(f.fetched.json.bound_hash, detailsHash(stored));
    assert.equal(f.fetched.json.entry, "direct");
    assert.equal(f.fetched.json.sub, l.subject, "the receipt names the pairwise subject");
    assert.equal(f.fetched.json.origin, ORIGIN);
    assert.equal(f.fetched.json.binding_message, "");
    assert.equal(f.fetched.json.client.linked, true);
    assert.equal(JSON.stringify(f.fetched.json).includes(f.opened.binding_code), false, "the device never gets the binding code");
    const t = await tokenOf(l, f.opened.auth_req_id);
    assert.equal(t.status, 200, JSON.stringify(t.json));
    valid("token-response", t.json);
    assert.equal(t.json.scope, "openid");
    const receipt = t.json.consent_receipt;
    assert.equal(receipt.version, "1.2");
    assert.equal(receipt.signed.details_hash, detailsHash(stored));
    const o = pinOpts(l, f.opened, { decision: "approve", origin: ORIGIN });
    assert.equal(verifyReceiptPinned(receipt, l.pins, details, o), true, "pinned verification");
    assert.equal(verifyReceiptPinned(receipt, l.pins, details, { ...o, consumed: new Set([o.auth_req_id]) }), false, "replay: the caller recorded the id after it acted");
    // attestation mode (what the server and a second check do)
    assert.equal(verifyReceipt(receipt, mock.jwks, { issuer: mock.issuer, now: now() }), true);
    assert.equal(verifyApproval(receipt, mock.jwks, f.fetched.json, f.opened.binding_code, new Set(), { issuer: mock.issuer, now: now(), deviceActive: true }), true);
    // the same receipt at both paths of the party
    const a = await rpCall(l.rp, "GET", `/consent/receipts/${f.opened.auth_req_id}`);
    const b = await rpCall(l.rp, "GET", `/consent/requests/${f.opened.auth_req_id}/receipt`);
    assert.deepEqual([a.status, b.status], [200, 200]);
    assert.deepEqual(a.json, receipt);
    assert.deepEqual(b.json, receipt);
    // one changed member in what the party sent: refused by the pinned verification
    const altered = structuredClone(details) as any;
    if (altered.creditor_account) altered.creditor_account.iban = "DE89370400440532013001";
    else if (altered.context) altered.context.ip = "203.0.113.43";
    else altered.lines[0].value = "Someone Else";
    assert.equal(verifyReceiptPinned(receipt, l.pins, altered, o), false);
    // the redeemed token is useless everywhere
    const bearer = { Authorization: "Bearer " + t.json.access_token };
    for (const [m, path] of [["POST", `/wallet/algorand-testnet/${account("alice")}/sign`], ["GET", "/consent/settings"], ["GET", "/devices"], ["GET", "/devices/requests"], ["PUT", "/wallet/limits"], ["POST", "/devices"], ["GET", `/consent/receipts/${f.opened.auth_req_id}`]] as const) {
      const r = await httpJson(base, m, path, { headers: bearer, body: m === "GET" ? undefined : { transactions: ["x"], per_transaction_eur: "1", per_day_eur: "1" } });
      assert.ok(r.status === 401 || r.status === 403, `${m} ${path} -> ${r.status}`);
    }
    // and it was single use
    assert.equal((await tokenOf(l, f.opened.auth_req_id)).json.error, "invalid_grant");
  });
}

test("request: without an origin the details carry none; with an origin in the object it is refused; the form-encoded entry takes JSON text", async () => {
  const l = await linked();
  const f = await requestFlow(l, PAYMENT);
  assert.equal(f.fetched.json.origin, undefined);
  assert.equal("origin" in f.fetched.json.authorization_details[0], false);
  assert.equal(f.fetched.json.bound_hash, detailsHash(PAYMENT));
  const t = (await tokenOf(l, f.opened.auth_req_id)).json;
  assert.equal(verifyReceiptPinned(t.consent_receipt, l.pins, PAYMENT, pinOpts(l, f.opened)), true);
  assert.equal(verifyReceiptPinned(t.consent_receipt, l.pins, PAYMENT, pinOpts(l, f.opened, { origin: ORIGIN })), false, "the party expected an origin the server did not add");
  assert.notEqual(detailsHash(PAYMENT), detailsHash({ ...PAYMENT, origin: ORIGIN }));
  const inside = await open(l, { ...PAYMENT, origin: ORIGIN });
  assert.deepEqual([inside.status, inside.json.error], [400, "invalid_authorization_details"]);
  assert.match(inside.json.error_description, /origin/);
  const badOrigin = await open(l, PAYMENT, { origin: "https://Bank.Example/path" });
  assert.equal(badOrigin.status, 400);
  const form = await rpCall(l.rp, "POST", "/connect/ciba", { form: { scope: "openid", login_hint: l.subject, authorization_details: JSON.stringify([LOGIN]), origin: ORIGIN } });
  assert.equal(form.status, 200, JSON.stringify(form.json));
  const garbage = await rpCall(l.rp, "POST", "/connect/ciba", { form: { scope: "openid", login_hint: l.subject, authorization_details: "{not json" } });
  assert.deepEqual([garbage.status, garbage.json.error], [400, "invalid_authorization_details"]);
});

test("request: refused with invalid_authorization_details, nothing stripped (text rules of every string, the IBAN, the amount, unknown members)", async () => {
  const l = await linked();
  const bad = async (label: string, details: unknown) => {
    const r = await open(l, details as object);
    assert.deepEqual([r.status, r.json?.error], [400, "invalid_authorization_details"], label + " " + JSON.stringify(r.json));
  };
  const pay = (patch: (d: any) => void) => {
    const d = structuredClone(PAYMENT) as any;
    patch(d);
    return d;
  };
  // every string a party supplies, with a bidirectional control, a zero-width character, a line break, padding and a joiner
  const poison = ["a‮b", "a⁦b", "a‎b", "a​b", "a⁠b", "a﻿b", "a\nb", "a\rb", "a\u0085b", "a b", "a\u0000b", "a\tb", " pad", "pad ", "two  spaces", "a‍b", "a‌b", "é".normalize("NFD") + "́́́́"];
  for (const p of poison) {
    const s = JSON.stringify(p);
    await bad("creditor_name " + s, pay((d) => (d.creditor_name = p)));
    await bad("remittance " + s, pay((d) => (d.remittance = p)));
    await bad("identification " + s, pay((d) => (d.creditor_account = { identification: p, scheme: "BBAN" })));
    await bad("scheme " + s, pay((d) => (d.creditor_account = { identification: "12345", scheme: p })));
    await bad("login ip " + s, { type: "biatec_login", context: { ip: p } });
    await bad("login location " + s, { type: "biatec_login", context: { location: p } });
    await bad("login user_agent " + s, { type: "biatec_login", context: { user_agent: p } });
    await bad("action title " + s, { ...ACTION, title: p });
    await bad("action label " + s, { ...ACTION, lines: [{ label: p, value: "v" }] });
    await bad("action value " + s, { ...ACTION, lines: [{ label: "l", value: p }] });
  }
  await bad("not NFC", pay((d) => (d.creditor_name = "Café")));
  await bad("too long creditor_name", pay((d) => (d.creditor_name = "x".repeat(71))));
  await bad("too long remittance", pay((d) => (d.remittance = "x".repeat(141))));
  await bad("empty remittance", pay((d) => (d.remittance = "")));
  await bad("9 lines", { ...ACTION, lines: Array.from({ length: 9 }, () => ({ label: "l", value: "v" })) });
  await bad("no lines", { ...ACTION, lines: [] });
  await bad("long title", { ...ACTION, title: "x".repeat(61) });
  await bad("long label", { ...ACTION, lines: [{ label: "x".repeat(31), value: "v" }] });
  await bad("long value", { ...ACTION, lines: [{ label: "l", value: "x".repeat(81) }] });
  await bad("long context", { type: "biatec_login", context: { ip: "x".repeat(81) } });
  await bad("empty context", { type: "biatec_login", context: {} });
  // IBAN, amount, date
  await bad("IBAN with a wrong check digit", pay((d) => (d.creditor_account = { iban: "DE89370400440532013001" })));
  await bad("IBAN in lower case", pay((d) => (d.creditor_account = { iban: "de89370400440532013000" })));
  await bad("IBAN with spaces", pay((d) => (d.creditor_account = { iban: "DE89 3704 0044 0532 0130 00" })));
  await bad("IBAN and identification", pay((d) => (d.creditor_account = { iban: "DE89370400440532013000", identification: "1", scheme: "BBAN" })));
  for (const amount of [1250, "0", "0.00", "-1", "1.234", "1,50", "01.00", " 1", "1e3", "", "1.", ".5", "NaN", "9999999999999999"]) await bad("amount " + JSON.stringify(amount), pay((d) => (d.instructed_amount.amount = amount)));
  for (const currency of ["eur", "EURO", "E1R", "", 978]) await bad("currency " + JSON.stringify(currency), pay((d) => (d.instructed_amount.currency = currency)));
  await bad("a date that does not exist", pay((d) => (d.execution_date = "2026-02-30")));
  await bad("a date with a time", pay((d) => (d.execution_date = "2026-10-20T00:00:00Z")));
  // shape
  await bad("an unknown member", pay((d) => (d.priority = "high")));
  await bad("an unknown type", { type: "biatec_transfer", title: "x" });
  await bad("a wallet type", { type: "biatec_limits_change", current_limits: {}, requested_limits: {}, request_hash: "x" });
  await bad("a missing member", pay((d) => delete d.creditor_name));
  const two = await rpCall(l.rp, "POST", "/connect/ciba", { body: { scope: "openid", login_hint: l.subject, authorization_details: [PAYMENT, LOGIN] } });
  assert.deepEqual([two.status, two.json.error], [400, "invalid_authorization_details"], "two objects");
  const obj = await rpCall(l.rp, "POST", "/connect/ciba", { body: { scope: "openid", login_hint: l.subject, authorization_details: PAYMENT } });
  assert.deepEqual([obj.status, obj.json.error], [400, "invalid_authorization_details"], "an object, not an array");
  const none = await rpCall(l.rp, "POST", "/connect/ciba", { body: { scope: "openid", login_hint: l.subject } });
  assert.equal(none.status, 400);
  const msg = await rpCall(l.rp, "POST", "/connect/ciba", { body: { scope: "openid", login_hint: l.subject, authorization_details: [PAYMENT], binding_message: "Pay now" } });
  assert.equal(msg.status, 400, "a relying party has no text channel besides the details");
  assert.equal(mock.state.pending.size > 0, true);
  // what is accepted: a text with an inner space, non-Latin text, the other account form
  const okays = [pay((d) => (d.creditor_name = "Müller & Söhne s. r. o.")), pay((d) => (d.creditor_name = "Ľudovít Štúr")), pay((d) => (d.creditor_account = { identification: "0123456789", scheme: "BBAN" })), pay((d) => (d.instructed_amount.amount = "0.01")), pay((d) => { delete d.remittance; delete d.execution_date; })];
  for (const d of okays) assert.equal((await open(l, d)).status, 200, JSON.stringify(d));
});

test("request: an unlinked, unknown, other party's or unlinked-again subject is 400 unknown_user_id, whatever the content", async () => {
  const l = await linked();
  const { rp: stranger } = await newRp();
  const answer = (r: { status: number; json: any }) => [r.status, r.json.error];
  assert.deepEqual(answer(await rpCall(stranger, "POST", "/connect/ciba", { body: { scope: "openid", login_hint: l.subject, authorization_details: [PAYMENT] } })), [400, "unknown_user_id"], "another party's subject");
  assert.deepEqual(answer(await rpCall(l.rp, "POST", "/connect/ciba", { body: { scope: "openid", login_hint: "pw_0000000000000000", authorization_details: [PAYMENT] } })), [400, "unknown_user_id"], "a subject that never existed");
  assert.deepEqual(answer(await rpCall(l.rp, "POST", "/connect/ciba", { body: { scope: "openid", login_hint: "pw_alice", authorization_details: [PAYMENT] } })), [400, "unknown_user_id"], "the account's own subject");
  assert.deepEqual(answer(await rpCall(l.rp, "POST", "/connect/ciba", { body: { scope: "openid", login_hint: "alice@example.com", authorization_details: [LOGIN] } })), [400, "unknown_user_id"], "an email address");
  assert.deepEqual(answer(await rpCall(stranger, "POST", "/connect/ciba", { body: { scope: "openid", login_hint: "pw_alice", authorization_details: { garbage: true } } })), [400, "unknown_user_id"], "not linked: refused before the content is looked at");
  // a party that registered and was never linked cannot open a request for any user of the seed
  for (const hint of ["alice@example.com", "pw_alice", "carol@example.com"]) assert.equal((await rpCall(stranger, "POST", "/connect/ciba", { body: { scope: "openid", login_hint: hint, authorization_details: [LOGIN] } })).json.error, "unknown_user_id");
  // the user unlinks on the phone: the same answer as for a stranger
  assert.equal((await open(l, LOGIN)).status, 200);
  const unlink = await httpJson(base, "PUT", "/devices/settings", { headers: { Authorization: l.dev.auth }, body: { change: { clients: { [l.rp.clientId]: { unlink: true } } } } });
  assert.equal(unlink.status, 200, JSON.stringify(unlink.json));
  assert.deepEqual(answer(await open(l, LOGIN)), [400, "unknown_user_id"]);
  assert.equal((await rpCall(l.rp, "GET", `/consent/links/subject/${l.subject}/devices`)).status, 404);
  const closedPending = [...mock.state.pending.values()].filter((p) => p.clientId === l.rp.clientId && p.status === "pending");
  assert.equal(closedPending.length, 0, "its open requests were closed with the link");
});

test("request: the party removes its own link (DELETE), the same subject comes back after a new link", async () => {
  const l = await linked();
  const first = await open(l, PAYMENT);
  assert.equal(first.status, 200);
  const del = await rpCall(l.rp, "DELETE", `/consent/links/subject/${l.subject}`);
  assert.deepEqual([del.status, del.json], [200, { unlinked: true }]);
  assert.equal((await open(l, PAYMENT)).json.error, "unknown_user_id");
  assert.equal((await rpCall(l.rp, "DELETE", `/consent/links/subject/${l.subject}`)).status, 404, "twice");
  const closed = await approveRequest(base, l.dev, first.json.auth_req_id, { code: first.json.binding_code, now: now() });
  assert.equal(closed.posted!.status, 409, "the request that was open when the link went is closed");
  // another party cannot unlink it
  const l2 = await linked();
  assert.equal((await rpCall(l.rp, "DELETE", `/consent/links/subject/${l2.subject}`)).status, 404);
  assert.equal((await open(l2, PAYMENT)).status, 200);
});

test("request: the device approves with the approval key only; a presence-key approve is refused, a presence-key deny is fine", async () => {
  const l = await linked();
  const o = (await open(l, PAYMENT)).json;
  const bad = await approveRequest(base, l.dev, o.auth_req_id, { code: o.binding_code, presence: true, now: now() });
  assert.deepEqual([bad.posted!.status, bad.posted!.json.error], [400, "invalid_receipt"]);
  const deny = await approveRequest(base, l.dev, o.auth_req_id, { code: "", decision: "deny", presence: true, now: now() });
  assert.equal(deny.posted!.status, 200, JSON.stringify(deny.posted!.json));
  const r = (await rpCall(l.rp, "GET", `/consent/receipts/${o.auth_req_id}`)).json;
  assert.equal(r.signed.decision, "deny");
  assert.equal(verifyReceiptPinned(r, l.pins, PAYMENT, pinOpts(l, o, { decision: "deny" })), true, "a deny is authentic with either key");
  assert.equal(verifyReceiptPinned(r, l.pins, PAYMENT, pinOpts(l, o, { decision: "approve" })), false);
  const t = await tokenOf(l, o.auth_req_id);
  assert.deepEqual([t.status, t.json.error], [400, "access_denied"]);
});

test("request: the receipt must restate the request; 1.1 for a relying party's request and 1.2 for a wallet one are refused; a changed member is refused", async () => {
  const l = await linked();
  const o = (await open(l, PAYMENT)).json;
  const post = async (change: Record<string, unknown>) => (await approveRequest(base, l.dev, o.auth_req_id, { code: o.binding_code, change, now: now() })).posted!;
  const iban = structuredClone(PAYMENT) as any;
  iban.creditor_account.iban = "DE89370400440532013001";
  const attacker = structuredClone(PAYMENT) as any;
  attacker.creditor_account.iban = "GB82WEST12345698765432";
  assert.equal((await post({ authorization_details: [attacker], details_hash: detailsHash(attacker) })).json.error, "receipt_mismatch", "details of another payment");
  assert.equal((await post({ details_hash: detailsHash(attacker) })).status, 400, "a hash that is not the hash of the details");
  assert.equal((await post({ sub: "pw_alice" })).status, 400, "another subject");
  assert.equal((await post({ binding_message: "Pay now" })).status, 400);
  assert.equal((await post({ remember: { applications: [1] } })).status, 400);
  assert.equal((await post({ expiry: 1 })).status, 400);
  assert.equal((await post({ client_fingerprint: "0123456789abcdef" })).status, 400);
  assert.equal((await post({ auth_req_id: "ciba_00000000000000000000000000000000" })).status, 400);
  assert.equal((await post({ binding_code: "111111" })).json.error, "wrong_code");
  // a 1.1 receipt (no details_hash) for this request
  const fetched = (await httpJson(base, "GET", `/consent/requests/${o.auth_req_id}`, { headers: { Authorization: l.dev.auth } })).json;
  const signed = { authorization_details: fetched.authorization_details, auth_req_id: fetched.auth_req_id, client_fingerprint: fetched.client.fingerprint, binding_code: o.binding_code, decision: "approve", expiry: fetched.expiry, sub: fetched.sub, binding_message: "" };
  const { jcs } = await import("../../scripts/lib.mjs");
  const old = { version: "1.1", signed, alg: "ES256", signature: signEs256(vectorKeys.device, Buffer.from(jcs(signed), "utf8")), issued_at: now(), device: { kid: l.dev.kid, key_use: "approve", public_key: l.dev.publicKey, attestation: l.dev.attestation } };
  const r11 = await httpJson(base, "POST", `/consent/requests/${o.auth_req_id}/receipt`, { headers: { Authorization: l.dev.auth }, body: old });
  assert.equal(r11.status, 400, "a 1.1 receipt does not fit the request: " + JSON.stringify(r11.json));
  // the request is still open and the right receipt is accepted
  const ok = await approveRequest(base, l.dev, o.auth_req_id, { code: o.binding_code, now: now() });
  assert.equal(ok.posted!.status, 200, JSON.stringify(ok.posted!.json));
  void iban;
  // and the other way round: a wallet request does not take a 1.2 receipt
  const w = await httpJson(base, "POST", "/connect/ciba", { headers: { Authorization: "Basic " + Buffer.from("biatec-chat:biatec-chat-secret").toString("base64") }, body: { scope: "sign", resource: "https://api.biatec.io", login_hint: "alice@example.com", network: "algorand-testnet", address: account("alice"), unsigned_group: buildGroup([usdcTransfer(account("alice"), account("payee"), 5000)]).items, origin: "https://rp-wallet.example" } });
  assert.equal(w.status, 200, JSON.stringify(w.json));
  const wr = await approveRequest(base, l.dev, w.json.auth_req_id, { code: w.json.binding_code, now: now() });
  assert.equal(wr.posted!.status, 400, JSON.stringify(wr.posted!.json));
});

test("request: three wrong codes end it (code_failed), the party gets the code_failed receipt but no token", async () => {
  const l = await linked();
  const o = (await open(l, LOGIN)).json;
  const wrong = (code: string) => approveRequest(base, l.dev, o.auth_req_id, { code, now: now() });
  assert.equal((await wrong("000001")).posted!.json.attempts_remaining, 2);
  assert.equal((await wrong("000002")).posted!.json.attempts_remaining, 1);
  assert.equal((await wrong("000003")).posted!.json.reason, "code_failed");
  const t = await tokenOf(l, o.auth_req_id);
  assert.deepEqual([t.status, t.json.error, t.json.reason], [400, "access_denied", "code_failed"]);
});

test("request: the open-request cap (5 per user and client), the party's rate, a paused client and a user without an active device", async () => {
  const l = await linked();
  for (let i = 0; i < 5; i++) assert.equal((await open(l, LOGIN)).status, 200);
  const sixth = await open(l, LOGIN);
  assert.deepEqual([sixth.status, sixth.json.error], [429, "too_many_pending_requests"]);
  // the cap is the party's, not shared
  const other = await linked();
  assert.equal((await open(other, LOGIN)).status, 200);
  // the rate (per minute, per party)
  const small = createMockOidc({ testHelpers: true, maxRelyingPartyRequestsPerMinute: 2, maxRelyingPartyPendingPerUser: 100 });
  await new Promise<void>((r) => small.server.listen(0, "127.0.0.1", r));
  const b = `http://127.0.0.1:${(small.server.address() as AddressInfo).port}`;
  try {
    const { rp } = await registerRelyingParty(b, small.issuer, { clientUri: "https://rate.example" });
    const c = (await rpCall(rp, "POST", "/consent/links", { body: {} })).json;
    const dev = await testDevice(b);
    assert.equal((await approveLink(b, dev, c.link_uri, { code: c.binding_code })).posted!.status, 200);
    const subject = (await rpCall(rp, "GET", `/consent/links/${c.link_id}`)).json.subject;
    const go = () => rpCall(rp, "POST", "/connect/ciba", { body: { scope: "openid", login_hint: subject, authorization_details: [LOGIN] } });
    assert.deepEqual([(await go()).status, (await go()).status], [200, 200]);
    const third = await go();
    assert.deepEqual([third.status, third.json.error], [429, "rate_limited"]);
    assert.ok(third.headers.get("retry-after"));
  } finally {
    small.server.close();
  }
  // paused by the user (rule 10)
  const p = await linked();
  assert.equal((await httpJson(base, "POST", `/mock/users/pw_alice/clients/${p.rp.clientId}/pause`)).status, 200);
  const paused = await open(p, LOGIN);
  assert.deepEqual([paused.status, paused.json.error], [403, "client_paused"]);
  // no active device
  const n = await linked();
  const d = mock.state.devices.get("alice-device")!;
  d.cancelled = true;
  try {
    const r = await open(n, LOGIN);
    assert.deepEqual([r.status, r.json.error, r.json.reason], [403, "consent_unavailable", "no_active_device"]);
  } finally {
    d.cancelled = false;
  }
  // a user who blocks unproven clients refuses an unproven party
  const u = await linked({ prove: false });
  mock.state.accountSettings.set("pw_alice", { biometricRequired: true, blockUnverified: true, askOnTestNetworks: true });
  try {
    assert.equal((await open(u, LOGIN)).json.error, "unproven_client_blocked");
  } finally {
    mock.state.accountSettings.set("pw_alice", { biometricRequired: true, blockUnverified: false, askOnTestNetworks: true });
  }
});

test("request: the threshold, trusted applications and first-use rules do not apply: it always goes to the phone, whatever the amount", async () => {
  const l = await linked();
  // a tiny payment of a user with a high threshold still opens a request
  mock.state.users.get("pw_alice")!.thresholdEur = 1_000_000;
  try {
    const f = await requestFlow(l, { ...PAYMENT, instructed_amount: { amount: "0.01", currency: "EUR" } });
    assert.equal(f.posted!.status, 200);
  } finally {
    mock.state.users.get("pw_alice")!.thresholdEur = 1000;
  }
});

test("request: another party cannot get the receipt or redeem the token; before approval there is no receipt", async () => {
  const l = await linked();
  const l2 = await linked();
  const o = (await open(l, PAYMENT)).json;
  assert.equal((await rpCall(l.rp, "GET", `/consent/receipts/${o.auth_req_id}`)).status, 404, "no receipt yet");
  assert.equal((await rpCall(l2.rp, "GET", `/consent/receipts/${o.auth_req_id}`)).status, 404);
  assert.equal((await rpCall(l2.rp, "GET", `/consent/requests/${o.auth_req_id}/receipt`)).status, 404);
  assert.equal((await tokenOf(l2, o.auth_req_id)).json.error, "invalid_grant");
  const pending = await tokenOf(l, o.auth_req_id);
  assert.equal(pending.json.error, "authorization_pending");
  assert.equal((await httpJson(base, "GET", `/consent/receipts/${o.auth_req_id}`)).status, 401);
});

test("a new device: its key shows up in the devices list, and pinned verification refuses its receipt until the party decides (spec 14.6)", async () => {
  const l = await linked();
  const pair = generateKeyPairSync("ec", { namedCurve: "P-256" });
  const pub = pair.publicKey.export({ format: "jwk" }) as Record<string, string>;
  const enrolled = await httpJson(base, "POST", "/devices", { headers: { Authorization: "Bearer login-alice" }, body: { kid: "alice-phone-" + seq, public_key: pub } });
  assert.equal(enrolled.status, 201, JSON.stringify(enrolled.json));
  const before = (await rpCall(l.rp, "GET", `/consent/links/subject/${l.subject}/devices`)).json;
  assert.equal(before.devices.some((d: any) => d.kid === enrolled.json.kid), false, "a pending device is not listed");
  assert.equal((await httpJson(base, "POST", `/mock/devices/${enrolled.json.kid}/activate`)).status, 200);
  const after = await rpCall(l.rp, "GET", `/consent/links/subject/${l.subject}/devices`);
  assert.equal(after.status, 200);
  const view = ajv.getSchema("https://schemas.biatec.io/consent/v1/link-status.schema.json#/$defs/devices")!;
  assert.ok(view(after.json.devices), ajv.errorsText(view.errors));
  const added = after.json.devices.find((d: any) => d.kid === enrolled.json.kid);
  assert.deepEqual([added.key_use, added.jwk], ["approve", { kty: "EC", crv: "P-256", x: pub.x, y: pub.y }]);
  assert.equal(after.json.devices.length, before.devices.length + 1);
  // the new phone approves a request: the key is not pinned
  const o = (await open(l, PAYMENT)).json;
  const newDev = { ...l.dev, auth: "Device " + enrolled.json.device_token, kid: enrolled.json.kid, publicKey: { kty: "EC", crv: "P-256", x: pub.x, y: pub.y }, attestation: enrolled.json.attestation };
  const fetched = (await httpJson(base, "GET", `/consent/requests/${o.auth_req_id}`, { headers: { Authorization: newDev.auth } })).json;
  const { jcs } = await import("../../scripts/lib.mjs");
  const signed = { authorization_details: fetched.authorization_details, auth_req_id: fetched.auth_req_id, client_fingerprint: fetched.client.fingerprint, binding_code: o.binding_code, decision: "approve", expiry: fetched.expiry, sub: fetched.sub, binding_message: "", details_hash: detailsHash(fetched.authorization_details[0]) };
  const { createPrivateKey, sign: cs } = await import("node:crypto");
  const sig = cs("sha256", Buffer.from(jcs(signed), "utf8"), { key: createPrivateKey({ key: pair.privateKey.export({ format: "jwk" }) as never, format: "jwk" }), dsaEncoding: "ieee-p1363" }).toString("base64url");
  const receipt = { version: "1.2", signed, alg: "ES256", signature: sig, issued_at: now(), device: { kid: newDev.kid, key_use: "approve", public_key: newDev.publicKey, attestation: newDev.attestation } };
  assert.equal((await httpJson(base, "POST", `/consent/requests/${o.auth_req_id}/receipt`, { headers: { Authorization: newDev.auth }, body: receipt })).status, 200);
  const stored = (await rpCall(l.rp, "GET", `/consent/receipts/${o.auth_req_id}`)).json;
  assert.equal(verifyReceiptPinned(stored, l.pins, PAYMENT, pinOpts(l, o)), false, "the old pins do not know this key");
  assert.equal(verifyReceiptPinned(stored, after.json.devices, PAYMENT, pinOpts(l, o)), true, "after the party accepted the keys of the list");
  assert.equal(verifyReceipt(stored, mock.jwks, { issuer: mock.issuer, now: now() }), true, "attestation mode accepts it");
});

test("domain: proven while the well-known file lists the client id, and not when it does not", async () => {
  const { rp, host } = await newRp({ prove: false });
  const dev = await testDevice(base);
  const fetchLink = async () => {
    const c = (await rpCall(rp, "POST", "/consent/links", { body: {} })).json;
    return (await approveLink(base, dev, c.link_uri, { code: "", decision: "deny", now: now() })).linkRequest.json.client;
  };
  assert.equal((await fetchLink()).domain_proven, false);
  mock.serve(`https://${host}/.well-known/biatec-client.json`, { client_ids: ["someone-else"] });
  assert.equal((await fetchLink()).domain_proven, false);
  mock.serve(`https://${host}/.well-known/biatec-client.json`, { client_ids: ["x", rp.clientId] });
  assert.deepEqual(await fetchLink(), { fingerprint: clientFingerprint(rp.clientId), domain_proven: true, domain: host });
  mock.serve(`https://${host}/.well-known/biatec-client.json`, null);
  assert.equal((await fetchLink()).domain_proven, false, "removed again");
});

test("the helper /mock/serve is served only with test helpers", async () => {
  const plain = createMockOidc();
  await new Promise<void>((r) => plain.server.listen(0, "127.0.0.1", r));
  const b = `http://127.0.0.1:${(plain.server.address() as AddressInfo).port}`;
  try {
    assert.equal((await httpJson(b, "POST", "/mock/serve", { body: { url: "https://x.example/", json: {} } })).status, 404);
  } finally {
    plain.server.close();
  }
});

test("the 1.1 wallet flow is unchanged next to relying parties (a seeded client still signs through step-up)", async () => {
  const g = buildGroup([usdcTransfer(account("alice"), account("payee"), 5000)]);
  const r = await httpJson(base, "POST", `/wallet/algorand-testnet/${account("alice")}/sign`, { headers: { Authorization: "Bearer alice-chat-sign" }, body: { transactions: g.items, origin: "https://rp-compat.example" } });
  assert.equal(r.status, 401);
  assert.equal(r.json.error, "insufficient_user_authentication");
});

test("a key pair of the relying party is never sent: the answers hold no private member", async () => {
  const l = await linked();
  const all = JSON.stringify([l.created, l.status, l.linkRequest]);
  assert.equal(all.includes(l.rp.key.privateJwk.d), false);
  void generateRpKey;
});

void clientFingerprint;

test("link: POST .../receipt needs X-Link-Secret like the fetch (missing or wrong is 404)", async () => {
  const dev = await testDevice(base, "alice");
  const { rp } = await newRp();
  const c = (await rpCall(rp, "POST", "/consent/links", { body: {} })).json;
  const { id, secret } = parseLinkUri(c.link_uri);
  const lr = (await httpJson(base, "GET", `/consent/links/${id}`, { headers: { Authorization: dev.auth, "X-Link-Secret": secret } })).json;
  const signed = { type: "biatec_link", link_id: id, client_fingerprint: lr.client.fingerprint, domain: lr.client.domain ?? null, decision: "approve", binding_code: c.binding_code, nonce: lr.nonce, issued_at: now(), kid: dev.kid };
  const body = { signed, signature: signEs256(vectorKeys.device, Buffer.from(jcs(signed), "utf8")) };
  const post = (h: Record<string, string>) => httpJson(base, "POST", `/consent/links/${id}/receipt`, { headers: { Authorization: dev.auth, ...h }, body });
  assert.equal((await post({})).status, 404, "no secret");
  assert.equal((await post({ "X-Link-Secret": "A".repeat(43) })).status, 404, "wrong secret");
  assert.equal((await rpCall(rp, "GET", `/consent/links/${id}`)).json.status, "pending");
  assert.equal((await post({ "X-Link-Secret": secret })).status, 200);
});

test("client assertion: a client_id next to the assertion must be the assertion's own (RFC 7523), else 401", async () => {
  const { rp } = await newRp();
  const other = (await newRp()).rp;
  const ok = await rpCall(rp, "POST", "/consent/links", { body: { client_id: rp.clientId } });
  assert.equal(ok.status, 201);
  const conflict = await rpCall(rp, "POST", "/consent/links", { body: { client_id: other.clientId } });
  assert.deepEqual([conflict.status, conflict.json.error], [401, "invalid_client"]);
  const form = await rpCall(rp, "POST", "/token", { form: { grant_type: "urn:openid:params:grant-type:ciba", auth_req_id: "x", client_id: other.clientId } });
  assert.equal(form.status, 401);
});

test("a request closed because the link was removed ends as access_denied with reason unlinked, and there is no receipt (by the party and on the phone)", async () => {
  for (const how of ["party", "phone"] as const) {
    const l = await linked();
    const o = (await open(l, PAYMENT)).json;
    if (how === "party") assert.equal((await rpCall(l.rp, "DELETE", `/consent/links/subject/${l.subject}`)).status, 200);
    else assert.equal((await httpJson(base, "PUT", "/devices/settings", { headers: { Authorization: l.dev.auth }, body: { change: { clients: { [l.rp.clientId]: { unlink: true } } } } })).status, 200);
    const t = await tokenOf(l, o.auth_req_id);
    assert.deepEqual([t.status, t.json.error, t.json.reason], [400, "access_denied", "unlinked"], how);
    assert.match(t.json.error_description, /not a denial by the user/);
    assert.equal((await rpCall(l.rp, "GET", `/consent/receipts/${o.auth_req_id}`)).status, 404, "no receipt");
    // a user's own deny stays distinguishable: no reason
    const l2 = await linked();
    const o2 = (await open(l2, PAYMENT)).json;
    await approveRequest(base, l2.dev, o2.auth_req_id, { code: "", decision: "deny", now: now() });
    const d = await tokenOf(l2, o2.auth_req_id);
    assert.deepEqual([d.json.error, d.json.reason], ["access_denied", undefined]);
  }
});

test("authorization_details of a refused request names the rule once: a bad IBAN, a text rule, an origin inside; another bad parameter is invalid_request", async () => {
  const l = await linked();
  const bad = { ...PAYMENT, creditor_account: { iban: "DE89370400440532013001" } };
  const r = await open(l, bad);
  assert.deepEqual([r.status, r.json.error], [400, "invalid_authorization_details"]);
  assert.match(r.json.error_description, /check digits/);
  const other = await open(l, PAYMENT, { requested_expiry: 9999 });
  assert.deepEqual([other.status, other.json.error], [400, "invalid_request"]);
});
