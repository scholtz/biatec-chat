import { defineConfig } from "vitest/config";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  root: here,
  test: {
    include: ["tests/**/*.test.ts"],
    globalSetup: ["./src/global-setup.ts"],
    testTimeout: 15_000,
    hookTimeout: 15_000,
    // "default" on the console, plus machine-readable reports for CI or for sending to a vendor.
    reporters: ["default", "json", "junit"],
    outputFile: {
      json: join(here, "report", "conformance.json"),
      junit: join(here, "report", "conformance.junit.xml"),
    },
  },
});
