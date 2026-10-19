import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { AddressInfo } from "node:net";
import Ajv2020Module from "ajv/dist/2020.js";
import { jcs, signEs256, verifyReceipt } from "../../scripts/lib.mjs";
import { createMockOidc } from "../src/app.ts";
import { encodeTx, type Tx } from "../src/group.ts";

const Ajv2020 = Ajv2020Module as unknown as typeof Ajv2020Module.default;
const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const load = (...p: string[]) => JSON.parse(readFileSync(join(root, ...p), "utf8"));
const ajv = new Ajv2020({ allErrors: true, strict: true });
for (const f of readdirSync(join(root, "schemas")).filter((n) => n.endsWith(".schema.json"))) ajv.addSchema(load("schemas", f));
const valid = (name: string, data: unknown) => {
  const v = ajv.getSchema(`https://schemas.biatec.io/consent/v1/${name}.schema.json`)!;
  assert.ok(v(data), name + " " + ajv.errorsText(v.errors));
};

const keys = load("vectors", "keys.json");
const vectorJwks = load("vectors", "jwks.json");
let clock = 1_792_425_600;
let codes: string[] = [];
const mock = createMockOidc({ now: () => clock, newBindingCode: () => codes.shift() ?? "483920" });
let base = "";
before(async () => {
  await new Promise<void>((r) => mock.server.listen(0, "127.0.0.1", r));
  base = `http://127.0.0.1:${(mock.server.address() as AddressInfo).port}`;
});
after(() => mock.server.close());

const ALICE = "alice";
const ALICE_ADDR = "ALICEADDRESS";
const usdc = { id: "31566704", symbol: "USDC", decimals: 6 };
const tx = (fiat: string, extra: Partial<Tx> = {}): Tx => ({ sender: ALICE_ADDR, receiver: "PAYEE", asset: usdc, amount: fiat + ".000000", fiat_eur: fiat + ".00", ...extra });
const group = (...t: Tx[]) => t.map(encodeTx);

async function call(method: string, path: string, opts: { auth?: string; body?: unknown; form?: Record<string, string> } = {}) {
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
  return { status: res.status, headers: res.headers, json: (await res.json()) as any };
}
const bearer = (t: string) => "Bearer " + t;
const basic = (id: string, secret: string) => "Basic " + Buffer.from(`${id}:${secret}`).toString("base64");
const chatClient = basic("biatec-chat", "biatec-chat-secret");
const sign = (token: string, g: string[], extra: object = {}) => call("POST", `/wallet/algorand-testnet/${ALICE_ADDR}/sign`, { auth: bearer(token), body: { unsigned_group: g, ...extra } });
const tokenReq = (authReqId: string, auth = chatClient) =>
  call("POST", "/token", { auth, form: { grant_type: "urn:openid:params:grant-type:ciba", auth_req_id: authReqId } });

const deviceAuth = (user: string) => "Device device-token-" + user;
async function device(user: string) {
  const d = (await call("GET", `/mock/devices/${user}-device`)).json;
  return { auth: deviceAuth(user), attestation: d.attestation as string, publicKey: d.public_key as Record<string, string>, kid: d.kid as string };
}
type Dev = Awaited<ReturnType<typeof device>>;

/** What the authenticator does: build the receipt from the fetched request and the code the user typed, sign it. */
function receiptFor(dev: Dev, request: any, code: string, decision = "approve", signingKey = keys.device) {
  const signed = {
    authorization_details: request.authorization_details,
    auth_req_id: request.auth_req_id,
    client_entity_id: request.client.entity_id,
    binding_code: code,
    decision,
    expiry: request.expiry,
    sub: request.sub,
    binding_message: request.binding_message,
  };
  return {
    version: "1",
    signed,
    alg: "ES256",
    signature: signEs256(signingKey, Buffer.from(jcs(signed), "utf8")),
    issued_at: clock,
    device: { kid: dev.kid, public_key: dev.publicKey, attestation: dev.attestation },
  };
}
const postReceipt = (dev: Dev, id: string, receipt: unknown) => call("POST", `/consent/requests/${id}/receipt`, { auth: dev.auth, body: receipt });

/** Opens a step-up challenge for a group above the threshold. */
async function challenge(g = group(tx("5000")), token = "alice-chat-sign") {
  const r = await sign(token, g);
  assert.equal(r.status, 401, JSON.stringify(r.json));
  return r;
}
const fetchRequest = async (dev: Dev, id: string) => (await call("GET", `/consent/requests/${id}`, { auth: dev.auth })).json;

// ---------------------------------------------------------------------------------------------------------------
// Acceptance: step-up on sign returns 401 with auth_req_id and binding code

test("step-up on sign: 401 with RFC 9470 challenge, auth_req_id and binding code", async () => {
  codes = ["483920"];
  const r = await challenge();
  valid("stepup-challenge", r.json);
  assert.equal(r.json.binding_code, "483920");
  assert.equal(r.json.expires_in, 300);
  assert.match(r.headers.get("www-authenticate")!, /error="insufficient_user_authentication", acr_values="biatec:consent"/);
});

test("below threshold signs directly; requireConsent and always-ask force the step-up", async () => {
  const small = await sign("alice-chat-sign", group(tx("10")));
  assert.equal(small.status, 200);
  assert.ok(small.json.signed_group[0].startsWith("signed:"));
  assert.equal((await sign("alice-chat-sign", group(tx("10")), { requireConsent: true })).status, 401);
  assert.equal((await sign("carol-chat-sign", group(tx("10")))).status, 401, "always-ask forces the phone even below the threshold");
});

test("scope and sender checks", async () => {
  assert.equal((await sign("alice-chat-nosign", group(tx("10")))).status, 403);
  assert.equal((await sign("alice-chat-sign", group(tx("10", { sender: "OTHER" })))).status, 400, "one sender per call");
  assert.equal((await call("POST", `/wallet/algorand-testnet/${ALICE_ADDR}/sign`, {})).status, 401);
});

test("rule 4: first use of a client always needs the phone; rule 8: no device, no above-threshold action", async () => {
  assert.equal((await sign("alice-bank-sign", group(tx("5")))).status, 401, "first use of bank-example");
  const bob = await sign("bob-chat-sign", group(tx("5000")));
  assert.equal(bob.status, 403);
  assert.equal(bob.json.reason, "no_active_device");
  assert.equal((await sign("bob-chat-sign", group(tx("5")))).status, 200, "below threshold still works without a device");
});

// ---------------------------------------------------------------------------------------------------------------
// Acceptance: CIBA token returned after valid receipt

test("full step-up flow: device fetches request, approves, CIBA token carries a verifiable consent_receipt, sign spends it once", async () => {
  codes = ["120934"];
  const g = group(tx("5000"));
  const c = await challenge(g);
  const dev = await device(ALICE);

  assert.equal((await tokenReq(c.json.auth_req_id)).json.error, "authorization_pending");

  const request = await fetchRequest(dev, c.json.auth_req_id);
  valid("consent-request", request);
  assert.equal("binding_code" in request, false, "the authenticator never receives the code (rule 2)");
  assert.equal(request.binding_message, "");
  assert.equal(request.entry, "step-up");
  assert.equal(request.client.display_name, "Biatec Chat");
  const list = (await call("GET", "/devices/requests", { auth: dev.auth })).json.requests;
  assert.ok(list.some((x: any) => x.auth_req_id === c.json.auth_req_id));

  const posted = await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, "120934"));
  assert.equal(posted.status, 200, JSON.stringify(posted.json));

  const t = await tokenReq(c.json.auth_req_id);
  assert.equal(t.status, 200, JSON.stringify(t.json));
  valid("token-response", t.json);
  assert.equal("refresh_token" in t.json, false);
  assert.ok(verifyReceipt(t.json.consent_receipt, vectorJwks, { issuer: mock.issuer, now: clock }), "offline verifiable with the published JWKS");
  const jwks = (await call("GET", "/.well-known/jwks.json")).json;
  assert.ok(verifyReceipt(t.json.consent_receipt, jwks, { issuer: mock.issuer, now: clock }));
  assert.equal((await tokenReq(c.json.auth_req_id)).json.error, "invalid_grant", "auth_req_id redeems once");
  assert.equal((await call("GET", `/consent/receipts/${c.json.auth_req_id}`, { auth: chatClient })).status, 200);
  assert.equal((await call("GET", `/consent/receipts/${c.json.auth_req_id}`, { auth: basic("bank-example", "bank-example-secret") })).status, 404, "only the owning client");

  const ok = await sign(t.json.access_token, g);
  assert.equal(ok.status, 200, JSON.stringify(ok.json));
  const again = await sign(t.json.access_token, g);
  assert.equal(again.status, 401, "rule 5: single use");
  assert.equal(again.json.reason, "consent_consumed");
});

test("token endpoint rejects bad client credentials and other clients' requests", async () => {
  codes = ["000001"];
  const c = await challenge();
  assert.equal((await tokenReq(c.json.auth_req_id, basic("biatec-chat", "wrong"))).status, 401);
  assert.equal((await tokenReq(c.json.auth_req_id, basic("bank-example", "bank-example-secret"))).json.error, "invalid_grant");
});

// ---------------------------------------------------------------------------------------------------------------
// Acceptance: mismatching group hash refused (rule 5)

test("rule 5: a different group with the consent token is refused and leaves the consent unspent", async () => {
  codes = ["222333"];
  const g = group(tx("5000"));
  const c = await challenge(g);
  const dev = await device(ALICE);
  const request = await fetchRequest(dev, c.json.auth_req_id);
  await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, "222333"));
  const t = await tokenReq(c.json.auth_req_id);

  const other = await sign(t.json.access_token, group(tx("5000", { receiver: "ATTACKER" })));
  assert.equal(other.status, 403);
  assert.equal(other.json.error, "group_hash_mismatch");
  assert.equal((await sign(t.json.access_token, group(tx("4000")))).status, 403);
  assert.equal((await sign(t.json.access_token, g)).status, 200, "still unspent after the refused attempts");
});

test("rule 5: a consent approved for testnet is refused on mainnet (same group hash)", async () => {
  codes = ["444555"];
  const g = group(tx("5000"));
  const c = await challenge(g);
  const dev = await device(ALICE);
  const request = await fetchRequest(dev, c.json.auth_req_id);
  await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, "444555"));
  const t = await tokenReq(c.json.auth_req_id);
  const main = await call("POST", `/wallet/algorand-mainnet/${ALICE_ADDR}/sign`, { auth: bearer(t.json.access_token), body: { unsigned_group: g } });
  assert.equal(main.status, 403);
  assert.equal(main.json.error, "group_hash_mismatch");
  assert.equal((await sign(t.json.access_token, g)).status, 200, "still unspent on the approved network");
});

test("rule 2: code_failed is terminal; a later deny or approve receipt is refused", async () => {
  codes = ["777888"];
  const c = await challenge();
  const dev = await device(ALICE);
  const request = await fetchRequest(dev, c.json.auth_req_id);
  for (const w of ["111111", "222222", "333333"]) await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, w));
  const deny = await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, "333333", "deny"));
  assert.equal(deny.status, 403);
  assert.equal(deny.json.reason, "code_failed");
  assert.equal((await tokenReq(c.json.auth_req_id)).json.reason, "code_failed");
});

test("rule 5: the consent token and the request expire after 300 seconds", async () => {
  codes = ["444555", "666777"];
  const g = group(tx("5000"));
  const c1 = await challenge(g);
  const c2 = await challenge(g);
  const dev = await device(ALICE);
  const approve = async (id: string, code: string) => postReceipt(dev, id, receiptFor(dev, await fetchRequest(dev, id), code));
  await approve(c2.json.auth_req_id, "666777");
  const t = await tokenReq(c2.json.auth_req_id);
  assert.equal(t.json.expires_in, 300);

  const before = clock;
  clock += 299;
  assert.equal((await approve(c1.json.auth_req_id, "444555")).status, 200, "still valid at 299 s");
  clock = before + 300;
  assert.equal((await sign(t.json.access_token, g)).status, 401, "consent token expired at 300 s");
  const c3 = await challenge(g);
  clock += 300;
  assert.equal((await tokenReq(c3.json.auth_req_id)).json.error, "expired_token");
  const req3 = { ...(await call("GET", `/consent/requests/${c3.json.auth_req_id}`, { auth: deviceAuth(ALICE) })).json };
  assert.equal(req3.error, undefined);
  assert.equal((await postReceipt(dev, c3.json.auth_req_id, receiptFor(dev, req3, "483920"))).status, 410);
  clock = before;
});

// ---------------------------------------------------------------------------------------------------------------
// Rule 2: binding code, three attempts

test("rule 2: wrong code does not deny at once; the third wrong code is code_failed", async () => {
  codes = ["135790"];
  const c = await challenge();
  const dev = await device(ALICE);
  const request = await fetchRequest(dev, c.json.auth_req_id);

  const w1 = await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, "111111"));
  assert.equal(w1.status, 400);
  assert.equal(w1.json.error, "wrong_code");
  assert.equal(w1.json.attempts_remaining, 2);
  assert.equal((await tokenReq(c.json.auth_req_id)).json.error, "authorization_pending", "not a denial yet");
  const w2 = await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, "222222"));
  assert.equal(w2.json.attempts_remaining, 1);
  const w3 = await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, "333333"));
  assert.equal(w3.status, 403);
  assert.equal(w3.json.reason, "code_failed");

  const right = await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, "135790"));
  assert.equal(right.status, 403, "the correct code after exhaustion is refused");
  const denial = await tokenReq(c.json.auth_req_id);
  assert.equal(denial.json.error, "access_denied");
  assert.equal(denial.json.reason, "code_failed");

  const cf = receiptFor(dev, request, "333333", "code_failed");
  assert.equal((await postReceipt(dev, c.json.auth_req_id, cf)).status, 200);
  valid("consent-receipt", (await call("GET", `/consent/receipts/${c.json.auth_req_id}`, { auth: chatClient })).json);
});

test("rule 2: success on the third attempt, and a code_failed receipt is refused before exhaustion", async () => {
  codes = ["246810"];
  const c = await challenge();
  const dev = await device(ALICE);
  const request = await fetchRequest(dev, c.json.auth_req_id);
  assert.equal((await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, "999999", "code_failed"))).status, 400);
  await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, "111111"));
  await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, "222222"));
  assert.equal((await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, "246810"))).status, 200);
  assert.equal((await tokenReq(c.json.auth_req_id)).status, 200);
});

test("rule 10: explicit deny yields a deny receipt and access_denied", async () => {
  codes = ["112233"];
  const c = await challenge();
  const dev = await device(ALICE);
  const request = await fetchRequest(dev, c.json.auth_req_id);
  assert.equal((await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, "", "deny"))).status, 200);
  const t = await tokenReq(c.json.auth_req_id);
  assert.equal(t.json.error, "access_denied");
  assert.equal(t.json.reason, undefined);
});

// ---------------------------------------------------------------------------------------------------------------
// Rule 9: receipts verify offline; anything altered is refused

test("rule 9: altered, foreign-signed or other-request receipts are refused", async () => {
  codes = ["314159", "271828"];
  const c = await challenge();
  const c2 = await challenge(group(tx("6000")));
  const dev = await device(ALICE);
  const request = await fetchRequest(dev, c.json.auth_req_id);
  const good = receiptFor(dev, request, "314159");

  const tampered = structuredClone(good);
  tampered.signed.authorization_details[0].amount = "1.000000";
  assert.equal((await postReceipt(dev, c.json.auth_req_id, tampered)).json.error, "invalid_receipt");

  const resigned = receiptFor(dev, { ...request, authorization_details: [{ ...request.authorization_details[0], receiver: "ATTACKER" }] }, "314159");
  assert.equal((await postReceipt(dev, c.json.auth_req_id, resigned)).json.error, "receipt_mismatch");

  const foreignKey = receiptFor(dev, request, "314159", "approve", keys.oidc);
  assert.equal((await postReceipt(dev, c.json.auth_req_id, foreignKey)).json.error, "invalid_receipt");

  const badAttestation = structuredClone(good);
  badAttestation.device.attestation = good.device.attestation.slice(0, -4) + "AAAA";
  assert.equal((await postReceipt(dev, c.json.auth_req_id, badAttestation)).json.error, "invalid_receipt");

  const request2 = await fetchRequest(dev, c2.json.auth_req_id);
  assert.equal((await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request2, "271828"))).json.error, "receipt_mismatch", "another request's receipt cannot be re-attached");

  const carol = await device("carol");
  assert.equal((await postReceipt(carol, c.json.auth_req_id, good)).status, 404, "another user's device cannot answer");
  assert.equal((await call("POST", `/consent/requests/${c.json.auth_req_id}/receipt`, { body: good })).status, 401);

  assert.equal((await postReceipt(dev, c.json.auth_req_id, good)).status, 200, "the genuine receipt still works");
  assert.equal((await postReceipt(dev, c.json.auth_req_id, good)).status, 409, "decision is final");
});

// ---------------------------------------------------------------------------------------------------------------
// Direct entry

test("direct entry: response schema, binding_message signed only for verified clients, authorization_details refused", async () => {
  const g = group(tx("5000"));
  const body = (extra: object) => ({ scope: "sign", resource: "https://api.biatec.io", login_hint: "alice@example.com", network: "algorand-testnet", address: ALICE_ADDR, unsigned_group: g, origin: "https://bank.example", binding_message: "Invoice 2026-0042", ...extra });

  codes = ["120934"];
  const bank = await call("POST", "/connect/ciba", { auth: basic("bank-example", "bank-example-secret"), body: body({}) });
  assert.equal(bank.status, 200, JSON.stringify(bank.json));
  valid("backchannel-authentication-response", bank.json);
  const dev = await device(ALICE);
  const bankReq = await fetchRequest(dev, bank.json.auth_req_id);
  valid("consent-request", bankReq);
  assert.equal(bankReq.entry, "direct");
  assert.equal(bankReq.binding_message, "Invoice 2026-0042");
  assert.equal(bankReq.origin, "https://bank.example");
  assert.equal((await postReceipt(dev, bank.json.auth_req_id, receiptFor(dev, bankReq, "120934"))).status, 200);
  const t = await tokenReq(bank.json.auth_req_id, basic("bank-example", "bank-example-secret"));
  assert.equal(t.status, 200);
  valid("token-response", t.json);
  assert.equal((await sign(t.json.access_token, g)).status, 200, "the direct-entry token goes straight to the wallet endpoint");

  codes = ["775201"];
  const fake = await call("POST", "/connect/ciba", { auth: basic("unverified-app", "unverified-app-secret"), body: body({}) });
  const fakeReq = await fetchRequest(dev, fake.json.auth_req_id);
  valid("consent-request", fakeReq);
  assert.equal(fakeReq.binding_message, "", "unverified clients have no free-text channel");
  assert.equal(fakeReq.client.verified, false);
  assert.equal(fakeReq.client.display_name, undefined);

  assert.equal((await call("POST", "/connect/ciba", { auth: chatClient, body: body({ authorization_details: [{ type: "biatec_transaction" }] }) })).status, 400);
  assert.equal((await call("POST", "/connect/ciba", { auth: chatClient, body: body({ login_hint: "nobody@example.com" }) })).json.error, "unknown_user_id");
  assert.equal((await call("POST", "/connect/ciba", { auth: chatClient, body: body({ login_hint: "bob@example.com" }) })).json.reason, "no_active_device");
  assert.equal((await call("POST", "/connect/ciba", { auth: basic("bank-example", "bad"), body: body({}) })).status, 401);
  assert.equal((await call("POST", "/connect/ciba", { auth: basic("bank-example", "bank-example-secret"), body: body({ scope: "manage-limits", limits_request: { per_day_eur: "1", per_transaction_eur: "1" }, unsigned_group: undefined, network: undefined, address: undefined }) })).status, 403, "manage-limits is allowlisted");
});

test("direct entry accepts form-encoded parameters", async () => {
  codes = ["654321"];
  const r = await call("POST", "/connect/ciba", {
    auth: chatClient,
    form: { scope: "sign", resource: "https://api.biatec.io", login_hint: "alice@example.com", network: "algorand-testnet", address: ALICE_ADDR, unsigned_group: group(tx("2000"))[0], origin: "https://app.biatec.io", requested_expiry: "120" },
  });
  assert.equal(r.status, 200, JSON.stringify(r.json));
  assert.equal(r.json.expires_in, 120);
});

// ---------------------------------------------------------------------------------------------------------------
// Limits change and device enrollment

test("limits: tightening applies at once, loosening needs consent bound to the request body hash", async () => {
  const tight = await call("PUT", "/wallet/limits", { auth: bearer("alice-chat-limits"), body: { per_transaction_eur: "500", per_day_eur: "1000" } });
  assert.equal(tight.status, 200);

  codes = ["905512"];
  const loose = { per_transaction_eur: "2000", per_day_eur: "5000" };
  const c = await call("PUT", "/wallet/limits", { auth: bearer("alice-chat-limits"), body: loose });
  assert.equal(c.status, 401);
  const dev = await device(ALICE);
  const request = await fetchRequest(dev, c.json.auth_req_id);
  valid("consent-request", request);
  assert.equal(request.authorization_details[0].type, "biatec_limits_change");
  assert.deepEqual(request.authorization_details[0].current_limits, { per_transaction_eur: "500", per_day_eur: "1000" });
  await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, "905512"));
  const t = await tokenReq(c.json.auth_req_id);
  assert.equal((await call("PUT", "/wallet/limits", { auth: bearer(t.json.access_token), body: { per_transaction_eur: "9000", per_day_eur: "5000" } })).json.error, "group_hash_mismatch");
  assert.equal((await call("PUT", "/wallet/limits", { auth: bearer(t.json.access_token), body: loose })).status, 200);
  assert.equal((await call("PUT", "/wallet/limits", { auth: bearer("alice-chat-sign"), body: loose })).status, 403, "scope manage-limits required");
});

test("rule 7: a first device is inactive for 24 hours unless an active device approves", async () => {
  const fresh = (await import("node:crypto")).generateKeyPairSync("ec", { namedCurve: "P-256" }).publicKey.export({ format: "jwk" }) as Record<string, string>;
  assert.equal((await call("POST", "/devices", { auth: bearer("alice-chat-sign"), body: { kid: "k0", public_key: fresh } })).status, 403, "needs a full login");

  const e1 = await call("POST", "/devices", { auth: bearer("login-bob"), body: { kid: "bob-phone", public_key: fresh } });
  assert.equal(e1.status, 201);
  assert.equal(e1.json.active, false);
  assert.equal(e1.json.active_at, clock + 86400);
  assert.ok(e1.json.attestation.split(".").length === 3);
  assert.equal((await call("GET", "/devices/requests", { auth: "Device " + e1.json.device_token })).status, 200);

  const e2 = await call("POST", "/devices", { auth: bearer("login-alice"), body: { kid: "alice-second", public_key: fresh, approved_by_kid: "alice-device" } });
  assert.equal(e2.json.active, true);
  const e3 = await call("POST", "/devices", { auth: bearer("login-alice"), body: { kid: "alice-third", public_key: fresh, approved_by_kid: "bob-phone" } });
  assert.equal(e3.status, 400);
  assert.equal((await call("POST", "/devices", { auth: bearer("login-alice"), body: { kid: "alice-third", public_key: { kty: "RSA" } } })).status, 400);

  // Bob still cannot complete an above-threshold action while his device is inactive (rules 7 and 8).
  const b = await call("POST", `/wallet/algorand-testnet/${ALICE_ADDR}/sign`, { auth: bearer("bob-chat-sign"), body: { unsigned_group: group(tx("5000")) } });
  assert.equal(b.json.reason, "no_active_device");
  clock += 86400;
  const b2 = await call("POST", `/wallet/algorand-testnet/${ALICE_ADDR}/sign`, { auth: bearer("bob-chat-sign"), body: { unsigned_group: group(tx("5000")) } });
  assert.equal(b2.status, 401, "after 24 hours the device is active");
  clock -= 86400;
});
