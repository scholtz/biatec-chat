import { serve } from "@hono/node-server";
import { createApp } from "./app.js";
import { loadConfig } from "./config.js";
import { safeBuilders } from "./safe/builders.js";

const config = loadConfig();
serve({ fetch: createApp(config, { ...safeBuilders() }).fetch, port: config.port });
console.log(`builders sidecar listening on :${config.port}`);
