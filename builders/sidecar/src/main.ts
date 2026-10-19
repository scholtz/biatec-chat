import { serve } from "@hono/node-server";
import { createApp } from "./app.js";
import { clammBuilders, clammQueries } from "./clamm/index.js";
import { loadConfig } from "./config.js";

const config = loadConfig();
serve({ fetch: createApp(config, { ...clammBuilders }, { ...clammQueries }).fetch, port: config.port });
console.log(`builders sidecar listening on :${config.port}`);
