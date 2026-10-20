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
  // fresh checkout without a prior build.
  test: { alias: { "@biatec/chat-widget": fileURLToPath(new URL("./src/index.ts", import.meta.url)) }, environment: "jsdom", include: ["test/**/*.test.ts"], css: { include: [/.+/] } },
});
