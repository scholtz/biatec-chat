// T-242: the in-app login (authorization code with PKCE), the `enroll-device` scope and `auth_time` check, activation of a new device by an
// existing one (`POST /devices/{kid}/activate`, a signed `biatec_device_approval`) and by a recovery code.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createHash, generateKeyPairSync, randomBytes } from "node:crypto";
import type { AddressInfo } from "node:net";
import { jcs, pairingCode, signEs256 } from "../../scripts/lib.mjs";
import { AUTHENTICATOR_REDIRECT_URIS, LOGIN_MAX_AGE_SECONDS, RECOVERY_CODE_MAX_FAILURES, RECOVERY_LOCKOUT_SECONDS, createMockOidc } from "../src/app.ts";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const keys = JSON.parse(readFileSync(join(root, "vectors", "keys.json"), "utf8"));

let clock = 1_792_425_600;
const mock = createMockOidc({ now: () => clock, testHelpers: true, twoKeys: true });
let base = "";
before(async () => {
  await new Promise<void>((r) => mock.server.listen(0, "127.0.0.1", r));
  base = `http://127.0.0.1:${(mock.server.address() as AddressInfo).port}`;
});
after(() => mock.server.close());

async function call(method: string, path: string, opts: { auth?: string; body?: unknown } = {}) {
  const headers: Record<string, string> = {};
  if (opts.auth) headers.Authorization = opts.auth;
  if (opts.body !== undefined) headers["Content-Type"] = "application/json";
  const res = await fetch(base + path, { method, headers, body: opts.body === undefined ? undefined : JSON.stringify(opts.body), redirect: "manual" });
  const text = await res.text();
  let json: any = {};
  try { json = JSON.parse(text); } catch { /* not JSON */ }
  return { status: res.status, json, headers: res.headers };
}

const pub = (k: any) => ({ kty: "EC", crv: "P-256", x: k.x, y: k.y });
const thumb = (k: any) => createHash("sha256").update(JSON.stringify({ crv: "P-256", kty: "EC", x: k.x, y: k.y })).digest("base64url");
const newKey = () => generateKeyPairSync("ec", { namedCurve: "P-256" }).privateKey.export({ format: "jwk" }) as Record<string, string>;
const jws = (typ: string, signer: any, payload: object) => {
  const input = Buffer.from(JSON.stringify({ alg: "ES256", typ, jwk: pub(signer) })).toString("base64url") + "." + Buffer.from(JSON.stringify(payload)).toString("base64url");
  return input + "." + signEs256(signer, Buffer.from(input, "utf8"));
};

/** A new device with two fresh keys, enrolled with the login token `login` (and the optional extra body members). */
async function enroll(login: string, extra: Record<string, unknown> = {}) {
  const approval = newKey();
  const presence = newKey();
  const kid = "new-" + randomBytes(6).toString("hex");
  const presenceKid = kid + "-presence";
  const challenge = await call("POST", "/devices/challenge", { auth: "Bearer " + login });
  if (challenge.status !== 200) return { res: challenge, kid, approval, presence, nonce: "" };
  const nonce = challenge.json.nonce as string;
  const payload = { kid, presence_kid: presenceKid, presence_jwk_thumbprint: thumb(presence), nonce, iat: clock };
  const res = await call("POST", "/devices", {
    auth: "Bearer " + login,
    body: {
      kid, public_key: pub(approval), presence_kid: presenceKid, presence_public_key: pub(presence), name: "Test phone",
      approval_proof: jws("biatec-device-enroll+jwt", approval, payload), presence_proof: jws("biatec-device-enroll-presence+jwt", presence, payload), ...extra,
    },
  });
  return { res, kid, approval, presence, nonce };
}

/** The approval statement an active seeded device signs (its approval key is the vector `device` key). */
async function approvalFor(approver: string, over: Record<string, unknown> = {}, signer: any = keys.device, keyUse = "approve") {
  const info = (await call("GET", `/mock/devices/${approver}`)).json;
  const target = over.target as { kid: string; approval: any; presence: any; nonce: string };
  const signed = {
    type: "biatec_device_approval", approver_kid: approver, new_kid: target.kid, new_approval_jwk_thumbprint: thumb(target.approval), new_presence_jwk_thumbprint: thumb(target.presence),
    pairing_code: pairingCode(thumb(target.approval), thumb(target.presence), target.nonce), nonce: target.nonce, issued_at: clock, ...(over.signed as object ?? {}),
  };
  return {
    signed, alg: "ES256", signature: signEs256(signer, Buffer.from(jcs(signed), "utf8")),
    device: { kid: approver, key_use: keyUse, public_key: pub(keys.device), attestation: info.attestation },
  };
}

test("login: the authorization code flow with PKCE gives a short-lived token with scope enroll-device and no refresh token", async () => {
  const verifier = randomBytes(32).toString("base64url");
  const challenge = createHash("sha256").update(verifier).digest("base64url");
  const redirect = AUTHENTICATOR_REDIRECT_URIS[1];
  const q = new URLSearchParams({ response_type: "code", client_id: "biatec-authenticator", redirect_uri: redirect, scope: "openid enroll-device", state: "st1", code_challenge: challenge, code_challenge_method: "S256", prompt: "login", max_age: "0" });
  const auth = await call("GET", "/authorize?" + q);
  assert.equal(auth.status, 302);
  const back = new URL(auth.headers.get("location")!);
  assert.equal(back.searchParams.get("state"), "st1");
  const code = back.searchParams.get("code")!;
  const form = (c: string, v: string) => fetch(base + "/token", { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" }, body: new URLSearchParams({ grant_type: "authorization_code", client_id: "biatec-authenticator", code: c, redirect_uri: redirect, code_verifier: v }) });
  const wrong = await form(code, randomBytes(32).toString("base64url"));
  assert.equal(wrong.status, 400, "a wrong verifier");
  assert.equal((await form(code, verifier)).status, 400, "the code is gone after one try");
  const again = await call("GET", "/authorize?" + q);
  const ok = await form(new URL(again.headers.get("location")!).searchParams.get("code")!, verifier);
  assert.equal(ok.status, 200);
  const token = (await ok.json()) as any;
  assert.equal(token.scope, "openid enroll-device");
  assert.equal("refresh_token" in token, false);
  assert.equal((await call("POST", "/devices/challenge", { auth: "Bearer " + token.access_token })).status, 200);
  // Another redirect URI is refused.
  assert.equal((await call("GET", "/authorize?" + new URLSearchParams({ ...Object.fromEntries(q), redirect_uri: "https://evil.example/cb" }))).status, 400);
  // Without the enroll-device scope the token cannot enroll.
  const lessQ = new URLSearchParams({ ...Object.fromEntries(q), scope: "openid" });
  const less = await call("GET", "/authorize?" + lessQ);
  const t2 = (await (await form(new URL(less.headers.get("location")!).searchParams.get("code")!, verifier)).json()) as any;
  assert.equal(t2.scope, "openid");
  assert.equal((await call("POST", "/devices/challenge", { auth: "Bearer " + t2.access_token })).status, 403);
  // A login older than the window is refused with max_age (the app restarts the login once).
  clock += LOGIN_MAX_AGE_SECONDS + 1;
  try {
    const old = await call("POST", "/devices/challenge", { auth: "Bearer " + token.access_token });
    assert.equal(old.status, 401);
  } finally {
    clock -= LOGIN_MAX_AGE_SECONDS + 1;
  }
});

test("enrollment: a stale login is refused with insufficient_user_authentication and max_age; the seeded login tokens carry the scope", async () => {
  const stale = await call("POST", "/devices/challenge", { auth: "Bearer login-alice-stale" });
  assert.equal(stale.status, 401);
  assert.equal(stale.json.error, "insufficient_user_authentication");
  assert.equal(stale.json.max_age, 0);
  assert.match(stale.headers.get("www-authenticate") ?? "", /insufficient_user_authentication/);
  const e = await enroll("login-alice-stale");
  assert.equal(e.res.status, 401);
  assert.equal((await enroll("login-alice")).res.status, 201);
});

test("activation: an active device signs the approval with its approval key and the new device becomes active; the event and push message follow", async () => {
  assert.equal((await call("PUT", "/devices/push", { auth: "Device device-token-alice", body: { provider: "apns", token: "apns-alice-act" } })).status, 200);
  const t = await enroll("login-alice");
  assert.equal(t.res.status, 201, JSON.stringify(t.res.json));
  assert.equal(t.res.json.active, false);
  const list = (await call("GET", "/devices", { auth: "Device device-token-alice" })).json.devices as any[];
  const pending = list.find((d) => d.kid === t.kid);
  assert.equal(pending.name, "Test phone");
  assert.equal(pending.pairing.pairing_code, pairingCode(thumb(t.approval), thumb(t.presence), t.nonce));
  assert.equal(pending.pairing.nonce, t.nonce);
  const outbox = (await call("GET", "/mock/push-outbox?kid=alice-device")).json.messages as any[];
  assert.ok(outbox.some((m) => m.data.event === "device_enrolled" && m.data.kid === t.kid), "a device_enrolled push entry for the active device");

  const approval = await approvalFor("alice-device", { target: t });
  const ok = await call("POST", `/devices/${t.kid}/activate`, { auth: "Device device-token-alice", body: { approval } });
  assert.equal(ok.status, 200, JSON.stringify(ok.json));
  const after = (await call("GET", "/devices", { auth: "Device device-token-alice" })).json.devices as any[];
  assert.equal(after.find((d) => d.kid === t.kid).active, true);
  assert.equal("pairing" in after.find((d) => d.kid === t.kid), false);
  const again = await call("POST", `/devices/${t.kid}/activate`, { auth: "Device device-token-alice", body: { approval } });
  assert.equal(again.status, 409, "already active");
});

test("activation: a presence-key signature, a wrong pairing code, a replayed nonce, a stale statement and another account's approver are refused", async () => {
  const t = await enroll("login-alice");
  const go = (approval: unknown, auth = "Device device-token-alice") => call("POST", `/devices/${t.kid}/activate`, { auth, body: { approval } });
  // Signed by the presence key (its attestation says `presence`): refused whichever way it is dressed.
  const presenceInfo = (await call("GET", "/mock/devices/alice-device")).json.presence;
  const bySame = await approvalFor("alice-device", { target: t }, keys.presence, "presence");
  assert.equal((await go(bySame)).status, 403, "the presence key's signature");
  const asPresenceKid = { ...(await approvalFor("alice-device", { target: t, signed: { approver_kid: "alice-device-presence" } }, keys.presence, "presence")), device: { kid: "alice-device-presence", key_use: "presence", public_key: pub(keys.presence), attestation: presenceInfo.attestation } };
  assert.equal((await go(asPresenceKid)).status, 403, "the presence key as the approver");
  assert.equal((await go(await approvalFor("alice-device", { target: t, signed: { pairing_code: "000000" } }))).status, 403, "a wrong pairing code");
  assert.equal((await go(await approvalFor("alice-device", { target: t, signed: { issued_at: clock - 1000 } }))).status, 403, "a stale statement");
  assert.equal((await go(await approvalFor("alice-device", { target: t, signed: { nonce: "A".repeat(32) } }))).status, 403, "another nonce");
  // Another account's active device cannot approve (carol's device, alice's pending one).
  const carol = await go(await approvalFor("carol-device", { target: t }), "Device device-token-carol");
  assert.equal(carol.status, 404);
  // A pending device cannot approve anything.
  const other = await enroll("login-alice");
  const byPending = await call("POST", `/devices/${t.kid}/activate`, { auth: "Device " + other.res.json.device_token, body: { approval: await approvalFor("alice-device", { target: t }) } });
  assert.equal(byPending.status, 403);
  // And the real one still works: nothing above spent the nonce.
  assert.equal((await go(await approvalFor("alice-device", { target: t }))).status, 200);
  // The statement nonce is single use for the approver.
  const t2 = await enroll("login-alice");
  const a2 = await approvalFor("alice-device", { target: t2 });
  assert.equal((await call("POST", `/devices/${t2.kid}/activate`, { auth: "Device device-token-alice", body: { approval: a2 } })).status, 200);
});

test("recovery code: activates a new device at once, once; a wrong code is refused and repeated wrong codes lock activation (429)", async () => {
  const code = async (who: string) => {
    const r = await call("POST", "/devices/recovery-code", { auth: "Device device-token-" + who });
    await call("POST", "/devices/recovery-code/ack", { auth: "Device device-token-" + who });
    return r.json.recovery_code as string;
  };
  const alice = await code("alice");
  assert.match(alice, /^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$/);
  const wrong = await enroll("login-alice", { activation: { kind: "recovery_code", code: "0000-0000-0000" } });
  assert.equal(wrong.res.status, 403);
  assert.equal(wrong.res.json.error, "invalid_recovery_code");
  const good = await enroll("login-alice", { activation: { kind: "recovery_code", code: alice } });
  assert.equal(good.res.status, 201, JSON.stringify(good.res.json));
  assert.equal(good.res.json.active, true);
  assert.equal((await enroll("login-alice", { activation: { kind: "recovery_code", code: alice } })).res.status, 403, "a recovery code is used once");
  assert.equal((await enroll("login-alice", { activation: { kind: "other", code: alice } })).res.status, 400);

  // Lockout, on carol's account.
  const carol = await code("carol");
  let last = await enroll("login-carol", { activation: { kind: "recovery_code", code: "1111-1111-1111" } });
  for (let i = 1; i < RECOVERY_CODE_MAX_FAILURES; i++) last = await enroll("login-carol", { activation: { kind: "recovery_code", code: "1111-1111-1111" } });
  assert.equal(last.res.status, 403, "the failure that reaches the limit is still a wrong code");
  const locked = await enroll("login-carol", { activation: { kind: "recovery_code", code: carol } });
  assert.equal(locked.res.status, 429, "the right code is refused while locked");
  assert.ok(Number(locked.res.headers.get("retry-after")) > 0);
  clock += RECOVERY_LOCKOUT_SECONDS + 1;
  try {
    assert.equal((await enroll("login-carol", { activation: { kind: "recovery_code", code: carol } })).res.status, 201);
  } finally {
    clock -= RECOVERY_LOCKOUT_SECONDS + 1;
  }
});
