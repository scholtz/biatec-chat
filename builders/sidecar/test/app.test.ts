import { describe, expect, it } from "vitest";
import { createApp, type Builder } from "../src/app.js";
import { loadConfig } from "../src/config.js";

const SECRET = "test-secret-0123456789";
const config = loadConfig({ BUILDERS_INTERNAL_SECRET: SECRET, ALGOD_URL_TESTNET: "http://algod:4001", ALGOD_URL_MAIN_NET: "http://m" });
const echo: Builder = async (params, ctx) => ({ transactions: [Buffer.from(JSON.stringify({ params, n: ctx.network })).toString("base64")] });
const app = createApp(config, { echo, bad: async () => ({ transactions: ["not base64!"] }), boom: async () => { throw new Error("nope"); } });
const auth = { "x-internal-secret": SECRET, "content-type": "application/json" };
const post = (path: string, body: unknown, headers: Record<string, string> = auth) =>
  app.request(path, { method: "POST", headers, body: JSON.stringify(body) });

describe("config", () => {
  it("refuses to start without a secret", () => {
    expect(() => loadConfig({})).toThrow();
    expect(() => loadConfig({ BUILDERS_INTERNAL_SECRET: "short" })).toThrow();
  });
  it("reads algod per network", () => {
    expect(config.algod).toEqual({ testnet: "http://algod:4001", "main-net": "http://m" });
  });
});

describe("auth", () => {
  it("health is open", async () => {
    const r = await app.request("/health");
    expect(r.status).toBe(200);
    expect(await r.json()).toEqual({ status: "ok" });
  });
  it("rejects calls without the secret", async () => {
    expect((await app.request("/v1/networks")).status).toBe(401);
    expect((await post("/v1/build/echo", { network: "testnet" }, { "content-type": "application/json" })).status).toBe(401);
  });
  it("rejects a wrong secret of any length", async () => {
    for (const s of ["wrong", SECRET + "x", SECRET.replace("t", "T")]) {
      expect((await app.request("/v1/networks", { headers: { "x-internal-secret": s } })).status).toBe(401);
    }
  });
  it("accepts the secret", async () => {
    const r = await app.request("/v1/networks", { headers: auth });
    expect(r.status).toBe(200);
    expect(await r.json()).toEqual({ networks: ["main-net", "testnet"] });
  });
});

describe("build", () => {
  it("returns base64 transactions", async () => {
    const r = await post("/v1/build/echo", { network: "TestNet", params: { a: 1 } });
    expect(r.status).toBe(200);
    const { transactions } = (await r.json()) as { transactions: string[] };
    expect(JSON.parse(Buffer.from(transactions[0]!, "base64").toString())).toEqual({ params: { a: 1 }, n: "testnet" });
  });
  it("404 for unknown builder, 400 for unknown network or bad JSON", async () => {
    expect((await post("/v1/build/none", { network: "testnet" })).status).toBe(404);
    expect((await post("/v1/build/constructor", { network: "testnet" })).status).toBe(404);
    expect((await post("/v1/build/echo", { network: "nowhere" })).status).toBe(400);
    expect((await post("/v1/build/echo", { network: "constructor" })).status).toBe(400);
    expect((await app.request("/v1/build/echo", { method: "POST", headers: auth, body: "{" })).status).toBe(400);
  });
  it("maps builder failures and invalid output", async () => {
    const f = await post("/v1/build/boom", { network: "testnet" });
    expect(f.status).toBe(422);
    expect(await f.json()).toEqual({ error: "build_failed", message: "nope" });
    expect((await post("/v1/build/bad", { network: "testnet" })).status).toBe(502);
  });
});
