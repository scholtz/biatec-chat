import algosdk from "algosdk";
import type { Builder } from "../app.js";
import { BadRequest, address, text, uint } from "../safe/codec.js";

/** Suggested transaction params; injectable so tests need no algod. */
export type ParamsSource = (algodUrl: string) => Promise<algosdk.SuggestedParams>;

const defaultParams: ParamsSource = (algodUrl) => new algosdk.Algodv2("", algodUrl).getTransactionParams().do();

/** Real payments stay off unless the operator turns the sidecar side on too (the backend has its own flag). */
export function x402Enabled(env: Record<string, string | undefined> = process.env): boolean {
  return env.X402_ENABLED === "true";
}

/**
 * x402: the buyer side of the `exact` scheme on Algorand: one USDC (ASA) transfer from the user's address to the
 * seller's `payTo`. The amount comes from the seller's payment requirements; the backend does not trust it, because
 * Biatec OIDC decodes the value from this very group. The note carries a hash of the resource URL so the facilitator
 * can bind the payment to the resource. Fees are the minimum; nothing here signs or submits.
 */
export function x402Pay(getParams: ParamsSource = defaultParams): Builder {
  return async (params, ctx) => {
    if (!params || typeof params !== "object") throw new BadRequest("params must be an object");
    const p = params as Record<string, unknown>;
    const from = address(p.from, "from");
    const payTo = address(p.payTo, "payTo");
    const assetId = uint(p.assetId, "assetId", { min: 1n });
    const amount = uint(p.amount, "amount", { min: 1n });
    const resource = text(p.resourceUrl, "resourceUrl", 2048);
    if (!resource.startsWith("https://")) throw new BadRequest("resourceUrl must be https");
    if (from === payTo) throw new BadRequest("payTo must differ from from");
    const sp = await getParams(ctx.algodUrl);
    const digest = Buffer.from(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(resource))).toString("hex");
    const txn = algosdk.makeAssetTransferTxnWithSuggestedParamsFromObject({
      sender: from,
      receiver: payTo,
      assetIndex: assetId,
      amount,
      note: new TextEncoder().encode(`x402:${digest}`),
      suggestedParams: sp,
    });
    return {
      transactions: [Buffer.from(algosdk.encodeUnsignedTransaction(txn)).toString("base64")],
      preview: { kind: "x402.pay", from, payTo, assetId: assetId.toString(), amount: amount.toString(), resourceUrl: resource, network: ctx.network },
    };
  };
}

export function x402Builders(getParams?: ParamsSource): Record<string, Builder> {
  return { x402: x402Pay(getParams) };
}
