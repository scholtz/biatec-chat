// What the signing endpoint answers besides a signature, and the rules of the policy (AlgorandGoogleDriveAccount issues 27 and 28):
// each refusal the chat backend has to map to something the user can read, reserve-then-spend, the user's own threshold and the
// settings endpoint. The defaults are the mock's seed users (see the README for the variables that point the suite at another server).
import { describe, it, expect, inject } from "vitest";
import { account, buildGroup, config, elevate, http, needs, schemaProblems, signCall, transfer } from "../src/kit.ts";
import type { Need } from "../src/needs.ts";

const base = inject("baseUrl");
const bearer = (t: string) => "Bearer " + t;
// What the flow of a case needs: the user's token, the enrolled device key and the mock's device lookup (the authenticator is played by the kit).
const approving: Need[] = ["userToken", "deviceKey", "mockHelpers"];
const decimal = /^(0|[1-9][0-9]*)(\.[0-9]+)?$/;

describe("the group is decoded and its id recomputed", () => {
  it("an inconsistent grp is 400 group_hash_mismatch", needs(["userToken"], async () => {
    const a = buildGroup([transfer(10), transfer(20, { note: "a" })]);
    const b = buildGroup([transfer(10, { note: "b" }), transfer(20, { note: "a" })]);
    const r = await signCall(base, config.stepUpBearer, { items: a.items, grp: b.grp });
    expect([r.status, r.json?.error]).toEqual([400, "group_hash_mismatch"]);
  }));

  it("more than one transaction without a group id is 400 group_hash_mismatch", needs(["userToken"], async () => {
    const ungrouped = buildGroup([transfer(10), transfer(20)], { grouped: false });
    const r = await signCall(base, config.stepUpBearer, ungrouped);
    expect([r.status, r.json?.error]).toEqual([400, "group_hash_mismatch"]);
  }));

  for (const [name, extra] of [
    ["a rekey", { rekeyTo: account("attacker") }],
    ["an asset transfer with a close-to address", { closeTo: account("attacker") }],
    ["an asset transfer with a clawback address", { clawbackFrom: account("attacker") }],
  ] as const) {
    it(`${name} is 403 consent_unsupported_operation`, needs(["userToken"], async () => {
      const r = await signCall(base, config.stepUpBearer, buildGroup([transfer(10, extra)]));
      expect([r.status, r.json?.error]).toEqual([403, "consent_unsupported_operation"]);
    }));
  }

  it("a network that cannot be consented to is 403 consent_unsupported_network", needs(["userToken"], async () => {
    const r = await signCall(base, config.stepUpBearer, buildGroup([transfer(10)]), {}, config.unsupportedNetwork);
    expect([r.status, r.json?.error]).toEqual([403, "consent_unsupported_network"]);
  }));
});

describe("403 refusals of the policy", () => {
  it("no active device and an action that needs the phone: consent_unavailable with reason no_active_device", needs(["bearer:noDevice"], async () => {
    const r = await signCall(base, config.bearers.noDevice, buildGroup([transfer(5000)]));
    expect([r.status, r.json?.error, r.json?.reason]).toEqual([403, "consent_unavailable", "no_active_device"]);
  }));

  it("a paused client: client_paused", needs(["bearer:paused"], async () => {
    const r = await signCall(base, config.bearers.paused, buildGroup([transfer(5)]));
    expect([r.status, r.json?.error]).toEqual([403, "client_paused"]);
  }));

  it("a user who blocks clients without a proven domain: unproven_client_blocked", needs(["bearer:blocksUnproven"], async () => {
    const r = await signCall(base, config.bearers.blocksUnproven, buildGroup([transfer(5)]));
    expect([r.status, r.json?.error]).toEqual([403, "unproven_client_blocked"]);
  }));

  it("no active device in the first day with a client: daily_cap_exceeded carries cap_eur", needs(["bearer:capped"], async () => {
    const r = await signCall(base, config.bearers.capped, buildGroup([transfer(500)]));
    expect(r.status).toBe(403);
    expect(r.json?.error).toBe("daily_cap_exceeded");
    expect(r.json?.cap_eur).toMatch(decimal);
    expect(r.json?.reason).toBe("no_active_device");
  }));
});

describe("429 limits", () => {
  it("too_many_pending_requests names the limit and its scope", needs(["userToken", "mockHelpers"], async () => {
    const group = buildGroup([transfer(5000)]);
    const origin = `https://pending-${Date.now().toString(36)}.example`;
    let refused: Awaited<ReturnType<typeof http>> | undefined;
    for (let i = 0; i < 12 && !refused; i++) {
      const r = await signCall(base, config.stepUpBearer, group, { origin });
      if (r.status === 429) refused = r;
      else expect(r.status, JSON.stringify(r.json)).toBe(401);
    }
    expect(refused, "a 13th request from one origin was still opened").toBeDefined();
    expect(refused!.json?.error).toBe("too_many_pending_requests");
    expect(Number.isInteger(refused!.json?.limit)).toBe(true);
    expect(refused!.json?.scope?.origin).toBe(origin);
  }));

  it("rate_limited comes with Retry-After", needs(["bearer:alwaysAsk", "mockHelpers"], async () => {
    // The always-ask user is challenged for any group, so every call opens a request; each from its own origin to stay clear of the pending limit.
    let limited: Awaited<ReturnType<typeof http>> | undefined;
    for (let i = 0; i < 200 && !limited; i++) {
      const r = await signCall(base, config.bearers.alwaysAsk, buildGroup([transfer(1, { note: String(i) })]));
      if (r.status === 429) limited = r;
      else expect(r.status, JSON.stringify(r.json)).toBe(401);
    }
    expect(limited, "no hourly limit was reached in 200 requests").toBeDefined();
    expect(limited!.json?.error).toBe("rate_limited");
    expect(Number(limited!.headers.get("retry-after"))).toBeGreaterThanOrEqual(1);
  }));
});

describe("a consent is bound, reserved and spent", () => {
  it("one byte changed after approval is 403 consent_hash_mismatch and leaves the consent usable", needs(approving, async () => {
    const original = buildGroup([transfer(3000), transfer(2000, { receiver: account("second payee") })]);
    const { token } = await elevate(base, original, { grp: original.grp });
    const altered = buildGroup([transfer(3000, { note: "x" }), transfer(2000, { receiver: account("second payee") })]);
    const mismatch = await signCall(base, token, altered);
    expect([mismatch.status, mismatch.json?.error]).toEqual([403, "consent_hash_mismatch"]);
    const ok = await signCall(base, token, original);
    expect(ok.status, JSON.stringify(ok.json)).toBe(200);
    expect(ok.json?.signedTransactions).toHaveLength(2);
  }));

  it("a second use of an elevated token is 401 with error_description consent not usable, and no new challenge", needs(approving, async () => {
    const group = buildGroup([transfer(5000, { note: "once" })]);
    const { token } = await elevate(base, group);
    expect((await signCall(base, token, group)).status).toBe(200);
    const again = await signCall(base, token, group);
    expect(again.status).toBe(401);
    expect(again.json?.error).toBe("insufficient_user_authentication");
    expect(again.json?.error_description).toBe("consent not usable");
    expect(again.json?.auth_req_id).toBeUndefined();
  }));

  it("the signing answer carries valuedEur, and the consent token is refused on every other endpoint", needs(approving, async () => {
    const group = buildGroup([transfer(5000, { note: "valued" })]);
    const { token } = await elevate(base, group);
    const other = await http(base, "GET", "/consent/settings", { auth: bearer(token) });
    expect(other.status).toBe(401);
    const signed = await signCall(base, token, group);
    expect(signed.status).toBe(200);
    expect(signed.json?.valuedEur === null || decimal.test(signed.json?.valuedEur)).toBe(true);
  }));
});

describe("the user's own threshold and GET /consent/settings", () => {
  it("a threshold of 5000 EUR asks for nothing below it, the default asks above 1000", needs(["userToken", "bearer:threshold5000"], async () => {
    const group = buildGroup([transfer(3000, { note: "threshold" })]); // 2767.50 EUR in the mock's price table
    const own = await signCall(base, config.bearers.threshold5000, group);
    expect(own.status, JSON.stringify(own.json)).toBe(200);
    const def = await signCall(base, config.stepUpBearer, group);
    expect(def.status).toBe(401);
  }));

  it("answers the calling client's view", needs(["userToken", "bearer:threshold5000"], async () => {
    const r = await http(base, "GET", "/consent/settings", { auth: bearer(config.stepUpBearer) });
    expect(r.status).toBe(200);
    const s = r.json;
    expect(s.threshold_eur).toMatch(decimal);
    expect(s.default_threshold_eur).toMatch(decimal);
    expect(typeof s.ask_on_test_networks).toBe("boolean");
    expect(typeof s.device_enrolled).toBe("boolean");
    expect(typeof s.device_active).toBe("boolean");
    expect(typeof s.client.linked).toBe("boolean");
    expect(typeof s.client.domain_proven).toBe("boolean");
    expect(Array.isArray(s.client.trusted_applications)).toBe(true);
    for (const a of s.client.trusted_applications) expect(Number.isInteger(a.app_id) && typeof a.network === "string").toBe(true);
    expect((await http(base, "GET", "/consent/settings")).status).toBe(401);
    expect((await http(base, "GET", "/consent/settings", { auth: bearer(config.bearers.threshold5000) })).json.threshold_eur).toMatch(/^5000(\.00)?$/);
  }));
});

describe("the consent request as protocol 1.1", () => {
  it("lists every transfer and names the client by fingerprint and proven domain", needs(approving, async () => {
    const group = buildGroup([transfer(3000, { note: "card" }), transfer(2000, { receiver: account("second payee") })]);
    const { request } = await elevate(base, group, { grp: group.grp });
    expect(schemaProblems("consent-request", request)).toEqual([]);
    const d = request.authorization_details[0];
    expect(d.transfers).toHaveLength(2);
    expect(d.group_hash).toMatch(/^[A-Z2-7]{52}$/);
    expect(d.value_known).toBe(true);
    expect(Object.keys(request.client).sort()).toEqual(request.client.domain_proven ? ["domain", "domain_proven", "fingerprint", "linked"] : ["domain_proven", "fingerprint", "linked"]);
  }));
});
