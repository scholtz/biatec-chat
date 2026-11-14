import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { AddressInfo } from "node:net";
import Ajv2020Module from "ajv/dist/2020.js";
import algosdk from "algosdk";
import { createHash, randomBytes } from "node:crypto";
import { jcs, signEs256, verifyApproval, verifyReceipt } from "../../scripts/lib.mjs";
import { DEVICE_PROOF_TYP, MAX_PENDING_PER_CLIENT_ORIGIN, MAX_PUSH_REGISTRATIONS_PER_MINUTE, createMockOidc } from "../src/app.ts";
import { account, buildGroup, usdcTransfer, type TxSpec } from "../src/build.ts";
import { base32, encodeTx, type Tx } from "../src/group.ts";

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
// Every test opens its requests from an origin of its own (the limit is three pending per client, user and origin), and the hourly rate is not under test here.
const mock = createMockOidc({ now: () => clock, newBindingCode: () => codes.shift() ?? "483920", testHelpers: true, maxRequestsPerHour: 1_000_000 });
let base = "";
before(async () => {
  await new Promise<void>((r) => mock.server.listen(0, "127.0.0.1", r));
  base = `http://127.0.0.1:${(mock.server.address() as AddressInfo).port}`;
});
after(() => mock.server.close());

test("BCHAT-006: without test helpers /mock/* is not served and the seeded device token is not predictable", async () => {
  const plain = createMockOidc();
  await new Promise<void>((r) => plain.server.listen(0, "127.0.0.1", r));
  const b = `http://127.0.0.1:${(plain.server.address() as AddressInfo).port}`;
  try {
    assert.equal((await fetch(`${b}/mock/devices/alice-device`)).status, 404);
    assert.equal((await fetch(`${b}/mock/push-outbox`)).status, 404);
    assert.equal((await fetch(`${b}/mock/devices/alice-device/activate`, { method: "POST" })).status, 404);
    const r = await fetch(`${b}/devices/requests`, { headers: { Authorization: "Device device-token-alice" } });
    assert.equal(r.status, 401, "the well-known seeded token is rejected");
  } finally {
    plain.server.close();
  }
});

const ALICE = "alice";
const ALICE_ADDR = account("alice");
const PAYEE = account("payee");
const ATTACKER = account("attacker");
/** A transfer of `usdc` whole USDC from alice's address: 0.9225 EUR each in the mock's price table. */
const tx = (usdc: number, extra: Partial<TxSpec> = {}): TxSpec => usdcTransfer(ALICE_ADDR, PAYEE, usdc, extra);
const group = (...t: TxSpec[]) => buildGroup(t).items;

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
let originSeq = 0;
const nextOrigin = () => `https://site${++originSeq}.example`;
const sign = (token: string, g: string[], extra: object = {}, network = "algorand-testnet") => call("POST", `/wallet/${network}/${ALICE_ADDR}/sign`, { auth: bearer(token), body: { transactions: g, origin: nextOrigin(), ...extra } });
const tokenReq = (authReqId: string, auth = chatClient) =>
  call("POST", "/token", { auth, form: { grant_type: "urn:openid:params:grant-type:ciba", auth_req_id: authReqId } });

const deviceAuth = (user: string) => "Device device-token-" + user;
async function device(user: string) {
  const d = (await call("GET", `/mock/devices/${user}-device`)).json;
  return { auth: deviceAuth(user), attestation: d.attestation as string, publicKey: d.public_key as Record<string, string>, kid: d.kid as string, presence: d.presence as { kid: string; public_key: Record<string, string>; attestation: string } };
}
type Dev = Awaited<ReturnType<typeof device>>;

/** What the authenticator does: build the 1.1 receipt from the fetched request and the code the user typed, sign it with the approval key (or the presence key, or another). */
function receiptFor(dev: Dev, request: any, code: string, decision = "approve", signingKey = keys.device, opts: { presence?: boolean; remember?: number[] } = {}) {
  const signed = {
    authorization_details: request.authorization_details,
    auth_req_id: request.auth_req_id,
    client_fingerprint: request.client.fingerprint,
    binding_code: code,
    decision,
    expiry: request.expiry,
    sub: request.sub,
    binding_message: request.binding_message,
    ...(opts.remember ? { remember: { applications: opts.remember } } : {}),
  };
  const device = opts.presence
    ? { kid: dev.presence.kid, key_use: "presence", public_key: dev.presence.public_key, attestation: dev.presence.attestation }
    : { kid: dev.kid, key_use: "approve", public_key: dev.publicKey, attestation: dev.attestation };
  return {
    version: "1.1",
    signed,
    alg: "ES256",
    signature: signEs256(opts.presence && signingKey === keys.device ? keys.presence : signingKey, Buffer.from(jcs(signed), "utf8")),
    issued_at: clock,
    device,
  };
}
const postReceipt = (dev: Dev, id: string, receipt: unknown) => call("POST", `/consent/requests/${id}/receipt`, { auth: dev.auth, body: receipt });

/** Opens a step-up challenge for a group above the threshold (about 4600 EUR unless another group is given). */
async function challenge(g = group(tx(5000)), token = "alice-chat-sign", extra: object = {}) {
  const r = await sign(token, g, extra);
  assert.equal(r.status, 401, JSON.stringify(r.json));
  return r;
}
const fetchRequest = async (dev: Dev, id: string) => (await call("GET", `/consent/requests/${id}`, { auth: dev.auth })).json;

/** Challenge, approval, token: the elevated token for `g`. */
async function elevated(g: string[], code = "483920", token = "alice-chat-sign", extra: object = {}) {
  codes = [code];
  const c = await challenge(g, token, extra);
  const dev = await device(ALICE);
  const request = await fetchRequest(dev, c.json.auth_req_id);
  const posted = await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, code));
  assert.equal(posted.status, 200, JSON.stringify(posted.json));
  const t = await tokenReq(c.json.auth_req_id);
  assert.equal(t.status, 200, JSON.stringify(t.json));
  return { c, dev, request, token: t.json.access_token as string, t };
}

// ---------------------------------------------------------------------------------------------------------------
// Acceptance: step-up on sign returns 401 with auth_req_id and binding code

test("step-up on sign: 401 with RFC 9470 challenge, auth_req_id and binding code", async () => {
  codes = ["483920"];
  const r = await challenge();
  valid("stepup-challenge", r.json);
  assert.equal(r.json.binding_code, "483920");
  assert.equal(r.json.expires_in, 300);
  assert.match(r.headers.get("www-authenticate")!, /error="insufficient_user_authentication", error_description="consent required", acr_values="biatec:consent"/);
});

test("below threshold signs directly; requireConsent and always-ask force the step-up", async () => {
  const small = await sign("alice-chat-sign", group(tx(10)));
  assert.equal(small.status, 200);
  assert.equal(small.json.valuedEur, "9.23", "the value the mock priced: 10 USDC at 0.9225");
  assert.deepEqual(small.json.signedTransactions, small.json.signed_group);
  assert.equal(algosdk.decodeSignedTransaction(Buffer.from(small.json.signedTransactions[0], "base64")).txn.sender.toString(), ALICE_ADDR);
  assert.equal((await sign("alice-chat-sign", group(tx(10)), { requireConsent: true })).status, 401);
  assert.equal((await sign("carol-chat-sign", group(tx(10)))).status, 401, "always-ask forces the phone even below the threshold");
});

test("scope and sender checks", async () => {
  assert.equal((await sign("alice-chat-nosign", group(tx(10)))).status, 403);
  assert.equal((await sign("alice-chat-sign", group(tx(10, { sender: ATTACKER })))).status, 400, "one sender per call");
  assert.equal((await call("POST", `/wallet/algorand-testnet/${ALICE_ADDR}/sign`, {})).status, 401);
  assert.equal((await sign("alice-chat-sign", ["not a transaction"])).status, 400);
  assert.equal((await sign("alice-chat-sign", [])).status, 400);
});

test("rule 4: first use of a client always needs the phone; rule 8: no device, no above-threshold action", async () => {
  assert.equal((await sign("alice-bank-sign", group(tx(5)))).status, 401, "first use of bank-example");
  const bob = await sign("bob-chat-sign", group(tx(5000)));
  assert.equal(bob.status, 403);
  assert.equal(bob.json.error, "consent_unavailable");
  assert.equal(bob.json.reason, "no_active_device");
  assert.equal((await sign("bob-chat-sign", group(tx(5)))).status, 200, "below threshold still works without a device");
});

// ---------------------------------------------------------------------------------------------------------------
// Acceptance: CIBA token returned after valid receipt

test("full step-up flow: device fetches request, approves, CIBA token carries a verifiable consent_receipt, sign spends it once", async () => {
  codes = ["120934"];
  const g = group(tx(5000));
  const c = await challenge(g);
  const dev = await device(ALICE);

  assert.equal((await tokenReq(c.json.auth_req_id)).json.error, "authorization_pending");

  const request = await fetchRequest(dev, c.json.auth_req_id);
  valid("consent-request", request);
  assert.equal("binding_code" in request, false, "the authenticator never receives the code (rule 2)");
  assert.equal(request.binding_message, "");
  assert.equal(request.entry, "step-up");
  assert.deepEqual(request.client, { fingerprint: createHash("sha256").update("biatec-chat").digest("hex").slice(0, 16), domain_proven: true, domain: "chat.biatec.io", linked: true });
  const d = request.authorization_details[0];
  assert.equal(d.value_known, true);
  assert.deepEqual(d.fiat_value, { amount: "4612.50", currency: "EUR" });
  assert.equal(d.transfers.length, 1);
  assert.equal(d.transfers[0].receiver, PAYEE);
  assert.deepEqual(d.transfers[0].asset, { id: "31566704", symbol: "USDC", decimals: 6 });
  assert.equal(d.transfers[0].amount, "5000.000000");
  assert.deepEqual(d.applications, []);
  assert.equal(d.origin, request.origin);
  assert.equal(request.bound_hash, d.group_hash);
  assert.equal(d.group_hash, algosdk.decodeUnsignedTransaction(Buffer.from(g[0], "base64")).txID(), "one ungrouped transaction: its transaction id");
  const list = (await call("GET", "/devices/requests", { auth: dev.auth })).json.requests;
  assert.ok(list.some((x: any) => x.auth_req_id === c.json.auth_req_id));

  const posted = await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, "120934"));
  assert.equal(posted.status, 200, JSON.stringify(posted.json));

  const t = await tokenReq(c.json.auth_req_id);
  assert.equal(t.status, 200, JSON.stringify(t.json));
  valid("token-response", t.json);
  assert.equal("refresh_token" in t.json, false);
  assert.ok(verifyReceipt(t.json.consent_receipt, vectorJwks, { issuer: mock.issuer, now: clock }), "offline verifiable with the published JWKS");
  assert.equal(t.json.consent_receipt.version, "1.1");
  assert.equal(t.json.consent_receipt.device.key_use, "approve");
  const jwks = (await call("GET", "/.well-known/jwks.json")).json;
  assert.ok(verifyReceipt(t.json.consent_receipt, jwks, { issuer: mock.issuer, now: clock }));
  assert.equal((await tokenReq(c.json.auth_req_id)).json.error, "invalid_grant", "auth_req_id redeems once");
  assert.equal((await call("GET", `/consent/receipts/${c.json.auth_req_id}`, { auth: chatClient })).status, 200);
  assert.equal((await call("GET", `/consent/receipts/${c.json.auth_req_id}`, { auth: basic("bank-example", "bank-example-secret") })).status, 404, "only the owning client");

  const ok = await sign(t.json.access_token, g);
  assert.equal(ok.status, 200, JSON.stringify(ok.json));
  assert.equal(ok.json.valuedEur, "4612.50");
  const again = await sign(t.json.access_token, g);
  assert.equal(again.status, 401, "rule 5: single use");
  assert.equal(again.json.error, "insufficient_user_authentication");
  assert.equal(again.json.error_description, "consent not usable");
  assert.equal("auth_req_id" in again.json, false, "no new challenge: the client starts over with its ordinary token");
});

test("a real two-transaction group round-trips: challenge, receipt, token, signed; one byte changed after approval is consent_hash_mismatch", async () => {
  const spec = [tx(3000), tx(2000, { receiver: account("second payee") })];
  const built = buildGroup(spec);
  assert.ok(built.grp);
  const { c, dev, request, token } = await elevated(built.items, "246813", "alice-chat-sign", { grp: built.grp });
  void c;
  const d = request.authorization_details[0];
  assert.equal(d.group_hash, base32(built.txns[0].group!), "group_hash is the group id");
  assert.match(d.group_hash, /^[A-Z2-7]{52}$/);
  assert.equal(Buffer.from(built.grp!, "base64").length, 32);
  assert.equal(d.transfers.length, 2, "every transfer is listed, none summarized");
  assert.deepEqual(d.fiat_value, { amount: "4612.50", currency: "EUR" });
  valid("consent-request", request);

  // One byte different (a note), with a consistent new group id: the consent is bound to the group it was granted for.
  const altered = buildGroup([tx(3000, { note: "x" }), spec[1]]);
  const mismatch = await sign(token, altered.items, { grp: altered.grp });
  assert.equal(mismatch.status, 403);
  assert.equal(mismatch.json.error, "consent_hash_mismatch");
  // The same bytes with the old group id left in: the recomputed id differs, the group itself is inconsistent.
  const stale = built.items.map((x, i) => (i === 0 ? buildGroup([tx(3000, { note: "x" })], { grouped: false }).items[0] : x));
  assert.equal((await sign(token, stale, { grp: built.grp })).json.error, "group_hash_mismatch");

  const ok = await sign(token, built.items, { grp: built.grp });
  assert.equal(ok.status, 200, JSON.stringify(ok.json));
  assert.equal(ok.json.signedTransactions.length, 2);
  assert.equal(ok.json.group_hash, d.group_hash);
  for (const s of ok.json.signedTransactions) algosdk.decodeSignedTransaction(Buffer.from(s, "base64"));
  void dev;
});

test("the group id is recomputed: an inconsistent grp or group is 400 group_hash_mismatch", async () => {
  const built = buildGroup([tx(10), tx(20, { receiver: ATTACKER })]);
  const other = buildGroup([tx(10, { note: "other" }), tx(20, { receiver: ATTACKER })]);
  assert.equal((await sign("alice-chat-sign", built.items, { grp: built.grp })).status, 200);
  const badGrp = await sign("alice-chat-sign", built.items, { grp: other.grp });
  assert.equal(badGrp.status, 400);
  assert.equal(badGrp.json.error, "group_hash_mismatch");
  assert.equal((await sign("alice-chat-sign", other.items.slice(0, 1))).status, 400, "a partial group");
  assert.equal((await sign("alice-chat-sign", [built.items[0], other.items[1]])).json.error, "group_hash_mismatch", "transactions of two groups");
  const ungrouped = buildGroup([tx(10), tx(20)], { grouped: false });
  assert.equal((await sign("alice-chat-sign", ungrouped.items)).json.error, "group_hash_mismatch", "more than one transaction with no group");
  assert.equal((await sign("alice-chat-sign", group(tx(10)), { grp: built.grp })).json.error, "group_hash_mismatch", "grp for an ungrouped transaction");
});

test("what cannot be consented is refused: rekey, close-to, clawback and other transaction types", async () => {
  const bad: Record<string, TxSpec> = {
    rekey: tx(10, { rekeyTo: ATTACKER }),
    "payment close-to": { sender: ALICE_ADDR, receiver: PAYEE, amount: 1000, closeTo: ATTACKER },
    "asset close-to": tx(10, { closeTo: ATTACKER }),
    clawback: tx(10, { clawbackFrom: ATTACKER }),
    keyreg: { sender: ALICE_ADDR, keyreg: true },
  };
  for (const [name, spec] of Object.entries(bad)) {
    const r = await sign("alice-chat-sign", group(spec));
    assert.equal(r.status, 403, name);
    assert.equal(r.json.error, "consent_unsupported_operation", name);
  }
  // Inside a group, too.
  const r = await sign("alice-chat-sign", group(tx(10), bad.rekey));
  assert.equal(r.json.error, "consent_unsupported_operation");
});

test("authorization_details 1.1: applications and unknown value; remember trusts an application for this client only", async () => {
  const app = (id: number): TxSpec => ({ sender: ALICE_ADDR, appId: id });
  const g = buildGroup([tx(10), app(777)]);
  codes = ["135791"];
  const c = await challenge(g.items, "alice-chat-sign", { grp: g.grp });
  const dev = await device(ALICE);
  const req = await fetchRequest(dev, c.json.auth_req_id);
  valid("consent-request", req);
  const d = req.authorization_details[0];
  assert.deepEqual(d.applications, [{ id: 777, trusted: false }]);
  assert.equal(d.value_known, false, "an untrusted application makes the value unknown");
  assert.equal("fiat_value" in d, false);
  assert.equal(d.transfers[0].fiat_value.amount, "9.23");

  // An asset the mock cannot price makes the value unknown, and asks the phone even for a tiny amount.
  const unknownAsset = await sign("alice-chat-sign", group({ sender: ALICE_ADDR, receiver: PAYEE, asset: 999999, amount: 5 }));
  assert.equal(unknownAsset.status, 401);

  // Approving with `remember` trusts the application: the next group shows it as trusted and is priced.
  const approve = await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, req, "135791", "approve", keys.device, { remember: [777] }));
  assert.equal(approve.status, 200, JSON.stringify(approve.json));
  const settings = (await call("GET", "/consent/settings", { auth: bearer("alice-chat-sign") })).json;
  assert.deepEqual(settings.client.trusted_applications, [{ network: "algorand-testnet", app_id: 777 }]);
  assert.deepEqual((await call("GET", "/consent/settings", { auth: bearer("alice-bank-sign") })).json.client.trusted_applications, [], "trust is per client");
  const next = await sign("alice-chat-sign", buildGroup([tx(10, { note: "n" }), app(777)]).items);
  assert.equal(next.status, 200, "a trusted application and a priced transfer: below the threshold");
  assert.equal(next.json.valuedEur, "9.23");
  // Remembering an application the card did not offer is refused.
  const c2 = await challenge(g.items, "alice-chat-sign", { grp: g.grp, requireConsent: true });
  const req2 = await fetchRequest(dev, c2.json.auth_req_id);
  assert.equal(req2.authorization_details[0].applications[0].trusted, true);
  const nothing = await postReceipt(dev, c2.json.auth_req_id, receiptFor(dev, req2, "483920", "approve", keys.device, { remember: [777] }));
  assert.equal(nothing.status, 400);
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
  const g = group(tx(5000));
  const { token } = await elevated(g, "222333");

  const other = await sign(token, group(tx(5000, { receiver: ATTACKER })));
  assert.equal(other.status, 403);
  assert.equal(other.json.error, "consent_hash_mismatch");
  assert.equal((await sign(token, group(tx(4000)))).status, 403);
  assert.equal((await sign(token, g)).status, 200, "still unspent after the refused attempts");
});

test("rule 5: a consent approved for testnet is not usable on mainnet", async () => {
  const g = group(tx(5000));
  const { token } = await elevated(g, "444555");
  const main = await sign(token, g, {}, "algorand-mainnet");
  assert.equal(main.status, 401);
  assert.equal(main.json.error_description, "consent not usable");
  assert.equal((await sign(token, g)).status, 200, "still unspent on the approved network");
});

test("a consent of another client, another sender or another action is not usable", async () => {
  const g = group(tx(5000));
  const { token } = await elevated(g, "314151");
  // Another sender address in the route: the group's sender differs from the route, which is refused before the consent is looked at.
  assert.equal((await call("POST", `/wallet/algorand-testnet/${ATTACKER}/sign`, { auth: bearer(token), body: { transactions: g, origin: nextOrigin() } })).status, 400);
  // The consent token is the user's and the client's: never accepted by another endpoint.
  assert.equal((await call("PUT", "/wallet/limits", { auth: bearer(token), body: { per_transaction_eur: "1", per_day_eur: "1" } })).status, 403, "granted for sign, not for manage-limits (no such scope)");
  assert.equal((await sign(token, g)).status, 200);
});

test("the consent token is refused on every endpoint but the wallet signing ones", async () => {
  const g = group(tx(5000));
  const { token } = await elevated(g, "271829");
  const refused = [
    await call("GET", "/consent/settings", { auth: bearer(token) }),
    await call("POST", "/devices", { auth: bearer(token), body: { kid: "k-consent", public_key: await freshJwk() } }),
    await call("POST", "/devices/challenge", { auth: bearer(token) }),
    await call("GET", "/consent/receipts/ciba_00000000000000000000000000000000", { auth: bearer(token) }),
  ];
  for (const r of refused) assert.equal(r.status, 401, JSON.stringify(r.json));
  assert.match(refused[0].json.error_description, /wallet signing endpoints/);
  assert.equal((await sign(token, g)).status, 200, "and none of that spent it");
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
  const g = group(tx(5000));
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

test("maxConsentSeconds bounds the consent window and a value below 30 is refused", async () => {
  assert.equal((await sign("alice-chat-sign", group(tx(5000)), { maxConsentSeconds: 29 })).status, 400);
  assert.equal((await sign("alice-chat-sign", group(tx(5000)), { maxConsentSeconds: 29 })).json.error_description, "maxConsentSeconds too small");
  const r = await challenge(group(tx(5000)), "alice-chat-sign", { maxConsentSeconds: 60 });
  assert.equal(r.json.expires_in, 60);
  const big = await challenge(group(tx(5000)), "alice-chat-sign", { maxConsentSeconds: 900 });
  assert.equal(big.json.expires_in, 300, "never more than the default");
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

test("rule 10: explicit deny yields a deny receipt and access_denied; the presence key may sign it, but never an approval", async () => {
  codes = ["112233", "112234"];
  const c = await challenge();
  const dev = await device(ALICE);
  const request = await fetchRequest(dev, c.json.auth_req_id);
  assert.equal((await postReceipt(dev, c.json.auth_req_id, receiptFor(dev, request, "", "deny"))).status, 200);
  const t = await tokenReq(c.json.auth_req_id);
  assert.equal(t.json.error, "access_denied");
  assert.equal(t.json.reason, undefined);

  const c2 = await challenge();
  const request2 = await fetchRequest(dev, c2.json.auth_req_id);
  const byPresence = await postReceipt(dev, c2.json.auth_req_id, receiptFor(dev, request2, "112234", "approve", keys.device, { presence: true }));
  assert.equal(byPresence.status, 400, "an approve receipt signed by the presence key is refused");
  assert.equal(byPresence.json.error, "invalid_receipt");
  assert.equal((await postReceipt(dev, c2.json.auth_req_id, receiptFor(dev, request2, "", "deny", keys.device, { presence: true }))).status, 200, "a deny may be signed by the presence key");
});

test("a protocol 1.0 receipt is refused: the mock speaks 1.1", async () => {
  codes = ["112235"];
  const c = await challenge();
  const dev = await device(ALICE);
  const request = await fetchRequest(dev, c.json.auth_req_id);
  const old = receiptFor(dev, request, "112235") as any;
  old.version = "1";
  delete old.device.key_use;
  old.signed = { ...old.signed, client_entity_id: "https://chat.biatec.io" };
  delete old.signed.client_fingerprint;
  const r = await postReceipt(dev, c.json.auth_req_id, old);
  assert.equal(r.status, 400);
});

// ---------------------------------------------------------------------------------------------------------------
// Rule 9: receipts verify offline; anything altered is refused

test("rule 9: altered, foreign-signed or other-request receipts are refused", async () => {
  codes = ["314159", "271828"];
  const c = await challenge();
  const c2 = await challenge(group(tx(6000)));
  const dev = await device(ALICE);
  const request = await fetchRequest(dev, c.json.auth_req_id);
  const good = receiptFor(dev, request, "314159");

  const tampered = structuredClone(good);
  tampered.signed.authorization_details[0].transfers[0].amount = "1.000000";
  assert.equal((await postReceipt(dev, c.json.auth_req_id, tampered)).json.error, "invalid_receipt");

  const resigned = receiptFor(dev, { ...request, authorization_details: [{ ...request.authorization_details[0], transfers: [{ ...request.authorization_details[0].transfers[0], receiver: ATTACKER }] }] }, "314159");
  assert.equal((await postReceipt(dev, c.json.auth_req_id, resigned)).json.error, "receipt_mismatch");

  const foreignKey = receiptFor(dev, request, "314159", "approve", keys.oidc);
  assert.equal((await postReceipt(dev, c.json.auth_req_id, foreignKey)).json.error, "invalid_receipt");

  const badAttestation = structuredClone(good);
  badAttestation.device.attestation = good.device.attestation.slice(0, -4) + "AAAA";
  assert.equal((await postReceipt(dev, c.json.auth_req_id, badAttestation)).json.error, "invalid_receipt");

  const otherClient = structuredClone(good);
  otherClient.signed.client_fingerprint = "0".repeat(16);
  assert.ok(["receipt_mismatch", "invalid_receipt"].includes((await postReceipt(dev, c.json.auth_req_id, otherClient)).json.error));

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

test("direct entry: response schema, binding_message signed only for clients with a proven domain, authorization_details refused", async () => {
  const g = group(tx(5000));
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
  assert.equal(bankReq.authorization_details[0].origin, "https://bank.example");
  assert.equal(bankReq.client.domain, "bank.example");
  assert.equal((await postReceipt(dev, bank.json.auth_req_id, receiptFor(dev, bankReq, "120934"))).status, 200);
  const t = await tokenReq(bank.json.auth_req_id, basic("bank-example", "bank-example-secret"));
  assert.equal(t.status, 200);
  valid("token-response", t.json);
  assert.equal((await sign(t.json.access_token, g)).status, 200, "the direct-entry token goes straight to the wallet endpoint");

  codes = ["775201"];
  const fake = await call("POST", "/connect/ciba", { auth: basic("unverified-app", "unverified-app-secret"), body: body({}) });
  const fakeReq = await fetchRequest(dev, fake.json.auth_req_id);
  valid("consent-request", fakeReq);
  assert.equal(fakeReq.binding_message, "", "a client without a proven domain has no free-text channel");
  assert.equal(fakeReq.client.domain_proven, false);
  assert.equal(fakeReq.client.domain, undefined);
  assert.match(fakeReq.client.fingerprint, /^[0-9a-f]{16}$/);
  assert.equal("display_name" in fakeReq.client || "verified" in fakeReq.client || "entity_id" in fakeReq.client, false, "never a name taken from the operator or the client");

  assert.equal((await call("POST", "/connect/ciba", { auth: chatClient, body: body({ authorization_details: [{ type: "biatec_transaction" }] }) })).status, 400);
  assert.equal((await call("POST", "/connect/ciba", { auth: chatClient, body: body({ login_hint: "nobody@example.com" }) })).json.error, "unknown_user_id");
  assert.equal((await call("POST", "/connect/ciba", { auth: chatClient, body: body({ login_hint: "bob@example.com" }) })).json.reason, "no_active_device");
  assert.equal((await call("POST", "/connect/ciba", { auth: chatClient, body: body({ login_hint: "dave@example.com" }) })).json.error, "client_paused");
  assert.equal((await call("POST", "/connect/ciba", { auth: chatClient, body: body({ unsigned_group: group(tx(10, { rekeyTo: ATTACKER })) }) })).json.error, "consent_unsupported_operation");
  assert.equal((await call("POST", "/connect/ciba", { auth: chatClient, body: body({ origin: "https://Chat.Example/path" }) })).status, 400, "a canonical origin only");
  assert.equal((await call("POST", "/connect/ciba", { auth: basic("bank-example", "bad"), body: body({}) })).status, 401);
  assert.equal((await call("POST", "/connect/ciba", { auth: basic("bank-example", "bank-example-secret"), body: body({ scope: "manage-limits", limits_request: { per_day_eur: "1", per_transaction_eur: "1" }, unsigned_group: undefined, network: undefined, address: undefined }) })).status, 403, "manage-limits is allowlisted");
});

test("direct entry accepts form-encoded parameters", async () => {
  codes = ["654321"];
  const r = await call("POST", "/connect/ciba", {
    auth: chatClient,
    form: { scope: "sign", resource: "https://api.biatec.io", login_hint: "alice@example.com", network: "algorand-testnet", address: ALICE_ADDR, unsigned_group: group(tx(2000))[0], origin: "https://app.biatec.io", requested_expiry: "120" },
  });
  assert.equal(r.status, 200, JSON.stringify(r.json));
  assert.equal(r.json.expires_in, 120);
});

// ---------------------------------------------------------------------------------------------------------------
// The refusals of the real signing endpoint and the policy (issue 27 and 28)

test("the user's threshold is the user's: 5000 EUR asks for nothing below it, the default asks above 1000", async () => {
  const g = group(tx(3000)); // 2767.50 EUR
  assert.equal((await sign("alice-chat-sign", g)).status, 401, "above alice's 1000");
  const frank = await sign("frank-chat-sign", g);
  assert.equal(frank.status, 200, "below frank's 5000");
  assert.equal(frank.json.valuedEur, "2767.50");
  assert.equal((await sign("frank-chat-sign", group(tx(6000)))).status, 401, "5535 EUR is above 5000");
  assert.equal((await call("GET", "/consent/settings", { auth: bearer("frank-chat-sign") })).json.threshold_eur, "5000.00");
});

test("cooling-off, an unknown value and the test-network switch", async () => {
  assert.equal((await sign("alice-new-sign", group(tx(1)))).status, 401, "a client registered an hour ago asks for every request");
  const unknown = group({ sender: ALICE_ADDR, receiver: PAYEE, asset: 424242, amount: 1 });
  assert.equal((await sign("alice-chat-sign", unknown)).status, 401, "an asset the mock cannot price: unknown value");
  mock.state.accountSettings.set("pw_alice", { biometricRequired: true, blockUnverified: false, askOnTestNetworks: false });
  try {
    assert.equal((await sign("alice-chat-sign", unknown)).status, 200, "on a test network the user chose not to be asked about value");
    assert.equal((await sign("alice-chat-sign", group(tx(5000)))).status, 200);
    assert.equal((await sign("alice-chat-sign", group(tx(5000)), { requireConsent: true })).status, 401, "every other reason still applies");
    assert.equal((await sign("alice-chat-sign", unknown, {}, "algorand-mainnet")).status, 401, "mainnet still asks");
    assert.equal((await sign("alice-new-sign", group(tx(1)))).status, 401, "cooling-off still applies");
  } finally {
    mock.state.accountSettings.delete("pw_alice");
  }
});

test("403 refusals: client_paused, unproven_client_blocked, consent_unavailable, consent_unsupported_network", async () => {
  const paused = await sign("dave-chat-sign", group(tx(5)));
  assert.equal(paused.status, 403);
  assert.equal(paused.json.error, "client_paused");
  assert.ok(paused.json.paused_until > clock);

  mock.state.accountSettings.set("pw_alice", { biometricRequired: true, blockUnverified: true, askOnTestNetworks: true });
  try {
    const blocked = await sign("alice-unverified-sign", group(tx(5)));
    assert.equal(blocked.status, 403);
    assert.equal(blocked.json.error, "unproven_client_blocked");
    assert.equal((await sign("alice-chat-sign", group(tx(5)))).status, 200, "a client with a proven domain is not blocked");
  } finally {
    mock.state.accountSettings.delete("pw_alice");
  }

  const none = await sign("bob-chat-sign", group(tx(5000)));
  assert.deepEqual([none.status, none.json.error, none.json.reason], [403, "consent_unavailable", "no_active_device"]);
  assert.equal((await sign("bob-chat-sign", group(tx(5)), { requireConsent: true })).json.error, "consent_unavailable", "requireConsent cannot be met without a device");

  const btc = await sign("alice-chat-sign", ["cGlzYg=="], {}, "bitcoin-testnet");
  assert.deepEqual([btc.status, btc.json.error], [403, "consent_unsupported_network"]);
});

test("403 daily_cap_exceeded: no active device, the first day with a client, 100 EUR", async () => {
  const cap = (g: string[]) => sign("erin-chat-sign", g);
  const first = await cap(group(tx(50)));
  assert.equal(first.status, 200, JSON.stringify(first.json));
  assert.equal(first.json.valuedEur, "46.13");
  const over = await cap(group(tx(60, { note: "again" })));
  assert.equal(over.status, 403);
  assert.deepEqual([over.json.error, over.json.cap_eur, over.json.reason], ["daily_cap_exceeded", "100.00", "no_active_device"]);
  const unknown = await cap(group({ sender: ALICE_ADDR, receiver: PAYEE, asset: 424242, amount: 1 }));
  assert.equal(unknown.json.error, "daily_cap_exceeded", "an unknown value never fits under the cap");
  const before = clock;
  clock += 86401;
  try {
    assert.equal((await cap(group(tx(60, { note: "later" })))).status, 200, "a day later the cap window is over and the spend forgotten");
    assert.equal((await cap(group(tx(5000, { note: "much" })))).json.error, "consent_unavailable", "outside the first day the phone would be needed");
  } finally {
    clock = before;
  }
});

test("429 too_many_pending_requests: three per client, user and origin; ended requests no longer count", async () => {
  const own = createMockOidc({ now: () => clock, testHelpers: true });
  await new Promise<void>((r) => own.server.listen(0, "127.0.0.1", r));
  const b = `http://127.0.0.1:${(own.server.address() as AddressInfo).port}`;
  const open = (origin: string) => fetch(`${b}/wallet/algorand-testnet/${ALICE_ADDR}/sign`, { method: "POST", headers: { Authorization: bearer("alice-chat-sign"), "Content-Type": "application/json" }, body: JSON.stringify({ transactions: group(tx(5000)), origin }) });
  try {
    for (let i = 0; i < MAX_PENDING_PER_CLIENT_ORIGIN; i++) assert.equal((await open("https://dex.biatec.io")).status, 401, `request ${i + 1} opens`);
    const refused = await open("https://dex.biatec.io");
    assert.equal(refused.status, 429);
    assert.deepEqual(await refused.json(), { error: "too_many_pending_requests", error_description: "at most 3 requests of one client can be open for one user from one origin", limit: 3, scope: { client: "biatec-chat", origin: "https://dex.biatec.io" } });
    assert.equal((await open("https://other.biatec.io")).status, 401, "another origin has its own slots");
    const start = clock;
    try {
      clock += 301;
      assert.equal((await open("https://dex.biatec.io")).status, 401, "after the 300 s window the user can ask again");
    } finally {
      clock = start;
    }
  } finally {
    own.server.close();
  }
});

test("429 rate_limited: an hourly anti-spam limit per client and user, with Retry-After", async () => {
  const own = createMockOidc({ now: () => clock, testHelpers: true, maxRequestsPerHour: 3, maxPendingPerClientOrigin: 100 });
  await new Promise<void>((r) => own.server.listen(0, "127.0.0.1", r));
  const b = `http://127.0.0.1:${(own.server.address() as AddressInfo).port}`;
  const open = () => fetch(`${b}/wallet/algorand-testnet/${ALICE_ADDR}/sign`, { method: "POST", headers: { Authorization: bearer("alice-chat-sign"), "Content-Type": "application/json" }, body: JSON.stringify({ transactions: group(tx(5000)) }) });
  try {
    for (let i = 0; i < 3; i++) assert.equal((await open()).status, 401);
    const limited = await open();
    assert.equal(limited.status, 429);
    assert.equal(((await limited.json()) as any).error, "rate_limited");
    assert.ok(Number(limited.headers.get("retry-after")) >= 1);
    const start = clock;
    try {
      clock += 3601;
      assert.equal((await open()).status, 401, "an hour later it is allowed again");
    } finally {
      clock = start;
    }
  } finally {
    own.server.close();
  }
});

test("reserve then spend: a second use of an elevated token answers 401 consent not usable, also for two at once", async () => {
  const g = group(tx(5000));
  const { token } = await elevated(g, "999000");
  const [a, b] = await Promise.all([sign(token, g), sign(token, g)]);
  assert.deepEqual([a.status, b.status].sort(), [200, 401], "exactly one signature");
  const loser = a.status === 401 ? a : b;
  assert.equal(loser.json.error_description, "consent not usable");
  assert.match(loser.headers.get("www-authenticate")!, /insufficient_user_authentication/);
  const spent = [...mock.state.pending.values()].find((p) => p.spend === "spent" && p.request.bound_hash === algosdk.decodeUnsignedTransaction(Buffer.from(g[0], "base64")).txID());
  assert.ok(spent, "the record says spent");
});

test("GET /consent/settings: the calling client's view, bearer only", async () => {
  const s = (token: string) => call("GET", "/consent/settings", { auth: bearer(token) });
  assert.equal((await call("GET", "/consent/settings")).status, 401);
  const alice = (await s("alice-chat-sign")).json;
  assert.ok(Array.isArray(alice.client.trusted_applications));
  delete alice.client.trusted_applications; // alice trusted an application in an earlier test
  assert.deepEqual(alice, {
    threshold_eur: "1000.00",
    default_threshold_eur: "1000.00",
    ask_on_test_networks: true,
    device_enrolled: true,
    device_active: true,
    client: { linked: true, always_ask: false, paused_until: null, cooling_off_until: null, first_use: false, per_client_threshold_eur: null, domain_proven: true },
  });
  assert.equal((await s("frank-chat-sign")).json.threshold_eur, "5000.00");
  assert.equal((await s("frank-chat-sign")).json.default_threshold_eur, "1000.00");
  const bob = (await s("bob-chat-sign")).json;
  assert.deepEqual([bob.device_enrolled, bob.device_active], [false, false]);
  assert.equal((await s("carol-chat-sign")).json.client.always_ask, true);
  const dave = (await s("dave-chat-sign")).json.client;
  assert.ok(dave.paused_until > clock);
  const newApp = (await s("alice-new-sign")).json.client;
  assert.ok(newApp.cooling_off_until > clock && newApp.cooling_off_until <= clock + 86400);
  const unlinked = (await s("alice-unverified-sign")).json.client;
  assert.deepEqual([unlinked.linked, unlinked.first_use, unlinked.domain_proven], [false, true, false]);
  assert.equal((await call("GET", "/consent/settings?origin=nope", { auth: bearer("alice-chat-sign") })).status, 400);
});

test("a request opened under JSON groups (MOCK_JSON_GROUPS) is still described as protocol 1.1", async () => {
  const own = createMockOidc({ now: () => clock, testHelpers: true, jsonGroups: true });
  await new Promise<void>((r) => own.server.listen(0, "127.0.0.1", r));
  const b = `http://127.0.0.1:${(own.server.address() as AddressInfo).port}`;
  const jtx: Tx = { sender: "ALICEADDRESS", receiver: "PAYEE", asset: { id: "31566704", symbol: "USDC", decimals: 6 }, amount: "5000.000000", fiat_eur: "5000.00", application_id: 42 };
  try {
    const r = await fetch(`${b}/wallet/algorand-testnet/ALICEADDRESS/sign`, { method: "POST", headers: { Authorization: bearer("alice-chat-sign"), "Content-Type": "application/json" }, body: JSON.stringify({ unsigned_group: [encodeTx(jtx)], origin: "https://chat.biatec.io" }) });
    assert.equal(r.status, 401);
    const id = ((await r.json()) as any).auth_req_id;
    const req = (await (await fetch(`${b}/consent/requests/${id}`, { headers: { Authorization: "Device device-token-alice" } })).json()) as any;
    valid("consent-request", req);
    assert.equal(req.authorization_details[0].transfers[0].fiat_value.amount, "5000.00");
    assert.deepEqual(req.authorization_details[0].applications, [{ id: 42, trusted: false }]);
    assert.equal(req.authorization_details[0].value_known, false);
  } finally {
    own.server.close();
  }
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
  assert.equal((await call("PUT", "/wallet/limits", { auth: bearer(t.json.access_token), body: { per_transaction_eur: "9000", per_day_eur: "5000" } })).json.error, "consent_hash_mismatch");
  assert.equal((await call("PUT", "/wallet/limits", { auth: bearer(t.json.access_token), body: loose })).status, 200);
  assert.equal((await call("PUT", "/wallet/limits", { auth: bearer("alice-chat-sign"), body: loose })).status, 403, "scope manage-limits required");
});

test("rule 7: the recovery code is never in the enrollment response, only issued once active, shown once", async () => {
  const fresh = (await import("node:crypto")).generateKeyPairSync("ec", { namedCurve: "P-256" }).publicKey.export({ format: "jwk" }) as Record<string, string>;
  const e = await call("POST", "/devices", { auth: bearer("login-bob"), body: { kid: "bob-recovery-phone", public_key: fresh } });
  assert.equal(e.status, 201);
  assert.equal("recovery_code" in e.json, false, "no recovery code during enrollment");
  const dev = "Device " + e.json.device_token;
  const early = await call("POST", "/devices/recovery-code", { auth: dev });
  assert.equal(early.status, 403);
  assert.equal(early.json.reason, "device_inactive");
  clock += 86400;
  try {
    const first = await call("POST", "/devices/recovery-code", { auth: dev });
    assert.equal(first.status, 200);
    assert.match(first.json.recovery_code, /^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$/);
    assert.equal((await call("POST", "/devices/recovery-code", { auth: dev })).json.recovery_code, first.json.recovery_code, "re-served until acknowledged");
    assert.equal((await call("POST", "/devices/recovery-code/ack", { auth: dev })).status, 200);
    assert.equal((await call("POST", "/devices/recovery-code", { auth: dev })).status, 409, "shown once only");
  } finally {
    clock -= 86400;
  }
  assert.equal((await call("POST", "/devices/recovery-code", { auth: "Device nope" })).status, 401);
});

test("contract: distinct installs enroll with distinct kids, the same kid is rejected", async () => {
  const { randomBytes, generateKeyPairSync } = await import("node:crypto");
  const pub = () => generateKeyPairSync("ec", { namedCurve: "P-256" }).publicKey.export({ format: "jwk" }) as Record<string, string>;
  const kid = () => "biatec-authenticator-" + randomBytes(16).toString("hex");
  const a = kid();
  const b = kid();
  assert.notEqual(a, b);
  assert.equal((await call("POST", "/devices", { auth: bearer("login-alice"), body: { kid: a, public_key: pub() } })).status, 201);
  assert.equal((await call("POST", "/devices", { auth: bearer("login-bob"), body: { kid: b, public_key: pub() } })).status, 201);
  const dup = await call("POST", "/devices", { auth: bearer("login-bob"), body: { kid: "biatec-authenticator-device-key", public_key: pub() } });
  const dup2 = await call("POST", "/devices", { auth: bearer("login-alice"), body: { kid: "biatec-authenticator-device-key", public_key: pub() } });
  assert.equal(dup.status, 201);
  assert.equal(dup2.status, 409, "a constant kid only ever works for the first device");
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
  const b = await call("POST", `/wallet/algorand-testnet/${ALICE_ADDR}/sign`, { auth: bearer("bob-chat-sign"), body: { transactions: group(tx(5000)), origin: nextOrigin() } });
  assert.equal(b.json.reason, "no_active_device");
  clock += 86400;
  const b2 = await call("POST", `/wallet/algorand-testnet/${ALICE_ADDR}/sign`, { auth: bearer("bob-chat-sign"), body: { transactions: group(tx(5000)), origin: nextOrigin() } });
  assert.equal(b2.status, 401, "after 24 hours the device is active");
  clock -= 86400;
});

test("push: the payload is the request id only, sent to active devices with a registered token", async () => {
  const dev = await device(ALICE);
  assert.equal((await call("PUT", "/devices/push", { auth: dev.auth, body: { provider: "fcm" } })).status, 400);
  assert.equal((await call("PUT", "/devices/push", { auth: dev.auth, body: { provider: "sms", token: "t" } })).status, 400);
  assert.equal((await call("PUT", "/devices/push", { auth: "Device nope", body: { provider: "apns", token: "t" } })).status, 401);
  assert.equal((await call("PUT", "/devices/push", { auth: dev.auth, body: { provider: "apns", token: "apns-alice-1" } })).status, 200);

  const before = (await call("GET", `/mock/push-outbox?kid=${dev.kid}`)).json.messages.length;
  const r = await challenge();
  const sent = (await call("GET", `/mock/push-outbox?kid=${dev.kid}`)).json.messages;
  assert.equal(sent.length, before + 1);
  const msg = sent.at(-1);
  assert.deepEqual(msg, { kid: dev.kid, provider: "apns", token: "apns-alice-1", data: { auth_req_id: r.json.auth_req_id } });

  // A token given at enrollment is used once the device is active, and not while it is inactive.
  const fresh = (await import("node:crypto")).generateKeyPairSync("ec", { namedCurve: "P-256" }).publicKey.export({ format: "jwk" }) as Record<string, string>;
  const e = await call("POST", "/devices", { auth: bearer("login-alice"), body: { kid: "alice-push-phone", public_key: fresh, push: { provider: "apns", token: "apns-1" } } });
  assert.equal(e.status, 201);
  assert.equal((await call("POST", "/devices", { auth: bearer("login-alice"), body: { kid: "alice-push-bad", public_key: fresh, push: { provider: "apns", token: "" } } })).status, 400);
  await challenge();
  assert.equal((await call("GET", "/mock/push-outbox?kid=alice-push-phone")).json.messages.length, 0, "inactive device gets no push");
  await call("POST", "/mock/devices/alice-push-phone/activate");
  const r2 = await challenge();
  assert.deepEqual((await call("GET", "/mock/push-outbox?kid=alice-push-phone")).json.messages.map((m: any) => m.data), [{ auth_req_id: r2.json.auth_req_id }]);

  // The same phone token registered on another device record moves there: one token, one recipient.
  const carol = await device("carol");
  assert.equal((await call("PUT", "/devices/push", { auth: carol.auth, body: { provider: "apns", token: "apns-alice-1" } })).status, 200);
  const n = (await call("GET", `/mock/push-outbox?kid=${dev.kid}`)).json.messages.length;
  await challenge();
  assert.equal((await call("GET", `/mock/push-outbox?kid=${dev.kid}`)).json.messages.length, n, "alice's old record no longer holds the token");
});

// --- T-084: device list, cancelling a pending enrollment, consent settings ------------------------------------------

/** What the authenticator does for a loosening: sign JCS(biatec_settings_change) with the device key. */
function settingsConsent(dev: { kid: string }, change: unknown, nonce: string, overrides: Record<string, unknown> = {}, signingKey = keys.device) {
  const signed = { type: "biatec_settings_change", sub: "pw_alice", kid: dev.kid, nonce, issued_at: clock, change, ...overrides };
  return { signed, alg: "ES256", signature: signEs256(signingKey, Buffer.from(jcs(signed), "utf8")) };
}
const freshJwk = async () => (await import("node:crypto")).generateKeyPairSync("ec", { namedCurve: "P-256" }).publicKey.export({ format: "jwk" }) as Record<string, string>;

test("T-084: the device list shows pending enrollments and any device of the user cancels a pending one", async () => {
  const alice = await device(ALICE);
  const e = await call("POST", "/devices", { auth: bearer("login-alice"), body: { kid: "alice-cancel-me", public_key: await freshJwk() } });
  assert.equal(e.status, 201);
  assert.equal((await call("GET", "/devices")).status, 401);

  const list = (await call("GET", "/devices", { auth: alice.auth })).json.devices as any[];
  assert.equal(list.find((d) => d.kid === "alice-device").current, true);
  assert.equal(list.find((d) => d.kid === "alice-cancel-me").active, false);
  assert.ok(!list.some((d) => d.kid.startsWith("bob")), "only the user's own devices");

  assert.equal((await call("POST", "/devices/alice-device/cancel", { auth: alice.auth })).status, 409, "an active device is not cancelled here");
  assert.equal((await call("POST", "/devices/bob-phone/cancel", { auth: alice.auth })).status, 404, "another user's device");
  assert.equal((await call("POST", "/devices/alice-cancel-me/cancel", { auth: alice.auth })).status, 200);
  assert.ok(!((await call("GET", "/devices", { auth: alice.auth })).json.devices as any[]).some((d) => d.kid === "alice-cancel-me"));
  assert.equal((await call("GET", "/devices/requests", { auth: "Device " + e.json.device_token })).status, 401, "a cancelled device is locked out");
  assert.equal((await call("POST", "/devices/alice-cancel-me/cancel", { auth: alice.auth })).status, 404);
});

test("T-084: tightening settings applies at once; loosening needs a fresh device-signed consent", async () => {
  const alice = await device(ALICE);
  const put = (change: unknown, consent?: unknown) => call("PUT", "/devices/settings", { auth: alice.auth, body: { change, consent } });
  const view = async () => (await call("GET", "/devices/settings", { auth: alice.auth })).json;
  assert.equal((await view()).threshold_eur, "1000.00");

  // Tightening: no consent.
  let r = await put({ threshold_eur: "500", biometric_required: true, clients: { "biatec-chat": { always_ask: true } } });
  assert.equal(r.status, 200);
  assert.equal(r.json.loosened, false);
  let s = await view();
  assert.equal(s.threshold_eur, "500.00");
  assert.equal(s.clients.find((c: any) => c.client_id === "biatec-chat").always_ask, true);

  // Loosening without consent, signed for another change, by the wrong key, stale, for another user: refused, nothing changes.
  const up = { threshold_eur: "800" };
  assert.equal((await put(up)).status, 403);
  assert.equal((await put(up)).json.reason, "consent_required");
  assert.equal((await put(up, settingsConsent(alice, { threshold_eur: "900" }, "n1"))).status, 403, "signed another change");
  assert.equal((await put(up, settingsConsent(alice, up, "n2", {}, keys.oidc))).status, 403, "signed by the wrong key");
  assert.equal((await put(up, settingsConsent(alice, up, "n3", { issued_at: clock - 301 }))).status, 403, "stale");
  assert.equal((await put(up, settingsConsent(alice, up, "n4", { sub: "pw_carol" }))).status, 403, "another user");
  assert.equal((await view()).threshold_eur, "500.00");

  // A valid consent works once.
  const good = settingsConsent(alice, up, "n5");
  r = await put(up, good);
  assert.equal(r.status, 200);
  assert.equal(r.json.loosened, true);
  assert.equal((await view()).threshold_eur, "800.00");
  assert.equal((await put({ threshold_eur: "700" })).status, 200);
  assert.equal((await put(up, good)).status, 403, "replay of a spent consent");

  // Always ask off, biometric off, per-client threshold up and unblocking are all loosening.
  for (const change of [{ clients: { "biatec-chat": { always_ask: false } } }, { biometric_required: false }, { clients: { "bank-example": { per_client_threshold_eur: "5000" } } }]) {
    assert.equal((await put(change)).status, 403, JSON.stringify(change));
  }
  assert.equal((await put({ block_unverified_clients: true })).status, 200, "blocking is tightening");
  assert.equal((await put({ block_unverified_clients: false })).status, 403, "unblocking is loosening");
  const off = { clients: { "biatec-chat": { always_ask: false } } };
  assert.equal((await put(off, settingsConsent(alice, off, "n6"))).status, 200);
  assert.equal((await put({ clients: { "bank-example": { per_client_threshold_eur: "50" } } })).status, 200, "lower per-client threshold is tightening");

  // Bad shapes.
  for (const change of [{}, { nope: 1 }, { threshold_eur: "x" }, { clients: { ghost: { always_ask: true } } }, { clients: { "biatec-chat": { paused: true } } }, "str"]) {
    assert.equal((await put(change)).status, 400, JSON.stringify(change));
  }
  // Restore for the tests that follow.
  const back = { threshold_eur: "1000", block_unverified_clients: false, clients: { "bank-example": { per_client_threshold_eur: null } } };
  assert.equal((await put(back, settingsConsent(alice, back, "n7"))).status, 200);
  assert.equal((await view()).threshold_eur, "1000.00");
});

test("T-084: a client pause is lifted only with a device-signed consent", async () => {
  const alice = await device(ALICE);
  assert.equal((await call("POST", "/mock/users/pw_alice/clients/bank-example/pause")).status, 200);
  const find = async () => ((await call("GET", "/devices/settings", { auth: alice.auth })).json.clients as any[]).find((c) => c.client_id === "bank-example");
  assert.equal((await find()).paused_until, clock + 86400);
  const lift = { clients: { "bank-example": { paused: false } } };
  assert.equal((await call("PUT", "/devices/settings", { auth: alice.auth, body: { change: lift } })).status, 403);
  assert.equal((await find()).paused_until, clock + 86400);
  assert.equal((await call("PUT", "/devices/settings", { auth: alice.auth, body: { change: lift, consent: settingsConsent(alice, lift, "p1") } })).status, 200);
  assert.equal((await find()).paused_until, null);
});

test("T-084 / BCHAT-024: an inactive device changes no settings (tightening included) and cancels no enrollment", async () => {
  const e = await call("POST", "/devices", { auth: bearer("login-alice"), body: { kid: "alice-inactive", public_key: await freshJwk() } });
  const auth = "Device " + e.json.device_token;
  const change = { threshold_eur: "2000" };
  const r = await call("PUT", "/devices/settings", { auth, body: { change, consent: settingsConsent({ kid: "alice-inactive" }, change, "i1") } });
  assert.equal(r.status, 403);
  assert.equal((await call("PUT", "/devices/settings", { auth, body: { change: { threshold_eur: "100" } } })).status, 403, "tightening needs an active device too (rule 7)");
  const other = await call("POST", "/devices", { auth: bearer("login-alice"), body: { kid: "alice-inactive-2", public_key: await freshJwk() } });
  assert.equal((await call("POST", "/devices/alice-inactive-2/cancel", { auth })).status, 403, "a pending device cannot cancel another pending enrollment");
  assert.equal((await call("POST", "/devices/alice-inactive/cancel", { auth })).status, 200, "but it can cancel itself");
  const again = await call("POST", "/devices", { auth: bearer("login-alice"), body: { kid: "alice-inactive-3", public_key: await freshJwk() } });
  assert.equal(again.status, 201);
  assert.equal(other.status, 201);
  const alice = await device(ALICE);
  assert.equal((await call("PUT", "/devices/settings", { auth: alice.auth, body: { change: { threshold_eur: "100" } } })).status, 200, "an active device can tighten");
  const back = { threshold_eur: "1000" };
  assert.equal((await call("PUT", "/devices/settings", { auth: alice.auth, body: { change: back, consent: settingsConsent(alice, back, "i2") } })).status, 200);
  assert.equal((await call("POST", "/devices/alice-inactive-2/cancel", { auth: alice.auth })).status, 200, "an active device cancels a pending enrollment");
  assert.equal((await call("POST", "/devices/alice-inactive-3/cancel", { auth: alice.auth })).status, 200);
});

// ---------------------------------------------------------------------------------------------------------------
// BCHAT-024: rules the mock must model so that a non-conforming client cannot pass

test("BCHAT-024: the token endpoint answers slow_down to a client that polls too fast, and the interval grows by 5 s", async () => {
  codes = ["483920"];
  const r = await challenge();
  const id = r.json.auth_req_id;
  assert.equal(r.json.interval, 2, "the challenge announces the interval");
  const start = clock;
  try {
    const first = await tokenReq(id);
    assert.equal(first.json.error, "authorization_pending", "the first poll is judged on the request");
    assert.equal(first.json.interval, 2);
    const fast = await tokenReq(id);
    assert.equal(fast.json.error, "slow_down");
    assert.equal(fast.json.interval, 7);
    clock += 2;
    const stillFast = await tokenReq(id);
    assert.equal(stillFast.json.error, "slow_down", "7 s are needed now, only 2 passed");
    assert.equal(stillFast.json.interval, 12);
    clock += 12;
    const ok = await tokenReq(id);
    assert.equal(ok.json.error, "authorization_pending", "a poll after the interval is judged on the request again");
    assert.equal(ok.json.interval, 12, "and it tells the current interval");
  } finally {
    clock = start;
  }
});

test("BCHAT-024: an approved request is returned on the first poll, however soon", async () => {
  codes = ["483920"];
  const r = await challenge();
  const alice = await device(ALICE);
  const req = await fetchRequest(alice, r.json.auth_req_id);
  assert.equal((await postReceipt(alice, r.json.auth_req_id, receiptFor(alice, req, "483920"))).status, 200);
  const t = await tokenReq(r.json.auth_req_id);
  assert.equal(t.status, 200);
  assert.ok(t.json.consent_receipt);
});

test("BCHAT-024: a receipt is returned to the subject it was issued for, not to another user of the same client", async () => {
  codes = ["483920"];
  const r = await challenge();
  const id = r.json.auth_req_id;
  const alice = await device(ALICE);
  const req = await fetchRequest(alice, id);
  assert.equal((await postReceipt(alice, id, receiptFor(alice, req, "483920"))).status, 200);
  const own = await call("GET", `/consent/receipts/${id}`, { auth: bearer("alice-chat-sign") });
  assert.equal(own.status, 200);
  assert.equal((await call("GET", `/consent/receipts/${id}`, { auth: chatClient })).status, 200, "the client itself, by its credentials, can still fetch it");
  const other = await call("GET", `/consent/receipts/${id}`, { auth: bearer("carol-chat-sign") });
  assert.equal(other.status, 404, "another subject's token of the same client gets nothing");
});

// ---------------------------------------------------------------------------------------------------------------
// BCHAT-030: a device call needs a proof of possession of the device key, not only the bearer token

test("BCHAT-030: with proof mode on the device token alone is refused; a correct proof passes once", async () => {
  const strict = createMockOidc({ now: () => clock, testHelpers: true, deviceProof: true });
  await new Promise<void>((r) => strict.server.listen(0, "127.0.0.1", r));
  const b = `http://127.0.0.1:${(strict.server.address() as AddressInfo).port}`;
  try {
    const d = (await (await fetch(`${b}/mock/devices/alice-device`)).json()) as any;
    const token = d.device_token as string;
    const proof = (over: { header?: object; payload?: object; key?: typeof keys.device; method?: string; url?: string } = {}) => {
      const header = { alg: "ES256", typ: DEVICE_PROOF_TYP, kid: d.kid, ...over.header };
      const payload = {
        htm: over.method ?? "GET",
        htu: over.url ?? `${b}/devices/requests`,
        iat: clock,
        jti: randomBytes(12).toString("hex"),
        ath: createHash("sha256").update(token).digest("base64url"),
        ...over.payload,
      };
      const input = Buffer.from(JSON.stringify(header)).toString("base64url") + "." + Buffer.from(JSON.stringify(payload)).toString("base64url");
      return input + "." + signEs256(over.key ?? keys.device, Buffer.from(input, "utf8"));
    };
    const get = (p?: string) => fetch(`${b}/devices/requests`, { headers: { Authorization: "Device " + token, ...(p ? { "X-Device-Proof": p } : {}) } });

    assert.equal((await get()).status, 401, "the bearer alone is not enough");
    assert.equal((await get(proof())).status, 200);
    const replay = proof();
    assert.equal((await get(replay)).status, 200);
    assert.equal((await get(replay)).status, 401, "a proof is single use");
    assert.equal((await get(proof({ method: "POST" }))).status, 401, "wrong method");
    assert.equal((await get(proof({ url: `${b}/devices/settings` }))).status, 401, "wrong URL");
    assert.equal((await get(proof({ url: `${b}/proxy/devices/requests` }))).status, 401, "a URL that merely ends like the request is another URL");
    assert.equal((await get(proof({ payload: { ath: "x" } }))).status, 401, "not bound to the token");
    assert.equal((await get(proof({ payload: { iat: clock - 120 } }))).status, 401, "stale");
    assert.equal((await get(proof({ payload: { iat: clock + 120 } }))).status, 401, "from the future");
    assert.equal((await get(proof({ payload: { jti: "short" } }))).status, 401, "jti too short");
    assert.equal((await get(proof({ header: { kid: "someone-else" } }))).status, 401, "another device's kid");
    assert.equal((await get(proof({ header: { typ: "JWT" } }))).status, 401, "wrong typ");
    assert.equal((await get(proof({ header: { alg: "none" } }))).status, 401, "wrong alg");
    assert.equal((await get(proof({ key: keys.oidc }))).status, 401, "signed by a different key");
    assert.equal((await get("a.b")).status, 401);
    assert.equal((await get("not-a-jws")).status, 401);
  } finally {
    strict.server.close();
  }
});

test("BCHAT-030: push registration is rate limited per device", async () => {
  const dev = await device(ALICE);
  const reg = (i: number) => call("PUT", "/devices/push", { auth: dev.auth, body: { provider: "apns", token: "t-" + i } });
  // Other tests registered for this device on the shared mock; move to a clean minute.
  clock += 61;
  const start = clock;
  try {
    for (let i = 0; i < MAX_PUSH_REGISTRATIONS_PER_MINUTE; i++) assert.equal((await reg(i)).status, 200, `registration ${i + 1}`);
    assert.equal((await reg(99)).status, 429);
    clock += 61;
    assert.equal((await reg(100)).status, 200, "a minute later it is allowed again");
  } finally {
    clock = start - 61;
  }
});

test("T-232: every answer carries the lowest accepted authenticator build", async () => {
  const defaultMock = await call("GET", "/.well-known/jwks.json", {});
  assert.equal(defaultMock.headers.get("x-biatec-min-app-build"), "1");
  const strict = createMockOidc({ now: () => clock, minAppBuild: 7 });
  await new Promise<void>((r) => strict.server.listen(0, "127.0.0.1", r));
  try {
    const port = (strict.server.address() as AddressInfo).port;
    const res = await fetch(`http://127.0.0.1:${port}/.well-known/jwks.json`);
    assert.equal(res.headers.get("x-biatec-min-app-build"), "7");
    const bad = await fetch(`http://127.0.0.1:${port}/nowhere`);
    assert.equal(bad.status, 404);
    assert.equal(bad.headers.get("x-biatec-min-app-build"), "7", "errors carry it too");
  } finally {
    strict.server.close();
  }
});

test("T-234: a platform attestation is bound to a single-use challenge", async () => {
  const { generateKeyPairSync } = await import("node:crypto");
  const pub = () => generateKeyPairSync("ec", { namedCurve: "P-256" }).publicKey.export({ format: "jwk" }) as Record<string, string>;
  const ask = async (auth = bearer("login-alice")) => (await call("POST", "/devices/challenge", { auth })).json.nonce as string;
  const attestation = { kind: "app_attest", token: "dGVzdA==", key_id: "k1" };
  const enroll = (kid: string, body: Record<string, unknown>, auth = bearer("login-alice")) => call("POST", "/devices", { auth, body: { kid, public_key: pub(), ...body } });

  // A challenge needs a fresh login or an enrolled device, and is new each time.
  assert.equal((await call("POST", "/devices/challenge")).status, 401);
  const n1 = await ask();
  assert.ok(n1.length >= 24);
  assert.notEqual(await ask(), n1);

  // With a good challenge the enrollment carries the attestation and says when to refresh it.
  const ok = await enroll("att-1", { attestation_challenge: n1, platform_attestation: attestation });
  assert.equal(ok.status, 201);
  assert.ok(ok.json.attestation_refresh_after > clock);

  // The challenge is single use.
  assert.equal((await enroll("att-2", { attestation_challenge: n1, platform_attestation: attestation })).status, 400, "replayed");

  // A malformed attestation is refused before the challenge is used up; a missing or foreign challenge is refused too.
  const n2 = await ask();
  for (const bad of [{ kind: "made_up", token: "x" }, { kind: "app_attest", token: "" }, { kind: "app_attest" }, { kind: "play_integrity", token: "x", key_id: 5 }]) {
    assert.equal((await enroll("att-bad", { attestation_challenge: n2, platform_attestation: bad })).status, 400, JSON.stringify(bad));
  }
  assert.equal((await enroll("att-bad", { platform_attestation: attestation })).status, 400, "no challenge");
  assert.equal((await enroll("att-bad", { attestation_challenge: "never-issued", platform_attestation: attestation })).status, 400, "unknown challenge");
  assert.equal((await enroll("att-bad", { attestation_challenge: n2, platform_attestation: attestation }, bearer("login-bob"))).status, 400, "another user's challenge");
  assert.equal((await enroll("att-3", { attestation_challenge: n2, platform_attestation: attestation })).status, 201, "n2 was never spent by the refused ones");

  // A challenge expires.
  const n3 = await ask();
  const before = clock;
  clock += 301;
  try {
    assert.equal((await enroll("att-4", { attestation_challenge: n3, platform_attestation: attestation })).status, 400, "expired");
  } finally {
    clock = before;
  }

  // The two fields go together.
  const n5 = await ask();
  assert.equal((await enroll("att-orphan", { attestation_challenge: n5 })).status, 400, "a challenge without an attestation");
  assert.equal((await enroll("att-orphan", { platform_attestation: attestation })).status, 400, "an attestation without a challenge");
  assert.equal((await enroll("att-orphan", { attestation_challenge: n5, platform_attestation: null })).status, 400, "a null attestation");
  assert.equal((await enroll("att-6", { attestation_challenge: n5, platform_attestation: attestation })).status, 201, "the orphan did not spend n5");

  // Enrolling without an attestation still works: Biatec OIDC decides what to do with such a device.
  const none = await enroll("att-5", {});
  assert.equal(none.status, 201);
  assert.equal(none.json.attestation_refresh_after, undefined);

  // The refresh: an enrolled device asks for a challenge with its device token and sends a new attestation.
  const dev = "Device " + ok.json.device_token;
  const n4 = await ask(dev);
  const refreshed = await call("PUT", "/devices/attestation", { auth: dev, body: { attestation_challenge: n4, platform_attestation: attestation } });
  assert.equal(refreshed.status, 200);
  assert.ok(refreshed.json.attestation_refresh_after > clock);
  assert.equal((await call("PUT", "/devices/attestation", { auth: dev, body: { attestation_challenge: n4, platform_attestation: attestation } })).status, 400, "the refresh challenge is single use too");
  assert.equal((await call("PUT", "/devices/attestation", { body: {} })).status, 401);
});

// ---------------------------------------------------------------------------------------------------------------
// T-241 (D9): with two keys enforced, enrollment needs both proofs and the device proof is the presence key's

test("T-241: MOCK_TWO_KEYS: enrollment needs the enrollment proof, and the device proof must come from the presence key", async () => {
  const strict = createMockOidc({ now: () => clock, testHelpers: true, deviceProof: true, twoKeys: true });
  await new Promise<void>((r) => strict.server.listen(0, "127.0.0.1", r));
  const b = `http://127.0.0.1:${(strict.server.address() as AddressInfo).port}`;
  const post = (path: string, auth: string, body?: object) =>
    fetch(`${b}${path}`, { method: "POST", headers: { Authorization: auth, "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
  const pub = (k: any) => ({ kty: "EC", crv: "P-256", x: k.x, y: k.y });
  const thumb = (k: any) => createHash("sha256").update(JSON.stringify({ crv: "P-256", kty: "EC", x: k.x, y: k.y })).digest("base64url");
  const jws = (typ: string, signer: any, payload: object, jwk: any = pub(signer)) => {
    const input = Buffer.from(JSON.stringify({ alg: "ES256", typ, jwk })).toString("base64url") + "." + Buffer.from(JSON.stringify(payload)).toString("base64url");
    return { input, sig: signEs256(signer, Buffer.from(input, "utf8")) };
  };
  try {
    const enrollBody = async (over: { presenceSigner?: any; nonce?: string } = {}) => {
      const nonce = over.nonce ?? ((await (await post("/devices/challenge", "Bearer login-alice")).json()) as any).nonce;
      const payload = { kid: "new-approval", presence_kid: "new-presence", presence_jwk_thumbprint: thumb(keys.new_presence), nonce, iat: clock };
      const a = jws("biatec-device-enroll+jwt", keys.new_approval, payload);
      const signer = over.presenceSigner ?? keys.new_presence;
      const p = jws("biatec-device-enroll-presence+jwt", signer, payload, pub(keys.new_presence));
      return { kid: "new-approval", public_key: pub(keys.new_approval), presence_kid: "new-presence", presence_public_key: pub(keys.new_presence), approval_proof: a.input + "." + a.sig, presence_proof: p.input + "." + p.sig };
    };
    assert.equal((await post("/devices", "Bearer login-alice", { kid: "x1", public_key: pub(keys.new_approval) })).status, 400, "a single key is refused");
    assert.equal((await post("/devices", "Bearer login-alice", await enrollBody({ presenceSigner: keys.device }))).status, 400, "the presence proof must be signed by the presence key");
    assert.equal((await post("/devices", "Bearer login-alice", await enrollBody({ nonce: "A".repeat(32) }))).status, 400, "an unknown nonce");
    const ok = await post("/devices", "Bearer login-alice", await enrollBody());
    assert.equal(ok.status, 201, await ok.clone().text());
    const enrolled = (await ok.json()) as any;
    assert.ok(enrolled.presence_attestation, "the presence key gets its own attestation");

    // The device proof: the presence key's, never the approval key's.
    const token = enrolled.device_token as string;
    const proof = (signer: any, kid: string) => {
      const payload = { htm: "GET", htu: `${b}/devices/requests`, iat: clock, jti: randomBytes(12).toString("hex"), ath: createHash("sha256").update(token).digest("base64url") };
      const input = Buffer.from(JSON.stringify({ alg: "ES256", typ: DEVICE_PROOF_TYP, kid })).toString("base64url") + "." + Buffer.from(JSON.stringify(payload)).toString("base64url");
      return input + "." + signEs256(signer, Buffer.from(input, "utf8"));
    };
    const get = (p: string) => fetch(`${b}/devices/requests`, { headers: { Authorization: "Device " + token, "X-Device-Proof": p } });
    assert.equal((await get(proof(keys.new_presence, "new-presence"))).status, 200);
    assert.equal((await get(proof(keys.new_approval, "new-approval"))).status, 401, "a proof signed by the approval key");
    assert.equal((await get(proof(keys.new_approval, "new-presence"))).status, 401, "the approval key under the presence kid");
  } finally {
    strict.server.close();
  }
});
