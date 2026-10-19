/**
 * Builds and simulates CLAMM groups against public testnet, without keys and without sending anything:
 *   pnpm --filter builders exec tsx scripts/testnet-simulate.ts
 * Simulation allows empty signatures, so any funded testnet account works as the sender. The accounts come from
 * the public indexer (an asset holder of the pool's assets, an LP holder for the removal).
 */
import { addLiquidity, removeLiquidity, swapOnPool } from "../src/clamm/builders.js";
import { algosdk } from "../src/clamm/lib.js";
import { listPools } from "../src/clamm/pools.js";

const ctx = { network: "testnet", algodUrl: process.env.ALGOD_URL_TESTNET ?? "https://testnet-api.4160.nodely.dev" };
const indexer = process.env.INDEXER_URL_TESTNET ?? "https://testnet-idx.4160.nodely.dev";
const USDC = "10458941";

/** A holder of `assetId` (more than `min`) that also has spare ALGO for fees and minimum balance. */
async function holder(assetId: string, min: bigint, skip: string[] = [], spareAlgo = 20_000_000): Promise<string> {
  const r = await fetch(`${indexer}/v2/assets/${assetId}/balances?currency-greater-than=${min}&limit=100`);
  const { balances } = (await r.json()) as { balances: { address: string; amount: number }[] };
  for (const b of balances.filter((x) => !skip.includes(x.address))) {
    const a = (await (await fetch(`${ctx.algodUrl}/v2/accounts/${b.address}?exclude=all`)).json()) as { amount: number; "min-balance": number };
    if (a.amount - a["min-balance"] > spareAlgo) return b.address;
  }
  throw new Error(`no holder of ${assetId} with more than ${min} and spare ALGO`);
}

const list = await listPools({ assetA: "0", assetB: USDC }, ctx);
console.log(`pools ALGO/USDC: ${list.pools.length}, excluded: ${list.excluded.length}`);
for (const p of list.pools) console.log(`  ${p.appId} fee ${p.feePercent}% range ${p.priceMin}-${p.priceMax} liquidity ${p.liquidity}`);

const pool = [...list.pools].sort((a, b) => (BigInt(b.liquidity) > BigInt(a.liquidity) ? 1 : -1))[0];
if (!pool) throw new Error("no ALGO/USDC pool on testnet");
console.log(`using pool ${pool.appId}`);

const usdcHolder = await holder(USDC, 5_000_000n);
const report = (name: string, r: { facts?: Record<string, unknown>; transactions: string[] }) =>
  console.log(`${name}: ${r.transactions.length} txs, simulation ${JSON.stringify(r.facts?.simulation)}`);

report("add liquidity", await addLiquidity({ poolAppId: pool.appId, sender: usdcHolder, amountA: "1000000", amountB: "1000000" }, ctx));
report("swap", await swapOnPool({ poolAppId: pool.appId, sender: usdcHolder, fromAsset: USDC, fromAmount: "100000", minimumToReceive: "1" }, ctx));
// Removal needs a real LP holder (not a pool's own account, which holds the unissued LP tokens): try every pool.
let removed = false;
for (const p of list.pools.filter((x) => BigInt(x.liquidity) > 0n)) {
  const poolAccount = algosdk.getApplicationAddress(BigInt(p.appId)).toString();
  const lpHolder = await holder(p.lpTokenId, 1000n, [poolAccount], 100_000).catch(() => undefined);
  if (!lpHolder) continue;
  console.log(`pool ${p.appId}, LP holder ${lpHolder}`);
  report("remove liquidity", await removeLiquidity({ poolAppId: p.appId, sender: lpHolder, lpAmount: "1000" }, ctx));
  removed = true;
  break;
}
if (!removed) console.log("remove liquidity: no ALGO/USDC pool has an LP holder other than the pool itself");
