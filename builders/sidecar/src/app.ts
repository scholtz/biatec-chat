import { timingSafeEqual } from "node:crypto";
import { Hono } from "hono";
import type { Config } from "./config.js";

/** Context a builder receives. Builders never see the secret. */
export interface BuildContext {
  network: string;
  algodUrl: string;
}

export interface BuildResult {
  /** Unsigned transactions as base64 msgpack, in group order. */
  transactions: string[];
  /** Decoded preview of what the group does, JSON-safe (amounts as decimal strings). */
  preview?: unknown;
  /** Read results for builders that return data instead of, or next to, a group. */
  data?: unknown;
  /** algod simulate outcome when the caller asked for it. */
  simulation?: unknown;
}

/** A builder turns a JSON request into an unsigned group. Specific builders (CLAMM, algo-safe) register here. */
export type Builder = (params: unknown, ctx: BuildContext) => Promise<BuildResult>;

export const SECRET_HEADER = "x-internal-secret";

function secretMatches(provided: string | undefined, expected: string): boolean {
  if (!provided) return false;
  const a = Buffer.from(provided);
  const b = Buffer.from(expected);
  return a.length === b.length && timingSafeEqual(a, b);
}

const BASE64 = /^[A-Za-z0-9+/]+={0,2}$/;

export function createApp(config: Config, builders: Record<string, Builder> = {}): Hono {
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

  app.post("/v1/build/:builder", async (c) => {
    const builder = Object.hasOwn(builders, c.req.param("builder")) ? builders[c.req.param("builder")] : undefined;
    if (!builder) return c.json({ error: "unknown_builder" }, 404);

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
      const result = await builder(body.params, { network, algodUrl });
      if (!Array.isArray(result.transactions) || result.transactions.some((t) => typeof t !== "string" || !BASE64.test(t))) {
        return c.json({ error: "builder_output_invalid" }, 502);
      }
      return c.json(result);
    } catch (e) {
      // Message only, never the stack, and nothing from the request.
      return c.json({ error: "build_failed", message: e instanceof Error ? e.message : "unknown" }, 422);
    }
  });

  return app;
}
