// The suite must fail on a broken receipt. Each case corrupts one property of a good receipt (or has a proxy do
// it on the wire) and asserts that the conformance checks report it.
import { describe, it, expect, inject, beforeAll, afterAll } from "vitest";
import { createServer, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import { lazy, needs, receiptProblems, runDirect, runStepUp } from "../src/kit.ts";
import type { Need } from "../src/needs.ts";

const base = inject("baseUrl");
// Every case plays client and authenticator against the server, then corrupts what came back.
const flowNeeds: Need[] = ["userToken", "deviceKey", "mockHelpers"];

describe("conformance checks detect a broken receipt", () => {
  const goodFlow = lazy(() => runStepUp(base));
  let good!: Awaited<ReturnType<typeof runStepUp>>;
  const check = (receipt: unknown) => receiptProblems(receipt, { jwks: good.jwks, request: good.request, bindingCode: good.bindingCode });
  const clone = () => structuredClone(good.receipt);
  const detects = (name: string, fn: () => unknown) => it(name, needs(flowNeeds, async () => { good = await goodFlow(); await fn(); }));

  detects("accepts the untouched receipt", () => expect(check(good.receipt)).toEqual([]));

  detects("flags a signed member changed after signing", () => {
    const r = clone();
    r.signed.authorization_details[0].group_hash = "A".repeat(52);
    expect(check(r).join()).toMatch(/verify|differs/);
  });

  detects("flags a corrupted signature", () => {
    const r = clone();
    r.signature = (r.signature[0] === "A" ? "B" : "A") + r.signature.slice(1);
    expect(check(r).join()).toMatch(/verify/);
  });

  detects("flags a swapped device key", () => {
    const r = clone();
    r.device.public_key = { ...r.device.public_key, x: r.device.public_key.y };
    expect(check(r).join()).toMatch(/verify/);
  });

  detects("flags a wrong binding code and a deny decision", () => {
    const r = clone();
    r.signed.binding_code = "000000";
    r.signed.decision = "deny";
    expect(check(r).length).toBeGreaterThan(0);
  });

  detects("flags schema violations (extra member, wrong version, missing signature)", () => {
    const extra = clone(); extra.surprise = true;
    expect(check(extra).join()).toMatch(/consent-receipt/);
    const ver = clone(); ver.version = "2";
    expect(check(ver).join()).toMatch(/consent-receipt/);
    const missing = clone(); delete missing.signature;
    expect(check(missing).join()).toMatch(/consent-receipt/);
  });

  detects("flags a receipt for another request", () => {
    expect(receiptProblems(good.receipt, { jwks: good.jwks, request: { ...good.request, auth_req_id: "ciba_other" }, bindingCode: good.bindingCode }).join()).toMatch(/auth_req_id/);
  });
});

describe("both entries are reported when the token endpoint returns a tampered receipt", () => {
  let proxy: Server;
  let proxyBase: string;
  beforeAll(async () => {
    proxy = createServer(async (req, res) => {
      const chunks: Buffer[] = [];
      for await (const c of req) chunks.push(c as Buffer);
      const headers: Record<string, string> = {};
      for (const k of ["authorization", "content-type"]) if (req.headers[k]) headers[k] = String(req.headers[k]);
      const upstream = await fetch(base + req.url, { method: req.method, headers, body: chunks.length ? Buffer.concat(chunks) : undefined });
      let text = await upstream.text();
      if (req.url === "/token" && upstream.status === 200) {
        const j = JSON.parse(text);
        j.consent_receipt.signed.binding_message = "tampered";
        text = JSON.stringify(j);
      }
      res.writeHead(upstream.status, { "Content-Type": "application/json" });
      res.end(text);
    });
    await new Promise<void>((r) => proxy.listen(0, "127.0.0.1", r));
    proxyBase = `http://127.0.0.1:${(proxy.address() as AddressInfo).port}`;
  });
  afterAll(() => new Promise<void>((r) => proxy.close(() => r())));

  for (const [name, run, extra] of [["step-up", runStepUp, []], ["direct", runDirect, ["clientSecret"]]] as const) {
    it(`${name} entry`, needs([...flowNeeds, ...(extra as readonly Need[])], async () => {
      const f = await run(proxyBase);
      expect(receiptProblems(f.receipt, { jwks: f.jwks, request: f.request, bindingCode: f.bindingCode }).length).toBeGreaterThan(0);
    }));
  }
});
