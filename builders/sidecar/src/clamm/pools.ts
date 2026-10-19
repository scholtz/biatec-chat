import type { FullConfig } from "biatec-concentrated-liquidity-amm";
import type { BuildContext } from "../app.js";
import { clammContext, verificationClassOf, type ClammContext } from "./context.js";
import { clamm } from "./lib.js";
import { asParams, optionalAddress, uint64 } from "./params.js";

/** Biatec scales fees and prices by 1e9 (SCALE in the pool contract). */
export const SCALE = 1_000_000_000n;

/** A registered Biatec pool as the backend shows it: ids, fee tier, price range, liquidity, minimum class. */
export interface PoolFacts {
  appId: string;
  assetA: string;
  assetB: string;
  lpTokenId: string;
  /** Fee in SCALE units (1e9 = 100 %), and as a percentage for display. */
  fee: string;
  feePercent: string;
  /** Price range of asset A in asset B, in SCALE units. */
  priceMin: string;
  priceMax: string;
  currentPrice: string;
  liquidity: string;
  verificationClass: number;
}

/** Every pool registered at the network's pool provider. Only these app ids are ever built against. */
export async function registeredPools(c: ClammContext): Promise<FullConfig[]> {
  return clamm.getPools({ assetId: 0n, poolProviderAppId: c.apps.poolProviderAppId, algod: c.algod });
}

/** The registered pool with this app id, or an error: a caller-supplied app id is never trusted on its own. */
export async function registeredPool(c: ClammContext, appId: bigint): Promise<FullConfig> {
  const pool = (await registeredPools(c)).find((p) => p.appId === appId);
  if (!pool) throw new Error(`app ${appId} is not a Biatec pool on this network`);
  return pool;
}

export async function poolFacts(c: ClammContext, pool: FullConfig): Promise<PoolFacts> {
  const client = new clamm.BiatecClammPoolClient({ algorand: c.algorand, appId: pool.appId });
  const state = await client.state.global.getAll();
  return {
    appId: pool.appId.toString(),
    assetA: pool.assetA.toString(),
    assetB: pool.assetB.toString(),
    lpTokenId: pool.lpTokenId.toString(),
    fee: pool.fee.toString(),
    feePercent: formatScaled(pool.fee * 100n),
    priceMin: pool.min.toString(),
    priceMax: pool.max.toString(),
    currentPrice: (state.currentPrice ?? 0n).toString(),
    liquidity: (state.liquidity ?? 0n).toString(),
    verificationClass: Number(pool.verificationClass),
  };
}

/** SCALE-unit value as a decimal string, for example 1000000n → "0.001". */
export function formatScaled(v: bigint): string {
  const whole = v / SCALE;
  const frac = (v % SCALE).toString().padStart(9, "0").replace(/0+$/, "");
  return frac ? `${whole}.${frac}` : whole.toString();
}

/**
 * The pools of a pair (either order), split into those an account of `accountClass` may use and those it may not
 * (a pool's verificationClass is the minimum class it accepts; no account given means nothing is excluded).
 */
export function selectPools(all: FullConfig[], assetA: bigint, assetB: bigint, accountClass?: number) {
  const pair = all.filter((x) => (x.assetA === assetA && x.assetB === assetB) || (x.assetA === assetB && x.assetB === assetA));
  const eligible = pair.filter((x) => accountClass === undefined || Number(x.verificationClass) <= accountClass);
  const excluded = pair
    .filter((x) => !eligible.includes(x))
    .map((x) => ({ appId: x.appId.toString(), verificationClass: Number(x.verificationClass), reason: "verification_class" }));
  return { eligible, excluded };
}

/**
 * `clamm.pools.list`: the pools for an asset pair (either order), with fee tier, price range and liquidity. With an
 * `account`, pools whose minimum verification class the account doesn't meet are left out and listed under
 * `excluded`, so the assistant never offers a pool the account can't use and can explain how to qualify.
 */
export async function listPools(params: unknown, ctx: BuildContext) {
  const p = asParams(params);
  const assetA = uint64(p, "assetA");
  const assetB = uint64(p, "assetB");
  const account = optionalAddress(p, "account");
  const c = await clammContext(ctx);

  const accountClass = account ? await verificationClassOf(c, account) : undefined;
  const { eligible, excluded } = selectPools(await registeredPools(c), assetA, assetB, accountClass);

  const pools = await Promise.all(eligible.map((x) => poolFacts(c, x)));
  pools.sort((a, b) => Number(BigInt(a.fee) - BigInt(b.fee)) || Number(BigInt(a.priceMin) - BigInt(b.priceMin)));
  return {
    network: ctx.network,
    apps: {
      configAppId: c.apps.configAppId.toString(),
      identityAppId: c.apps.identityAppId.toString(),
      poolProviderAppId: c.apps.poolProviderAppId.toString(),
    },
    ...(accountClass === undefined ? {} : { account, accountVerificationClass: accountClass }),
    pools,
    excluded,
  };
}
