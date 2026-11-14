import { defineConfig } from "vitest/config";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { serverUrl } from "./src/needs.ts";

const here = dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  root: here,
  test: {
    include: ["tests/**/*.test.ts"],
    // tests/report.test.ts starts its own stub server and nested run: it never runs against an external server (CONFORMANCE_OIDC_URL),
    // so its cases can never be mistaken for results of that server, and never inside its own nested run.
    exclude: serverUrl() || process.env.CONFORMANCE_NESTED ? ["tests/report.test.ts"] : [],
    globalSetup: ["./src/global-setup.ts"],
    testTimeout: 15_000,
    hookTimeout: 15_000,
    // "default" on the console, one redacted report file per run (src/report.ts), plus the vitest JSON and JUnit files for the
    // in-process mock only (never when CONFORMANCE_OIDC_URL is set, whatever the server): those carry raw failure messages, which against a real server could echo a token.
    reporters: serverUrl() ? ["default", "./src/report.ts"] : ["default", "./src/report.ts", "json", "junit"],
    outputFile: {
      json: join(here, "report", "conformance.json"),
      junit: join(here, "report", "conformance.junit.xml"),
    },
  },
});
