import { describe, it, expect, inject, beforeAll } from "vitest";
import { jcs } from "../../scripts/lib.mjs";
import { config, http, loadJson, receiptProblems, receiptShape, runDirect, runStepUp, schemaProblems, signPath, type FlowResult } from "../src/kit.ts";

const base = inject("baseUrl");

describe(`consent conformance (${inject("target")}: ${base})`, () => {
  let stepUp: Awaited<ReturnType<typeof runStepUp>>;
  let direct: Awaited<ReturnType<typeof runDirect>>;

  beforeAll(async () => {
    stepUp = await runStepUp(base);
    direct = await runDirect(base);
  });

  describe("canonical form", () => {
    it("the shared RFC 8785 vectors canonicalize identically", () => {
      for (const v of loadJson("vectors", "canonicalization.json").vectors) expect(jcs(v.input), v.name).toBe(v.canonical);
    });
  });

  describe("entry responses", () => {
    it("step-up: 401 insufficient_user_authentication matching the challenge schema", () => {
      expect(schemaProblems("stepup-challenge", stepUp.challenge.json)).toEqual([]);
      expect(stepUp.challenge.headers.get("www-authenticate") ?? "").toMatch(/insufficient_user_authentication/);
    });

    it("direct: backchannel authentication response matches its schema", () => {
      expect(schemaProblems("backchannel-authentication-response", direct.start.json)).toEqual([]);
    });
  });

  describe.each([
    ["step-up entry", () => stepUp],
    ["direct entry", () => direct],
  ] as [string, () => FlowResult][])("%s", (_name, get) => {
    it("the consent request the authenticator receives matches its schema and never carries the binding code", () => {
      const f = get();
      expect(schemaProblems("consent-request", f.request)).toEqual([]);
      expect(JSON.stringify(f.request)).not.toContain(f.bindingCode);
    });

    it("the token response matches its schema and carries a consent_receipt", () => {
      const f = get();
      expect(schemaProblems("token-response", f.tokenReply.json)).toEqual([]);
      expect(f.receipt).toBeTruthy();
    });

    it("the receipt conforms: schema, canonical form, signatures and request binding", () => {
      const f = get();
      expect(receiptProblems(f.receipt, { jwks: f.jwks, request: f.request, bindingCode: f.bindingCode })).toEqual([]);
    });

    it("the consent token signs the group it was granted for", () => {
      const f = get();
      expect(f.signReply.status, JSON.stringify(f.signReply.json)).toBe(200);
    });
  });

  describe("entries share one receipt format", () => {
    it("the request entry field tells them apart", () => {
      expect(stepUp.request.entry).toBe("step-up");
      expect(direct.request.entry).toBe("direct");
    });

    it("receipts have the same members, algorithm and version", () => {
      expect(receiptShape(stepUp.receipt)).toEqual(receiptShape(direct.receipt));
    });

    it("a spent consent cannot sign again", async () => {
      const again = await http(base, "POST", signPath(), {
        auth: "Bearer " + stepUp.tokenReply.json.access_token,
        body: { unsigned_group: config.unsignedGroup },
      });
      expect(again.status).toBeGreaterThanOrEqual(400);
    });
  });
});
