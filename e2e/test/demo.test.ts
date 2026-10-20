import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { verifyBundle } from "../../packages/evidence-verifier/src/verify.ts";
import { crossCheck, renderRunLog, runDemo, type DemoRun } from "../src/demo.ts";
import { MockOidcHarness } from "../src/oidc.ts";

const out = join(dirname(fileURLToPath(import.meta.url)), "..", "out");
const h = new MockOidcHarness();
let run: DemoRun;
const startedAt = new Date().toISOString();
const verdicts: string[] = [];

beforeAll(async () => {
  await h.start();
  run = await runDemo(h);
});

afterAll(async () => {
  await h.stop();
  // The run log is saved on every run (e2e/out is git-ignored; a reviewed copy lives in e2e/runs).
  mkdirSync(out, { recursive: true });
  writeFileSync(join(out, "demo-run.log.md"), renderRunLog(run, { startedAt, verdicts }));
  writeFileSync(join(out, "run.json"), JSON.stringify(run.steps, null, 2));
  writeFileSync(join(out, "evidence-alice.json"), JSON.stringify(run.bundles.alice, null, 2));
  writeFileSync(join(out, "evidence-bob.json"), JSON.stringify(run.bundles.bob, null, 2));
  writeFileSync(join(out, "evidence-jwks.json"), JSON.stringify(run.jwks, null, 2));
});

describe("demo script steps 1-8 (always-ask fallback, mock Biatec OIDC)", () => {
  it("runs every step and every signing call ends signed", () => {
    expect([...new Set(run.steps.map((s) => s.step))]).toEqual([1, 2, 3, 4, 5, 6, 7]);
    expect(run.steps).toHaveLength(12);
    for (const s of run.steps) expect(s.txIds.length).toBeGreaterThan(0);
    verdicts.push(`${run.steps.length} signing calls over steps 1-7 all ended signed`);
  });

  it("asks the phone for every call: Alice 9 times, Bob 3 times, and the first action is phone-approved", () => {
    const asked = (u: string) => run.steps.filter((s) => s.user === u && s.phone === "asked").length;
    expect(asked("Alice")).toBe(9);
    expect(asked("Bob")).toBe(3);
    expect(run.steps.every((s) => s.phone === "asked")).toBe(true);
    expect(run.steps.filter((s) => s.extra)).toHaveLength(7);
    verdicts.push("phone prompts: Alice 9, Bob 3 (7 of them are the documented extra prompts)");
  });

  it("shows the phone the same group hash the preview was bound to, with the amounts and the pool application", () => {
    for (const s of run.steps) expect(s.phoneCard!.groupHash).toBe(s.groupHash);
    const liquidity = run.steps.filter((s) => s.step === 5 || s.step === 6).filter((s) => s.capability === "safe.approve");
    expect(liquidity).toHaveLength(2);
    for (const s of liquidity) {
      expect(Number(s.phoneCard!.fiatEur)).toBe(4000);
      expect(s.phoneCard!.applicationId).toBe(600);
    }
    const fund = run.steps.find((s) => s.step === 4)!;
    expect(fund.phoneCard!.fiatEur).toBe("4000.00");
    expect(fund.bindingCode).toMatch(/^\d{6}$/);
  });

  it("refuses a wrong binding code and a swapped group after approval, and a consent token works once", () => {
    const fund = run.steps.find((s) => s.step === 4)!;
    expect(fund.wrongCodeAttempts).toBe(1);
    expect(run.refusedSwitches).toHaveLength(1);
    expect(run.refusedSwitches[0]).not.toBe(fund.groupHash);
    expect(fund.notes.join(" ")).toMatch(/group_hash_mismatch/);
    expect(run.steps.every((s) => s.notes.some((n) => n.includes("cannot be replayed")))).toBe(true);
    verdicts.push("wrong code refused, swapped group refused with group_hash_mismatch, consent single use");
  });
});

describe("step 8: evidence verifies offline and lines up with the run", () => {
  for (const user of ["Alice", "Bob"] as const) {
    const bundleOf = () => (user === "Alice" ? run.bundles.alice : run.bundles.bob);

    it(`${user}: the hash chain and signatures verify against the published JWKS`, () => {
      const r = verifyBundle(bundleOf(), { jwks: run.jwks as never });
      expect(r.errors).toEqual([]);
      expect(r.valid).toBe(true);
      expect(r.keysAuthenticated).toBe(true);
      expect(r.payloadsVerified).toBe(r.entries);
      verdicts.push(`${user}: bundle of ${r.entries} entries verifies against the JWKS`);
    });

    it(`${user}: consent receipts verify against Biatec OIDC's JWKS and match transaction ids and group hashes`, () => {
      expect(crossCheck(bundleOf(), run, user)).toEqual([]);
      verdicts.push(`${user}: receipts, group hashes and transaction ids line up with the run`);
    });
  }

  it("the two sessions are separate conversations", () => {
    expect(run.bundles.alice.conversationId).not.toBe(run.bundles.bob.conversationId);
  });

  it("a tampered payload, a dropped entry and a wrong JWKS are all detected", () => {
    const jwks = run.jwks as never;
    const edited = structuredClone(run.bundles.alice);
    const preview = edited.entries.find((e) => e.kind === "action_preview")!;
    (preview.payload as any).groupHash = "X".repeat(52);
    expect(verifyBundle(edited, { jwks }).valid).toBe(false);

    const dropped = structuredClone(run.bundles.bob);
    dropped.entries.splice(3, 1);
    expect(verifyBundle(dropped, { jwks }).valid).toBe(false);

    expect(verifyBundle(run.bundles.alice, { jwks: run.oidcJwks as never }).valid).toBe(false);
    verdicts.push("tampered payload, dropped entry and a foreign JWKS are rejected");
  });
});
