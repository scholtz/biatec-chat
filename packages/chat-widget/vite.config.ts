import { defineConfig } from "vitest/config";
import vue from "@vitejs/plugin-vue";
import { fileURLToPath } from "node:url";
import { normalizePath } from "vite";

export default defineConfig({
  plugins: [vue()],
  build: {
    lib: {
      entry: "src/index.ts",
      name: "BiatecChat",
      fileName: "biatec-chat",
      formats: ["es", "umd"],
      cssFileName: "style",
    },
    rollupOptions: {
      external: ["vue"],
      output: { globals: { vue: "Vue" } },
    },
  },
  // Tests (and the playground host modules they import) resolve the package by name to the source, so they run on a
  // fresh checkout without a prior build. `@/` is the DEX page's host modules, as in playground/vite.config.ts.
  // A regex alias with a forward-slash target: the string key "@/" with a Windows path (backslashes) did not resolve (BCHAT-045).
  test: {
    alias: [
      // Exact package name (not a prefix) and forward slashes in both targets, so a Windows path cannot break either.
      { find: /^@biatec\/chat-widget$/, replacement: normalizePath(fileURLToPath(new URL("./src/index.ts", import.meta.url))) },
      { find: /^@\//, replacement: normalizePath(fileURLToPath(new URL("./playground/dex/host", import.meta.url))) + "/" },
    ],
    environment: "jsdom",
    setupFiles: ["test/setup.ts"],
    include: ["test/**/*.test.ts"],
    css: { include: [/.+/] },
  },
});
