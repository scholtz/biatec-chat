import { defineConfig } from "vitest/config";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { serverUrl } from "./src/needs.ts";

const here = dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  root: here,
  test: {
    include: ["tests/**/*.test.ts"],
    // The nested run of tests/report.test.ts must not run that test again.
    exclude: process.env.CONFORMANCE_NESTED ? ["tests/report.test.ts"] : [],
    globalSetup: ["./src/global-setup.ts"],
    testTimeout: 15_000,
    hookTimeout: 15_000,
    // "default" on the console, one redacted report file per run (src/report.ts), plus the vitest JSON and JUnit files for the
    // in-process mock only: those carry raw failure messages, which against a real server could echo a token.
    reporters: serverUrl() && process.env.CONFORMANCE_MOCK_HELPERS !== "1" ? ["default", "./src/report.ts"] : ["default", "./src/report.ts", "json", "junit"],
    outputFile: {
      json: join(here, "report", "conformance.json"),
      junit: join(here, "report", "conformance.junit.xml"),
    },
  },
});
