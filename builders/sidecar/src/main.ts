import { serve } from "@hono/node-server";
import { createApp } from "./app.js";
import { clammBuilders, clammQueries } from "./clamm/index.js";
import { loadConfig } from "./config.js";
import { safeBuilders } from "./safe/builders.js";
import { x402Builders, x402Enabled } from "./x402/builders.js";

const config = loadConfig();
serve({ fetch: createApp(config, { ...clammBuilders, ...safeBuilders(), ...(x402Enabled() ? x402Builders() : {}) }, clammQueries).fetch, port: config.port });
console.log(`builders sidecar listening on :${config.port}`);
