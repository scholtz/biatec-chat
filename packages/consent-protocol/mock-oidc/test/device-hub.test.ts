import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { createHash, generateKeyPairSync, randomBytes } from "node:crypto";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { AddressInfo } from "node:net";
import WebSocket from "ws";
import { jcs, signEs256 } from "../../scripts/lib.mjs";
import { DEVICE_PROOF_TYP, createMockOidc } from "../src/app.ts";
import { account, buildGroup, usdcTransfer } from "../src/build.ts";

const RS = "\u001e";
const keys = JSON.parse(readFileSync(join(dirname(fileURLToPath(import.meta.url)), "..", "..", "vectors", "keys.json"), "utf8"));
let clock = 1_792_425_600;
// `mock` has a short lifetime and a short ping interval and wants the device proof; `long` keeps the defaults (a 30 minute lifetime), with pings off.
const mock = createMockOidc({ now: () => clock, testHelpers: true, deviceProof: true, maxRequestsPerHour: 1_000_000, hubLifetimeSeconds: 1.5, hubPingSeconds: 0.4 });
const long = createMockOidc({ now: () => clock, testHelpers: true, maxRequestsPerHour: 1_000_000, hubPingSeconds: 3600 });
let base = "";
let longBase = "";
before(async () => {
  await Promise.all([mock, long].map((m) => new Promise<void>((r) => m.server.listen(0, "127.0.0.1", r))));
  base = `http://127.0.0.1:${(mock.server.address() as AddressInfo).port}`;
  longBase = `http://127.0.0.1:${(long.server.address() as AddressInfo).port}`;
});
after(() => {
  mock.server.close();
  long.server.close();
});

const call = async (b: string, method: string, path: string, headers: Record<string, string> = {}, body?: unknown) => {
  const res = await fetch(b + path, { method, headers: { ...headers, ...(body ? { "Content-Type": "application/json" } : {}) }, body: body ? JSON.stringify(body) : undefined });
  return { status: res.status, json: (await res.json()) as any };
};
const mockDevice = async (b: string, user: string) => (await call(b, "GET", `/mock/devices/${user}-device`)).json;

/** The device auth headers; a proof as well on the mock that wants one. */
async function deviceHeaders(b: string, user: string, method: string, path: string) {
  const d = await mockDevice(b, user);
  const headers: Record<string, string> = { Authorization: "Device " + d.device_token };
  if (b === base) {
    const header = { alg: "ES256", typ: DEVICE_PROOF_TYP, kid: d.kid };
    const payload = { htm: method, htu: b + path, iat: clock, jti: randomBytes(12).toString("hex"), ath: createHash("sha256").update(d.device_token).digest("base64url") };
    const input = Buffer.from(JSON.stringify(header)).toString("base64url") + "." + Buffer.from(JSON.stringify(payload)).toString("base64url");
    headers["X-Device-Proof"] = input + "." + signEs256(keys.device, Buffer.from(input, "utf8"));
  }
  return { headers, kid: d.kid as string };
}
const ticket = async (b: string, user = "alice") => (await call(b, "POST", "/devices/hub-ticket", (await deviceHeaders(b, user, "POST", "/devices/hub-ticket")).headers)).json;

/** A hub client: keeps the raw frames, and the messages in them. */
async function connect(b: string, t: string, handshake = true) {
  const ws = new WebSocket(b.replace("http", "ws") + "/hubs/device?access_token=" + encodeURIComponent(t));
  const frames: string[] = [];
  const messages: any[] = [];
  const waiters: (() => void)[] = [];
  ws.on("message", (d) => {
    const f = d.toString();
    frames.push(f);
    for (const part of f.split(RS)) if (part) messages.push(JSON.parse(part));
    waiters.splice(0).forEach((w) => w());
  });
  await new Promise<void>((res, rej) => (ws.once("open", res), ws.once("error", rej)));
  const closed = new Promise<number>((r) => ws.once("close", r));
  const next = async (pred: (m: any) => boolean = () => true, ms = 3000) => {
    const deadline = Date.now() + ms;
    for (;;) {
      const i = messages.findIndex(pred);
      if (i >= 0) return messages.splice(i, 1)[0];
      if (Date.now() > deadline) throw new Error("timed out; got " + JSON.stringify(messages));
      await new Promise<void>((r) => (waiters.push(r), setTimeout(r, 50)));
    }
  };
  if (handshake) {
    ws.send(JSON.stringify({ protocol: "json", version: 1 }) + RS);
    assert.deepEqual(await next(), {});
    assert.equal(frames[0], "{}" + RS, "the handshake answer is {} and the record separator");
  }
  return { ws, frames, messages, next, closed };
}

/** Opens a consent request for alice (a step-up for a large transfer, as the other tests do). */
async function openRequest(b: string) {
  const g = buildGroup([usdcTransfer(account("alice"), account("payee"), 5000)]).items;
  const res = await call(b, "POST", `/wallet/algorand-testnet/${account("alice")}/sign`, { Authorization: "Bearer alice-chat-sign" }, { transactions: g, origin: "https://hub-" + randomBytes(4).toString("hex") + ".example" });
  assert.equal(res.status, 401, JSON.stringify(res.json));
  return res.json.auth_req_id as string;
}

test("hub ticket: single use, 30 seconds, and an upgrade without a good ticket is refused with 401", async () => {
  const t = await ticket(longBase);
  assert.equal(t.expires_in, 30);
  assert.equal(typeof t.ticket, "string");
  const c = await connect(longBase, t.ticket);
  c.ws.close();
  await assert.rejects(connect(longBase, t.ticket), /401/, "a used ticket is refused");
  await assert.rejects(connect(longBase, "nope"), /401/, "an unknown ticket is refused");
  const t2 = await ticket(longBase);
  clock += 31;
  try {
    await assert.rejects(connect(longBase, t2.ticket), /401/, "an expired ticket is refused");
  } finally {
    clock -= 31;
  }
  assert.equal((await call(longBase, "POST", "/devices/hub-ticket")).status, 401, "device auth is needed");
  const d = await mockDevice(base, "alice");
  assert.equal((await call(base, "POST", "/devices/hub-ticket", { Authorization: "Device " + d.device_token })).status, 401, "the device proof is needed where the mock asks for it");
});

test("hub: ping is answered, and consent_request / consent_closed arrive after the mock's state changes", async () => {
  const c = await connect(longBase, (await ticket(longBase)).ticket);
  c.ws.send(JSON.stringify({ type: 6 }) + RS);
  assert.deepEqual(await c.next(), { type: 6 }, "a ping is answered with a ping");

  const id = await openRequest(longBase);
  assert.deepEqual(await c.next((x) => x.type === 1), { type: 1, target: "consent_request", arguments: [{ auth_req_id: id }] });
  const frame = c.frames.at(-1)!;
  assert.equal(frame.indexOf(RS), frame.length - 1, "one message, one record separator, at the end of the frame");

  // Denying it closes it.
  const dev = await mockDevice(longBase, "alice");
  const auth = { Authorization: "Device " + dev.device_token };
  const req = (await call(longBase, "GET", `/consent/requests/${id}`, auth)).json;
  const signed = { authorization_details: req.authorization_details, auth_req_id: id, client_fingerprint: req.client.fingerprint, binding_code: "000000", decision: "deny", expiry: req.expiry, sub: req.sub, binding_message: req.binding_message };
  const receipt = { version: "1.1", signed, alg: "ES256", signature: signEs256(keys.device, Buffer.from(jcs(signed), "utf8")), issued_at: clock, device: { kid: dev.kid, key_use: "approve", public_key: dev.public_key, attestation: dev.attestation } };
  assert.equal((await call(longBase, "POST", `/consent/requests/${id}/receipt`, auth, receipt)).status, 200);
  assert.deepEqual(await c.next((x) => x.type === 1), { type: 1, target: "consent_closed", arguments: [{ auth_req_id: id }] });
  c.ws.close();
});

test("hub: a request that runs out is announced, enrollment and activation send device_enrolled", async () => {
  const c = await connect(longBase, (await ticket(longBase)).ticket);
  const id = await openRequest(longBase);
  assert.equal((await c.next((x) => x.type === 1)).target, "consent_request");
  clock += 400;
  try {
    assert.deepEqual(await c.next((x) => x.type === 1, 4000), { type: 1, target: "consent_closed", arguments: [{ auth_req_id: id }] });
  } finally {
    clock -= 400;
  }
  const fresh = generateKeyPairSync("ec", { namedCurve: "P-256" }).publicKey.export({ format: "jwk" }) as Record<string, string>;
  assert.equal((await call(longBase, "POST", "/devices", { Authorization: "Bearer login-alice" }, { kid: "alice-hub-phone", public_key: fresh })).status, 201);
  assert.deepEqual(await c.next((x) => x.type === 1), { type: 1, target: "device_enrolled", arguments: [{ kid: "alice-hub-phone" }] });
  await call(longBase, "POST", "/mock/devices/alice-hub-phone/activate");
  assert.deepEqual(await c.next((x) => x.type === 1), { type: 1, target: "device_enrolled", arguments: [{ kid: "alice-hub-phone" }] });
  c.ws.close();
});

test("hub: settings_changed arrives after a settings change", async () => {
  const c = await connect(longBase, (await ticket(longBase)).ticket);
  const dev = await mockDevice(longBase, "alice");
  // Tightening needs no signed consent.
  const r = await call(longBase, "PUT", "/devices/settings", { Authorization: "Device " + dev.device_token }, { change: { block_unverified_clients: true } });
  assert.equal(r.status, 200, JSON.stringify(r.json));
  assert.deepEqual(await c.next((x) => x.type === 1), { type: 1, target: "settings_changed", arguments: [{}] });
  c.ws.close();
});

test("hub: only the user's own devices hear their events", async () => {
  const c = await connect(longBase, (await ticket(longBase, "carol")).ticket);
  await openRequest(longBase); // alice's
  await new Promise((r) => setTimeout(r, 200));
  assert.equal(c.messages.length, 0);
  c.ws.close();
});

test("hub: the server pings on its own, and closes after its lifetime with allowReconnect true", async () => {
  const c = await connect(base, (await ticket(base)).ticket);
  assert.deepEqual(await c.next((x) => x.type === 6), { type: 6 });
  assert.deepEqual(await c.next((x) => x.type === 7, 4000), { type: 7, allowReconnect: true });
  await c.closed;
});

test("hub: closing a device's connections can say allowReconnect false", async () => {
  const c = await connect(longBase, (await ticket(longBase)).ticket);
  const dev = await mockDevice(longBase, "alice");
  await call(longBase, "POST", "/mock/hub/close", {}, { kid: dev.kid, allow_reconnect: false });
  assert.deepEqual(await c.next((x) => x.type === 7), { type: 7, allowReconnect: false });
  await c.closed;
});

test("hub: a wrong handshake is answered with an error and the connection closes", async () => {
  const c = await connect(longBase, (await ticket(longBase)).ticket, false);
  c.ws.send(JSON.stringify({ protocol: "messagepack", version: 1 }) + RS);
  assert.match((await c.next()).error, /json/);
  await c.closed;
});

test("push: fcm is refused with unsupported_push_provider, apns is accepted and stored", async () => {
  const dev = await deviceHeaders(longBase, "alice", "PUT", "/devices/push");
  const fcm = await call(longBase, "PUT", "/devices/push", dev.headers, { provider: "fcm", token: "fcm-1" });
  assert.equal(fcm.status, 400);
  assert.equal(fcm.json.error, "unsupported_push_provider");
  const apns = await call(longBase, "PUT", "/devices/push", dev.headers, { provider: "apns", token: "abcdef0123" });
  assert.equal(apns.status, 200);
  assert.deepEqual(apns.json, { kid: dev.kid, provider: "apns" });
  assert.equal((await call(longBase, "PUT", "/devices/push", dev.headers, { provider: "apns" })).status, 400);
  const id = await openRequest(longBase);
  const outbox = (await call(longBase, "GET", `/mock/push-outbox?kid=${dev.kid}`)).json.messages;
  assert.deepEqual(outbox.at(-1), { kid: dev.kid, provider: "apns", token: "abcdef0123", data: { auth_req_id: id } });
});
