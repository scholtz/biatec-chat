import { defineConfig } from "vitest/config";
import vue from "@vitejs/plugin-vue";

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
  test: { environment: "jsdom", include: ["test/**/*.test.ts"], css: { include: [/.+/] } },
});
