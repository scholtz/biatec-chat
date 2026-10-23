import { defineConfig } from "vitest/config";
import vue from "@vitejs/plugin-vue";
import { fileURLToPath } from "node:url";

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
  test: {
    // A string key "@/" would only match imports like "@//x" (the alias matcher wants a "/" right after the key), so `@/i18n` needs the regular expression.
    alias: [
      { find: "@biatec/chat-widget", replacement: fileURLToPath(new URL("./src/index.ts", import.meta.url)) },
      { find: /^@\//, replacement: fileURLToPath(new URL("./playground/dex/host", import.meta.url)) + "/" },
    ],
    environment: "jsdom", setupFiles: ["./test/setup-storage.ts"], include: ["test/**/*.test.ts"], css: { include: [/.+/] } },
});
