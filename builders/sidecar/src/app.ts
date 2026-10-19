import { timingSafeEqual } from "node:crypto";
import { Hono, type Context } from "hono";
import type { Config } from "./config.js";

/** Context a builder receives. Builders never see the secret. */
export interface BuildContext {
  network: string;
  algodUrl: string;
}

export interface BuildResult {
  /** Unsigned transactions as base64 msgpack, in group order. */
  transactions: string[];
  /** What the group does, decoded by the builder (pool, amounts, simulation), for the action preview. */
  facts?: Record<string, unknown>;
}

/** A builder turns a JSON request into an unsigned group. Specific builders (CLAMM, algo-safe) register here. */
export type Builder = (params: unknown, ctx: BuildContext) => Promise<BuildResult>;

/** A query reads chain state for the backend (for example the pools of a pair) and builds nothing. */
export type Query = (params: unknown, ctx: BuildContext) => Promise<Record<string, unknown>>;

export const SECRET_HEADER = "x-internal-secret";

function secretMatches(provided: string | undefined, expected: string): boolean {
  if (!provided) return false;
  const a = Buffer.from(provided);
  const b = Buffer.from(expected);
  return a.length === b.length && timingSafeEqual(a, b);
}

const BASE64 = /^[A-Za-z0-9+/]+={0,2}$/;

class InvalidOutput extends Error {}

/** bigint (Algorand amounts and ids) as a decimal string; JSON.stringify can't serialise it otherwise. */
const jsonSafe = (value: unknown): unknown => JSON.parse(JSON.stringify(value, (_, v) => (typeof v === "bigint" ? v.toString() : v)));

export function createApp(config: Config, builders: Record<string, Builder> = {}, queries: Record<string, Query> = {}): Hono {
  const app = new Hono();

  // Liveness only: no data, so it stays open for the container healthcheck.
  app.get("/health", (c) => c.json({ status: "ok" }));

  app.use("/v1/*", async (c, next) => {
    if (!secretMatches(c.req.header(SECRET_HEADER), config.internalSecret)) {
      return c.json({ error: "unauthorized" }, 401);
    }
    await next();
  });

  app.get("/v1/networks", (c) => c.json({ networks: Object.keys(config.algod).sort() }));

  /** Parses `{ network, params }`, resolves the network's algod and runs `run`; maps failures the same way for every route. */
  const handle = async (c: Context, run: (params: unknown, ctx: BuildContext) => Promise<unknown>, failed: string) => {
    let body: { network?: unknown; params?: unknown };
    try {
      body = await c.req.json();
    } catch {
      return c.json({ error: "invalid_json" }, 400);
    }
    const network = typeof body?.network === "string" ? body.network.toLowerCase() : "";
    const algodUrl = Object.hasOwn(config.algod, network) ? config.algod[network] : undefined;
    if (!algodUrl) return c.json({ error: "unknown_network" }, 400);
    try {
      return c.json(jsonSafe(await run(body.params, { network, algodUrl })) as object);
    } catch (e) {
      if (e instanceof InvalidOutput) return c.json({ error: "builder_output_invalid" }, 502);
      // Message only, never the stack, and nothing from the request.
      return c.json({ error: failed, message: e instanceof Error ? e.message : "unknown" }, 422);
    }
  };

  app.post("/v1/build/:builder", async (c) => {
    const builder = Object.hasOwn(builders, c.req.param("builder")) ? builders[c.req.param("builder")] : undefined;
    if (!builder) return c.json({ error: "unknown_builder" }, 404);
    return handle(c, async (params, ctx) => {
      const result = await builder(params, ctx);
      if (!Array.isArray(result.transactions) || result.transactions.some((t) => typeof t !== "string" || !BASE64.test(t))) {
        throw new InvalidOutput();
      }
      return result;
    }, "build_failed");
  });

  app.post("/v1/query/:query", async (c) => {
    const query = Object.hasOwn(queries, c.req.param("query")) ? queries[c.req.param("query")] : undefined;
    if (!query) return c.json({ error: "unknown_query" }, 404);
    return handle(c, query, "query_failed");
  });

  return app;
}
