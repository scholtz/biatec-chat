import { describe, it, expect, inject } from "vitest";
import type { Need } from "../src/needs.ts";
import { jcs } from "../../scripts/lib.mjs";
import { config, http, lazy, loadJson, needs, receiptProblems, receiptShape, runDirect, runStepUp, schemaProblems, signPath, type FlowResult } from "../src/kit.ts";

const base = inject("baseUrl");

describe(`consent conformance (${inject("target")}: ${base})`, () => {
  // A flow plays client and authenticator, so it needs the user's token, the enrolled device key and the mock's device lookup;
  // the direct entry also the client secret. Started on first use only: a run that cannot offer these skips the cases below.
  const stepUpNeeds = ["userToken", "deviceKey", "mockHelpers"] as const;
  const directNeeds = [...stepUpNeeds, "clientSecret"] as const;
  const stepUpFlow = lazy(() => runStepUp(base));
  const directFlow = lazy(() => runDirect(base));

  describe("canonical form", () => {
    it("the shared RFC 8785 vectors canonicalize identically", () => {
      for (const v of loadJson("vectors", "canonicalization.json").vectors) expect(jcs(v.input), v.name).toBe(v.canonical);
    });
  });

  describe("entry responses", () => {
    it("step-up: 401 insufficient_user_authentication matching the challenge schema", needs([...stepUpNeeds], async () => {
      const stepUp = await stepUpFlow();
      expect(schemaProblems("stepup-challenge", stepUp.challenge.json)).toEqual([]);
      expect(stepUp.challenge.headers.get("www-authenticate") ?? "").toMatch(/insufficient_user_authentication/);
    }));

    it("direct: backchannel authentication response matches its schema", needs([...directNeeds], async () => {
      expect(schemaProblems("backchannel-authentication-response", (await directFlow()).start.json)).toEqual([]);
    }));
  });

  describe.each([
    ["step-up entry", stepUpFlow, stepUpNeeds],
    ["direct entry", directFlow, directNeeds],
  ] as unknown as [string, () => Promise<FlowResult>, readonly Need[]][])("%s", (_name, get, flowNeeds) => {
    it("the consent request the authenticator receives matches its schema and never carries the binding code", needs([...flowNeeds], async () => {
      const f = await get();
      expect(schemaProblems("consent-request", f.request)).toEqual([]);
      expect(JSON.stringify(f.request)).not.toContain(f.bindingCode);
    }));

    it("the token response matches its schema and carries a consent_receipt", needs([...flowNeeds], async () => {
      const f = await get();
      expect(schemaProblems("token-response", f.tokenReply.json)).toEqual([]);
      expect(f.receipt).toBeTruthy();
    }));

    it("the receipt conforms: schema, canonical form, signatures and request binding", needs([...flowNeeds], async () => {
      const f = await get();
      expect(receiptProblems(f.receipt, { jwks: f.jwks, request: f.request, bindingCode: f.bindingCode })).toEqual([]);
    }));

    it("the consent token signs the group it was granted for", needs([...flowNeeds], async () => {
      const f = await get();
      expect(f.signReply.status, JSON.stringify(f.signReply.json)).toBe(200);
    }));
  });

  describe("entries share one receipt format", () => {
    it("the request entry field tells them apart", needs([...directNeeds], async () => {
      expect((await stepUpFlow()).request.entry).toBe("step-up");
      expect((await directFlow()).request.entry).toBe("direct");
    }));

    it("receipts have the same members, algorithm and version", needs([...directNeeds], async () => {
      expect(receiptShape((await stepUpFlow()).receipt)).toEqual(receiptShape((await directFlow()).receipt));
    }));

    it("a spent consent cannot sign again", needs([...stepUpNeeds], async () => {
      const stepUp = await stepUpFlow();
      const again = await http(base, "POST", signPath(), {
        auth: "Bearer " + stepUp.tokenReply.json.access_token,
        body: { transactions: config.unsignedGroup, origin: config.origin },
      });
      expect(again.status).toBeGreaterThanOrEqual(400);
    }));
  });
});
