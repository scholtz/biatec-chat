import algosdk from "algosdk";
import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { x402Builders, x402Enabled } from "../src/x402/builders.js";

const SECRET = "test-secret-0123456789";
const from = algosdk.generateAccount().addr.toString();
const payTo = algosdk.generateAccount().addr.toString();
const sp: algosdk.SuggestedParams = { fee: 1000n, flatFee: true, firstValid: 100n, lastValid: 1100n, genesisHash: new Uint8Array(32).fill(1), genesisID: "testnet-v1.0", minFee: 1000n };
const app = createApp({ port: 0, internalSecret: SECRET, algod: { testnet: "http://algod.invalid" } }, x402Builders(async () => sp));

const post = (params: unknown) =>
  app.request("/v1/build/x402", { method: "POST", headers: { "x-internal-secret": SECRET, "content-type": "application/json" }, body: JSON.stringify({ network: "testnet", params }) });

describe("x402 builder", () => {
  it("is off unless X402_ENABLED=true", () => {
    expect(x402Enabled({})).toBe(false);
    expect(x402Enabled({ X402_ENABLED: "true" })).toBe(true);
  });

  it("builds one USDC transfer with the amount from the request", async () => {
    const res = await post({ from, payTo, assetId: 10458941, amount: "250000", resourceUrl: "https://shop.example/premium" });
    expect(res.status).toBe(200);
    const body = (await res.json()) as { transactions: string[] };
    expect(body.transactions).toHaveLength(1);
    const txn = algosdk.decodeUnsignedTransaction(Buffer.from(body.transactions[0], "base64"));
    expect(txn.type).toBe(algosdk.TransactionType.axfer);
    expect(txn.sender.toString()).toBe(from);
    expect(txn.assetTransfer?.receiver.toString()).toBe(payTo);
    expect(txn.assetTransfer?.amount).toBe(250000n);
    expect(txn.assetTransfer?.assetIndex).toBe(10458941n);
  });

  it.each([
    [{ from, payTo, assetId: 1, amount: 0, resourceUrl: "https://a.example/x" }],
    [{ from, payTo: from, assetId: 1, amount: 5, resourceUrl: "https://a.example/x" }],
    [{ from, payTo, assetId: 1, amount: 5, resourceUrl: "http://a.example/x" }],
    [{ from, payTo: "nope", assetId: 1, amount: 5, resourceUrl: "https://a.example/x" }],
  ])("rejects bad request %#", async (params) => {
    expect((await post(params)).status).toBe(422);
  });

  it("requires the internal secret", async () => {
    const res = await app.request("/v1/build/x402", { method: "POST", body: "{}" });
    expect(res.status).toBe(401);
  });
});
