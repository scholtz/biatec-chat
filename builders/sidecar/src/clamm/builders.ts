import type { Algodv2, Transaction } from "algosdk";
import type { BiatecClammPoolClient, FullConfig } from "biatec-concentrated-liquidity-amm";
import type { BuildContext, BuildResult } from "../app.js";
import { clammContext, unsignedAccount, verificationClassOf, type ClammContext } from "./context.js";
import { algosdk, clamm } from "./lib.js";
import { address, asParams, uint64, type Params } from "./params.js";
import { poolFacts, registeredPool } from "./pools.js";

interface Prepared {
  c: ClammContext;
  pool: FullConfig;
  sender: string;
  client: BiatecClammPoolClient;
}

/** Shared first step: the pool must be registered at the pool provider and the sender must meet its class. */
async function prepare(p: Params, ctx: BuildContext): Promise<Prepared> {
  const poolAppId = uint64(p, "poolAppId", { positive: true });
  const sender = address(p, "sender");
  const c = await clammContext(ctx);
  const pool = await registeredPool(c, poolAppId);
  const senderClass = await verificationClassOf(c, sender);
  if (senderClass < Number(pool.verificationClass)) {
    throw new Error(`pool ${poolAppId} needs verification class ${pool.verificationClass}; ${sender} has ${senderClass}`);
  }
  return { c, pool, sender, client: new clamm.BiatecClammPoolClient({ algorand: c.algorand, appId: pool.appId }) };
}

export interface Simulation {
  ok: boolean;
  failureMessage?: string;
}

/** Simulates the unsigned group (empty signatures allowed) so a group that would fail never reaches a preview. */
export async function simulate(algod: Algodv2, txs: Transaction[]): Promise<Simulation> {
  const request = new algosdk.modelsv2.SimulateRequest({
    txnGroups: [new algosdk.modelsv2.SimulateRequestTransactionGroup({ txns: txs.map((txn) => new algosdk.SignedTransaction({ txn })) })],
    allowEmptySignatures: true,
    allowUnnamedResources: true,
  });
  const result = await algod.simulateTransactions(request).do();
  const failure = result.txnGroups[0]?.failureMessage;
  return failure ? { ok: false, failureMessage: failure } : { ok: true };
}

async function finish(prep: Prepared, kind: string, txs: Transaction[], action: Record<string, unknown>): Promise<BuildResult> {
  const [pool, simulation] = await Promise.all([poolFacts(prep.c, prep.pool), simulate(prep.c.algod, txs)]);
  return {
    transactions: txs.map((t) => Buffer.from(algosdk.encodeUnsignedTransaction(t)).toString("base64")),
    facts: { kind, sender: prep.sender, pool, ...action, txIds: txs.map((t) => t.txID()), simulation },
  };
}

function common(prep: Prepared) {
  return {
    params: prep.c.params,
    clientBiatecClammPool: prep.client,
    account: unsignedAccount(prep.sender),
    appBiatecConfigProvider: prep.c.apps.configAppId,
    appBiatecIdentityProvider: prep.c.apps.identityAppId,
    assetA: prep.pool.assetA,
    assetB: prep.pool.assetB,
  };
}

/** `clamm.liquidity.add`: deposit `amountA` and `amountB` (base units) into a pool; opts the sender into the LP token if needed. */
export async function addLiquidity(params: unknown, ctx: BuildContext): Promise<BuildResult> {
  const p = asParams(params);
  const amountA = uint64(p, "amountA");
  const amountB = uint64(p, "amountB");
  if (amountA === 0n && amountB === 0n) throw new Error("amountA or amountB must be greater than zero");
  const prep = await prepare(p, ctx);
  const info = await prep.c.algod.accountInformation(prep.sender).do();
  const optinSender = !info.assets?.some((a) => a.assetId === prep.pool.lpTokenId);
  const txs = await clamm.clammAddLiquidityTxs({
    ...common(prep),
    clientBiatecPoolProvider: new clamm.BiatecPoolProviderClient({ algorand: prep.c.algorand, appId: prep.c.apps.poolProviderAppId }),
    assetLp: prep.pool.lpTokenId,
    assetADeposit: amountA,
    assetBDeposit: amountB,
    fee: prep.pool.fee,
    verificationClass: BigInt(prep.pool.verificationClass),
    min: prep.pool.min,
    max: prep.pool.max,
    optinSender,
  });
  return finish(prep, "clamm.liquidity.add", txs, { amountA: amountA.toString(), amountB: amountB.toString(), lpOptIn: optinSender });
}

/** `clamm.liquidity.remove`: return `lpAmount` LP tokens (base units) to the pool for its share of both assets. */
export async function removeLiquidity(params: unknown, ctx: BuildContext): Promise<BuildResult> {
  const p = asParams(params);
  const lpAmount = uint64(p, "lpAmount", { positive: true });
  const prep = await prepare(p, ctx);
  const txs = await clamm.clammRemoveLiquidityTxs({ ...common(prep), assetLp: prep.pool.lpTokenId, lpToSend: lpAmount });
  return finish(prep, "clamm.liquidity.remove", txs, { lpAmount: lpAmount.toString() });
}

/** `clamm.swap.pool`: swap `fromAmount` of `fromAsset` (asset A or B of the pool) on one named pool. */
export async function swapOnPool(params: unknown, ctx: BuildContext): Promise<BuildResult> {
  const p = asParams(params);
  const fromAsset = uint64(p, "fromAsset");
  const fromAmount = uint64(p, "fromAmount", { positive: true });
  const minimumToReceive = uint64(p, "minimumToReceive");
  const prep = await prepare(p, ctx);
  if (fromAsset !== prep.pool.assetA && fromAsset !== prep.pool.assetB) {
    throw new Error(`fromAsset ${fromAsset} is not traded in pool ${prep.pool.appId}`);
  }
  const toAsset = fromAsset === prep.pool.assetA ? prep.pool.assetB : prep.pool.assetA;
  const txs = await clamm.clammSwapTxs({
    ...common(prep),
    appBiatecPoolProvider: prep.c.apps.poolProviderAppId,
    fromAsset,
    fromAmount,
    minimumToReceive,
  });
  return finish(prep, "clamm.swap.pool", txs, {
    fromAsset: fromAsset.toString(),
    fromAmount: fromAmount.toString(),
    toAsset: toAsset.toString(),
    minimumToReceive: minimumToReceive.toString(),
  });
}
