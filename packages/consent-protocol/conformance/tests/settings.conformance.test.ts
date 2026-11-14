// The settings of Biatec OIDC issue 30 (T-245): the user's own threshold and trust, set on the phone. Every case runs as the settings account
// (an account of its own, see README), changes what it needs and puts it back. A loosening is a `biatec_settings_change` signed with the
// approval key; whether a change loosens is the server's decision, so the suite only sends the change and reads the answer.
import { describe, it, expect, inject } from "vitest";
import { jcs, signEs256 } from "../../scripts/lib.mjs";
import { buildGroup, buildReceipt, config, approve, fetchRequest, http, needs, signCall, transfer } from "../src/kit.ts";
import type { Need } from "../src/needs.ts";

const base = inject("baseUrl");
const who = config.settings.device;
const auth = "Device " + who.deviceToken;
const bearer = config.settings.bearer;
// The flow of a case: the settings account, its device key (the vector device key) and the mock's device lookup (attestation).
const flow: Need[] = ["settingsUser", "deviceKey", "mockHelpers"];

let nonceSeq = 0;
const nonce = () => `conformance-${Date.now().toString(36)}-${++nonceSeq}-${"0".repeat(8)}`;
const view = async () => (await http(base, "GET", "/devices/settings", { auth })).json;
const clientView = async () => (await view()).clients.find((c: any) => c.client_id === config.clientId);

/** What the authenticator sends to loosen something: the change, signed with the approval key, with the key's attestation. */
async function consentFor(change: unknown, over: Record<string, unknown> = {}, sub?: string) {
  const info = (await http(base, "GET", `/mock/devices/${who.deviceKid}`)).json;
  const signed = { type: "biatec_settings_change", sub: sub ?? (await view()).sub, kid: who.deviceKid, nonce: nonce(), issued_at: Math.floor(Date.now() / 1000), change, ...over };
  return {
    signed, alg: "ES256", signature: signEs256(config.devicePrivateJwk!, Buffer.from(jcs(signed), "utf8")),
    device: { kid: who.deviceKid, key_use: "approve", public_key: info.public_key, attestation: info.attestation },
  };
}
/** `PUT /devices/settings`; `signed` adds the consent of the approval key. */
const put = async (change: unknown, signed = false) => http(base, "PUT", "/devices/settings", { auth, body: { change, ...(signed ? { consent: await consentFor(change) } : {}) } });
const clientChange = (c: Record<string, unknown>) => ({ clients: { [config.clientId]: c } });
const app = (id: number) => ({ network: config.network, app_id: id });
const withApp = (id: number, note: string) => buildGroup([transfer(10, { note }), { sender: config.address, appId: id }]);

/** Links the client again after an unlink: the next request is first use and is linked by the approval. */
async function relink() {
  const first = await signCall(base, bearer, buildGroup([transfer(5, { note: "relink-" + nonce() })]));
  if (first.status !== 401) return;
  await approve(base, first.json.auth_req_id, first.json.binding_code, { who });
}

describe("settings: loosening vs tightening is the server's decision", () => {
  it("tightening applies without a signature; loosening without one is 403 insufficient_user_authentication / consent_required and changes nothing", needs(flow, async () => {
    const tighter = await put({ threshold_eur: "500" });
    expect([tighter.status, tighter.json?.applied, tighter.json?.loosened]).toEqual([200, true, false]);
    expect((await view()).threshold_eur).toMatch(/^500(\.00)?$/);
    try {
      const unsigned = await put({ threshold_eur: "800" });
      expect(unsigned.status).toBe(403);
      expect(unsigned.json).toMatchObject({ error: "insufficient_user_authentication", acr_values: "biatec:consent", reason: "consent_required" });
      expect((await view()).threshold_eur).toMatch(/^500(\.00)?$/);
      const signed = await put({ threshold_eur: "800" }, true);
      expect([signed.status, signed.json?.applied, signed.json?.loosened]).toEqual([200, true, true]);
      expect((await view()).threshold_eur).toMatch(/^800(\.00)?$/);
    } finally {
      expect((await put({ threshold_eur: "1000" }, true)).status).toBe(200);
    }
  }));

  it("a signature is refused when it is for another change, another account, by the presence key, stale or replayed", needs(flow, async () => {
    const change = { threshold_eur: "1500" };
    const send = (consent: unknown) => http(base, "PUT", "/devices/settings", { auth, body: { change, consent } });
    const refused = async (consent: unknown) => {
      const r = await send(consent);
      expect([r.status, r.json?.reason]).toEqual([403, "consent_required"]);
    };
    await refused(await consentFor({ threshold_eur: "1600" }));
    await refused(await consentFor(change, {}, "pw_somebody_else"));
    await refused(await consentFor(change, { issued_at: Math.floor(Date.now() / 1000) - 400 }));
    await refused(await consentFor(change, { nonce: "short" }));
    await refused(await consentFor(change, { extra: true }));
    const info = (await http(base, "GET", `/mock/devices/${who.deviceKid}`)).json;
    if (info.presence) {
      const signed = { type: "biatec_settings_change", sub: (await view()).sub, kid: info.presence.kid, nonce: nonce(), issued_at: Math.floor(Date.now() / 1000), change };
      const viaPresence = { signed, alg: "ES256", signature: signEs256(config.devicePrivateJwk!, Buffer.from(jcs(signed), "utf8")), device: { kid: info.presence.kid, key_use: "presence", public_key: info.presence.public_key, attestation: info.presence.attestation } };
      await refused(viaPresence);
    }
    expect((await view()).threshold_eur).toMatch(/^1000(\.00)?$/);
    const good = await consentFor(change);
    expect((await send(good)).status).toBe(200);
    try {
      await put({ threshold_eur: "1000" });
      await refused(good); // the nonce is spent
    } finally {
      expect((await put({ threshold_eur: "1000" })).status).toBe(200);
    }
  }));
});

describe("settings: the user's own threshold", () => {
  it("has no operator ceiling by default: 250000 EUR is accepted and a threshold of 0 asks for everything", needs(flow, async () => {
    try {
      expect((await put({ threshold_eur: "250000" }, true)).status).toBe(200);
      expect((await view()).threshold_eur).toMatch(/^250000(\.00)?$/);
      expect((await put({ threshold_eur: "0" })).status).toBe(200);
      expect((await signCall(base, bearer, buildGroup([transfer(1, { note: "zero" })]))).status).toBe(401);
    } finally {
      expect((await put({ threshold_eur: "1000" }, true)).status).toBe(200);
    }
  }));

  it("a threshold of 5000 EUR signs a 2000 EUR transfer without a request, the default asks", needs(flow, async () => {
    const group = buildGroup([transfer(2200, { note: "threshold-2000" })]); // about 2029 EUR in the mock's price table
    expect((await signCall(base, bearer, group)).status).toBe(401);
    try {
      expect((await put({ threshold_eur: "5000" }, true)).status).toBe(200);
      const r = await signCall(base, bearer, group);
      expect(r.status, JSON.stringify(r.json)).toBe(200);
    } finally {
      expect((await put({ threshold_eur: "1000" }, true)).status).toBe(200);
    }
  }));

  it("rejects an amount with more than two decimals and one that is not a decimal string with 400", needs(flow, async () => {
    for (const threshold_eur of ["10.123", "-1", "ten", 5]) expect((await put({ threshold_eur }, true)).status, String(threshold_eur)).toBe(400);
    expect((await put({})).status).toBe(400);
    expect((await put({ block_unverified_clients: true })).status, "the old name").toBe(400);
  }));
});

describe("settings: ask_on_test_networks and block_unproven_clients", () => {
  it("turning ask_on_test_networks off is loosening and stops asking about value on a test network", needs(flow, async () => {
    const group = buildGroup([transfer(5000, { note: "testnet-value" })]);
    expect((await signCall(base, bearer, group)).status).toBe(401);
    try {
      expect((await put({ ask_on_test_networks: false })).status).toBe(403);
      expect((await put({ ask_on_test_networks: false }, true)).status).toBe(200);
      expect((await view()).ask_on_test_networks).toBe(false);
      expect((await signCall(base, bearer, group, {}, "algorand-testnet")).status).toBe(200);
    } finally {
      expect((await put({ ask_on_test_networks: true })).status, "on is tightening").toBe(200);
    }
    expect((await signCall(base, bearer, group)).status).toBe(401);
  }));

  it("block_unproven_clients: on is tightening, off is loosening", needs(flow, async () => {
    try {
      expect((await put({ block_unproven_clients: true })).status).toBe(200);
      expect((await view()).block_unproven_clients).toBe(true);
      expect((await put({ block_unproven_clients: false })).status).toBe(403);
    } finally {
      expect((await put({ block_unproven_clients: false }, true)).status).toBe(200);
    }
  }));
});

describe("settings: label, trusted applications, unlink", () => {
  it("a label is the user's own display-safe text; tightening; null in the view until set", needs(flow, async () => {
    try {
      const set = await put(clientChange({ label: "My assistant" }));
      expect([set.status, set.json?.loosened]).toEqual([200, false]);
      expect((await clientView()).label).toBe("My assistant");
      for (const label of ["", " padded ", "a‮b", "zero​width", "two\nlines", "x".repeat(41), 5, null]) {
        expect((await put(clientChange({ label }))).status, JSON.stringify(label)).toBe(400);
      }
      expect((await put(clientChange({ label: "x".repeat(40) }))).status).toBe(200);
      const c = await clientView();
      for (const forbidden of ["verified", "display_name", "entity_id"]) expect(c, forbidden).not.toHaveProperty(forbidden);
    } finally {
      await put(clientChange({ unlink: true }));
      await relink();
    }
    expect((await clientView()).label ?? null).toBeNull();
  }));

  it("adding a trusted application is loosening; the policy then uses it; removing is tightening", needs(flow, async () => {
    const id = 4_242_001;
    expect((await signCall(base, bearer, withApp(id, "a"))).status, "an untrusted application: the value is unknown").toBe(401);
    try {
      const add = clientChange({ trusted_applications_add: [app(id)] });
      expect((await put(add)).status).toBe(403);
      expect((await put(add, true)).status).toBe(200);
      expect((await clientView()).trusted_applications).toContainEqual(app(id));
      const signed = await signCall(base, bearer, withApp(id, "b"));
      expect(signed.status, JSON.stringify(signed.json)).toBe(200);
    } finally {
      const removed = await put(clientChange({ trusted_applications_remove: [app(id)] }));
      expect([removed.status, removed.json?.loosened]).toEqual([200, false]);
    }
    expect((await clientView()).trusted_applications).not.toContainEqual(app(id));
    expect((await signCall(base, bearer, withApp(id, "c"))).status).toBe(401);
    for (const list of [[], [app(1), app(1)], [{ network: "bitcoin-mainnet", app_id: 1 }], Array.from({ length: 17 }, (_, i) => app(i + 1))]) {
      expect((await put(clientChange({ trusted_applications_add: list }), true)).status, JSON.stringify(list).slice(0, 50)).toBe(400);
    }
  }));

  it("unlink is tightening and forgets what the user held: the next request is first use again", needs(flow, async () => {
    const id = 4_242_002;
    try {
      expect((await put(clientChange({ trusted_applications_add: [app(id)] }), true)).status).toBe(200);
      const unlink = await put(clientChange({ unlink: true }));
      expect([unlink.status, unlink.json?.loosened]).toEqual([200, false]);
      expect((await put(clientChange({ unlink: true, always_ask: true }))).status, "unlink stands alone").toBe(400);
      const settings = await http(base, "GET", "/consent/settings", { auth: "Bearer " + bearer });
      expect([settings.json.client.linked, settings.json.client.first_use, settings.json.client.trusted_applications]).toEqual([false, true, []]);
      const asks = await signCall(base, bearer, buildGroup([transfer(5, { note: "first-use" })]));
      expect(asks.status, "first use asks the phone whatever the amount").toBe(401);
      expect((await fetchRequest(base, asks.json.auth_req_id, who)).client.linked).toBe(false);
      await approve(base, asks.json.auth_req_id, asks.json.binding_code, { who });
      const linked = await http(base, "GET", "/consent/settings", { auth: "Bearer " + bearer });
      expect([linked.json.client.linked, linked.json.client.first_use]).toEqual([true, false]);
      expect((await signCall(base, bearer, buildGroup([transfer(5, { note: "linked" })]))).status, "linked and under the threshold").toBe(200);
    } finally {
      await put(clientChange({ unlink: true }));
      await relink();
    }
  }));
});

describe("settings: remember on the receipt", () => {
  it("trusts exactly the ticked applications of the request; ids that are not in it, duplicates or a denial are refused", needs(flow, async () => {
    const group = buildGroup([transfer(10, { note: "remember" }), { sender: config.address, appId: 5_151_001 }, { sender: config.address, appId: 5_151_002 }]);
    const open = async () => {
      const c = await signCall(base, bearer, group, { grp: group.grp, requireConsent: true });
      expect(c.status, JSON.stringify(c.json)).toBe(401);
      return { id: c.json.auth_req_id as string, code: c.json.binding_code as string, request: await fetchRequest(base, c.json.auth_req_id, who) };
    };
    const post = async (r: Awaited<ReturnType<typeof open>>, opts: { decision?: "approve" | "deny"; remember?: number[] }) =>
      http(base, "POST", `/consent/requests/${r.id}/receipt`, { auth, body: await buildReceipt(base, r.request, r.code, { who, ...opts }) });
    try {
      const r = await open();
      expect(r.request.authorization_details[0].applications).toEqual([{ id: 5_151_001, trusted: false }, { id: 5_151_002, trusted: false }]);
      for (const remember of [[9_999_999], [5_151_001, 5_151_001], []]) expect((await post(r, { remember })).status, JSON.stringify(remember)).toBe(400);
      expect((await post(r, { decision: "deny", remember: [5_151_001] })).status, "a denial never carries remember").toBe(400);
      expect((await post(r, { remember: [5_151_001] })).status, "the request stayed open").toBe(200);
      const trusted = (await clientView()).trusted_applications.map((a: any) => a.app_id);
      expect(trusted).toContain(5_151_001);
      expect(trusted).not.toContain(5_151_002);
      const next = await signCall(base, bearer, buildGroup([transfer(10, { note: "remember-next" }), { sender: config.address, appId: 5_151_001 }]));
      expect(next.status, "the remembered application under the threshold: nothing asked").toBe(200);
      const again = await open();
      expect(again.request.authorization_details[0].applications).toEqual([{ id: 5_151_001, trusted: true }, { id: 5_151_002, trusted: false }]);
      expect((await post(again, { remember: [5_151_001] })).status, "an application that is already trusted has nothing to remember").toBe(400);
      expect((await post(again, { remember: [5_151_002] })).status).toBe(200);
    } finally {
      await put(clientChange({ trusted_applications_remove: [app(5_151_001), app(5_151_002)] }));
    }
  }));
});
