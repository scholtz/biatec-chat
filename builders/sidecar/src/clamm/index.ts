import type { Builder, Query } from "../app.js";
import { addLiquidity, removeLiquidity, swapOnPool } from "./builders.js";
import { listPools } from "./pools.js";

/** CLAMM builders, served at POST /v1/build/{name}. */
export const clammBuilders: Record<string, Builder> = {
  "clamm.liquidity.add": addLiquidity,
  "clamm.liquidity.remove": removeLiquidity,
  "clamm.swap.pool": swapOnPool,
};

/** CLAMM read-only queries, served at POST /v1/query/{name}. */
export const clammQueries: Record<string, Query> = {
  "clamm.pools.list": listPools,
};
