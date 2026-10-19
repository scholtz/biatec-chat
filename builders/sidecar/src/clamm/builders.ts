import type { Algodv2, Transaction } from "algosdk";
import type { BiatecClammPoolClient, FullConfig } from "biatec-concentrated-liquidity-amm";
import type { BuildContext, BuildResult } from "../app.js";
import { clammContext, identityOf, unsignedAccount, type ClammContext } from "./context.js";
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
  const [pool, identity] = await Promise.all([registeredPool(c, poolAppId), identityOf(c, sender)]);
  if (identity.isLocked) throw new Error(`the Biatec identity of ${sender} is locked`);
  if (identity.verificationClass < Number(pool.verificationClass)) {
    throw new Error(`pool ${poolAppId} needs verification class ${pool.verificationClass}; ${sender} has ${identity.verificationClass}`);
  }
  return { c, pool, sender, client: new clamm.BiatecClammPoolClient({ algorand: c.algorand, appId: pool.appId }) };
}

export interface Simulation {
  ok: boolean;
  failureMessage?: string;
}

/**
 * Simulates the unsigned group with empty signatures and only the resources the group names itself, as the real
 * submission will; a group that would fail is refused here and never reaches a preview.
 */
export async function simulate(algod: Algodv2, txs: Transaction[]): Promise<Simulation> {
  const request = new algosdk.modelsv2.SimulateRequest({
    txnGroups: [new algosdk.modelsv2.SimulateRequestTransactionGroup({ txns: txs.map((txn) => new algosdk.SignedTransaction({ txn })) })],
    allowEmptySignatures: true,
  });
  const result = await algod.simulateTransactions(request).do();
  const failure = result.txnGroups[0]?.failureMessage;
  return failure ? { ok: false, failureMessage: failure } : { ok: true };
}

async function finish(prep: Prepared, kind: string, txs: Transaction[], action: Record<string, unknown>): Promise<BuildResult> {
  const [pool, simulation] = await Promise.all([poolFacts(prep.c, prep.pool), simulate(prep.c.algod, txs)]);
  if (!simulation.ok) throw new Error(`the group fails in simulation: ${simulation.failureMessage}`);
  return {
    transactions: txs.map((t) => Buffer.from(algosdk.encodeUnsignedTransaction(t)).toString("base64")),
    facts: {
      kind, sender: prep.sender, pool, ...action, txIds: txs.map((t) => t.txID()), simulation,
      // The group is valid only in these rounds; the backend rebuilds it if signing would come later.
      firstValid: prep.c.params.firstValid.toString(), lastValid: prep.c.params.lastValid.toString(),
    },
  };
}

/** Whether the account is opted into the asset (one bounded lookup; 404 means it isn't). */
async function holdsAsset(c: ClammContext, account: string, assetId: bigint): Promise<boolean> {
  try {
    await c.algod.accountAssetInformation(account, assetId).do();
    return true;
  } catch (e) {
    if ((e as { response?: { status?: number } }).response?.status === 404) return false;
    throw e;
  }
}

/** The sender must be able to receive what the pool pays out; a clear refusal beats a logic error from simulation. */
async function requireOptedIn(prep: Prepared, assets: bigint[]): Promise<void> {
  for (const asset of assets) {
    if (asset !== 0n && !(await holdsAsset(prep.c, prep.sender, asset))) {
      throw new Error(`${prep.sender} is not opted in to asset ${asset}`);
    }
  }
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
  const optinSender = !(await holdsAsset(prep.c, prep.sender, prep.pool.lpTokenId));
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
  await requireOptedIn(prep, [prep.pool.assetA, prep.pool.assetB]);
  const txs = await clamm.clammRemoveLiquidityTxs({ ...common(prep), assetLp: prep.pool.lpTokenId, lpToSend: lpAmount });
  return finish(prep, "clamm.liquidity.remove", txs, { lpAmount: lpAmount.toString() });
}

/** `clamm.swap.pool`: swap `fromAmount` of `fromAsset` (asset A or B of the pool) on one named pool. */
export async function swapOnPool(params: unknown, ctx: BuildContext): Promise<BuildResult> {
  const p = asParams(params);
  const fromAsset = uint64(p, "fromAsset");
  const fromAmount = uint64(p, "fromAmount", { positive: true });
  // Required and positive: a swap that accepts any output has no slippage protection.
  const minimumToReceive = uint64(p, "minimumToReceive", { positive: true });
  const prep = await prepare(p, ctx);
  if (fromAsset !== prep.pool.assetA && fromAsset !== prep.pool.assetB) {
    throw new Error(`fromAsset ${fromAsset} is not traded in pool ${prep.pool.appId}`);
  }
  const toAsset = fromAsset === prep.pool.assetA ? prep.pool.assetB : prep.pool.assetA;
  await requireOptedIn(prep, [toAsset]);
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
