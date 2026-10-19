import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import type { FullConfig } from "biatec-concentrated-liquidity-amm";
import { createApp } from "../src/app.js";
import { loadConfig } from "../src/config.js";
import type { ClammContext } from "../src/clamm/context.js";
import { algosdk } from "../src/clamm/lib.js";
import { address, asParams, uint64 } from "../src/clamm/params.js";
import { formatScaled, registeredPool, registeredPools, selectPools } from "../src/clamm/pools.js";
import { swapOnPool } from "../src/clamm/builders.js";

/** The pool provider's pool list as recorded from testnet (all ALGO/USDC and Vote/ALGO pools, class 0). */
const fixture: FullConfig[] = (JSON.parse(readFileSync(new URL("./fixtures/testnet-pools.json", import.meta.url), "utf8")) as Record<string, string | number>[]).map(
  (p) => ({
    appId: BigInt(p.appId!), assetA: BigInt(p.assetA!), assetB: BigInt(p.assetB!), fee: BigInt(p.fee!),
    min: BigInt(p.min!), max: BigInt(p.max!), lpTokenId: BigInt(p.lpTokenId!), verificationClass: Number(p.verificationClass),
  }) as unknown as FullConfig,
);
const USDC = 10458941n;
const VOTE = 48806985n;

/** The 59-byte "fc" box name the pool provider stores per pool, as getPools decodes it. */
function boxName(p: FullConfig): Uint8Array {
  const u64 = (v: bigint) => algosdk.encodeUint64(v);
  return new Uint8Array([
    ...Buffer.from("fc"), ...u64(p.appId), ...u64(p.assetA), ...u64(p.assetB),
    ...u64(p.min), ...u64(p.max), ...u64(p.fee), ...u64(p.lpTokenId), Number(p.verificationClass),
  ]);
}

/** A context whose algod only answers the pool provider's box listing, from the fixture. */
function fakeContext(pools: FullConfig[]): ClammContext {
  const boxes = [...pools.map((p) => ({ name: boxName(p) })), { name: new Uint8Array(32) }];
  const algod = { getApplicationBoxes: () => ({ do: async () => ({ boxes }) }) };
  return { algod, apps: { configAppId: 1n, identityAppId: 2n, poolProviderAppId: 3n } } as unknown as ClammContext;
}

describe("params", () => {
  it("reads uint64 from decimal strings and safe integers", () => {
    expect(uint64({ a: "18446744073709551615" }, "a")).toBe(18446744073709551615n);
    expect(uint64({ a: 42 }, "a")).toBe(42n);
  });
  it("rejects everything else, naming the field", () => {
    for (const a of [-1, 1.5, "1e3", "0x10", "18446744073709551616", null, undefined, 2 ** 60]) {
      expect(() => uint64({ a }, "a")).toThrow(/a /);
    }
    expect(() => uint64({ a: "0" }, "a", { positive: true })).toThrow(/greater than zero/);
  });
  it("checks addresses and the params object", () => {
    expect(address({ s: "ALGONAUTSPIUHDCX3SLFXOFDUKOE4VY36XV4JX2JHQTWJNKVBKPEBQACRY" }, "s")).toMatch(/^ALGONAUTS/);
    expect(() => address({ s: "ALGONAUTS" }, "s")).toThrow(/s must be an Algorand address/);
    expect(() => asParams([])).toThrow();
    expect(() => asParams(null)).toThrow();
  });
});

describe("pools", () => {
  it("decodes the pool provider boxes and ignores other boxes", async () => {
    const pools = await registeredPools(fakeContext(fixture));
    expect(pools).toHaveLength(fixture.length);
    expect(pools.find((p) => p.appId === 768784928n)).toMatchObject({ assetA: USDC, assetB: 0n, fee: 1_000_000n, min: 130_000_000n, max: 140_000_000n });
  });

  it("finds a pair's pools in either asset order", () => {
    const a = selectPools(fixture, 0n, USDC).eligible;
    const b = selectPools(fixture, USDC, 0n).eligible;
    expect(a).toHaveLength(11);
    expect(b.map((p) => p.appId)).toEqual(a.map((p) => p.appId));
    expect(selectPools(fixture, VOTE, 0n).eligible).toHaveLength(8);
    expect(selectPools(fixture, VOTE, USDC).eligible).toHaveLength(0);
  });

  it("leaves out pools above the account's verification class and says why", () => {
    const gated = fixture.map((p) => (p.appId === 768784928n || p.appId === 768787834n ? { ...p, verificationClass: 2 } : p));
    const anon = selectPools(gated, 0n, USDC, 0);
    expect(anon.eligible).toHaveLength(9);
    expect(anon.excluded.map((x) => x.appId).sort()).toEqual(["768784928", "768787834"]);
    expect(anon.excluded.every((x) => x.verificationClass === 2 && x.reason === "verification_class")).toBe(true);
    expect(selectPools(gated, 0n, USDC, 2).excluded).toHaveLength(0);
    expect(selectPools(gated, 0n, USDC).excluded).toHaveLength(0);
  });

  it("only builds against registered pools", async () => {
    const c = fakeContext(fixture);
    await expect(registeredPool(c, 768784928n)).resolves.toMatchObject({ appId: 768784928n });
    await expect(registeredPool(c, 123n)).rejects.toThrow(/not a Biatec pool/);
  });

  it("formats SCALE values", () => {
    expect(formatScaled(1_000_000n * 100n)).toBe("0.1");
    expect(formatScaled(1_000_000_000n)).toBe("1");
    expect(formatScaled(130_000_000n)).toBe("0.13");
  });
});

describe("builders", () => {
  it("refuse a swap without slippage protection before touching the chain", async () => {
    const params = { poolAppId: "1", sender: "ALGONAUTSPIUHDCX3SLFXOFDUKOE4VY36XV4JX2JHQTWJNKVBKPEBQACRY", fromAsset: "0", fromAmount: "1", minimumToReceive: "0" };
    await expect(swapOnPool(params, { network: "testnet", algodUrl: "http://127.0.0.1:1" })).rejects.toThrow(/minimumToReceive must be greater than zero/);
  });
});

describe("routes", () => {
  const SECRET = "test-secret-0123456789";
  const app = createApp(
    loadConfig({ BUILDERS_INTERNAL_SECRET: SECRET, ALGOD_URL_TESTNET: "http://algod:4001" }),
    { facts: async () => ({ transactions: ["AAAA"], facts: { amount: 18446744073709551615n } }) },
    { echo: async (params) => ({ params, big: 2n ** 64n }), boom: async () => { throw new Error("pool 1 is not a Biatec pool"); } },
  );
  const post = (path: string, body: unknown) =>
    app.request(path, { method: "POST", headers: { "x-internal-secret": SECRET, "content-type": "application/json" }, body: JSON.stringify(body) });

  it("serves queries with bigints as strings", async () => {
    const r = await post("/v1/query/echo", { network: "testnet", params: { a: 1 } });
    expect(r.status).toBe(200);
    expect(await r.json()).toEqual({ params: { a: 1 }, big: "18446744073709551616" });
  });
  it("passes builder facts through", async () => {
    expect(await (await post("/v1/build/facts", { network: "testnet" })).json()).toEqual({ transactions: ["AAAA"], facts: { amount: "18446744073709551615" } });
  });
  it("maps unknown and failing queries", async () => {
    expect((await post("/v1/query/none", { network: "testnet" })).status).toBe(404);
    expect((await post("/v1/query/echo", { network: "nowhere" })).status).toBe(400);
    const f = await post("/v1/query/boom", { network: "testnet" });
    expect(f.status).toBe(422);
    expect(await f.json()).toEqual({ error: "query_failed", message: "pool 1 is not a Biatec pool" });
  });
  it("requires the secret for queries too", async () => {
    expect((await app.request("/v1/query/echo", { method: "POST", body: "{}" })).status).toBe(401);
  });
});
