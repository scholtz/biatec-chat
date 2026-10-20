/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import tailwindcss from "@tailwindcss/vite";
import { fileURLToPath } from "node:url";

const here = (p: string) => fileURLToPath(new URL(p, import.meta.url));

// Showcase site for the widget (docs/demo-site.md). It consumes @biatec/chat-widget from the workspace exactly as a
// host consumes it from npm (the package's dist), so build chat-protocol and chat-widget first.
//
// Backend and client key: `vite dev` talks to the local backend (docker-compose + `dotnet run`, dev tenant "localhost":
// origin http://localhost:5173, client key "dev"); `vite build` targets stage unless the shell overrides them. An empty
// value counts as unset. The client key is a label, not a secret; nothing else is baked into the bundle.
export default defineConfig(({ command }) => {
  const dev = command === "serve";
  if (!process.env.VITE_BIATEC_CHAT_URL?.trim()) {
    process.env.VITE_BIATEC_CHAT_URL = dev ? "http://localhost:5000" : "https://stage.chat.biatec.io";
  }
  if (!process.env.VITE_BIATEC_CHAT_CLIENT_KEY?.trim()) {
    process.env.VITE_BIATEC_CHAT_CLIENT_KEY = dev ? "dev" : "biatec-demo";
  }
  return {
    root: here("."),
    envPrefix: ["VITE_", "PRIMEVUE_"],
    plugins: [vue(), tailwindcss()],
    resolve: {
      // The reused DEX page lives under packages/chat-widget, whose node_modules hold their own links to these.
      dedupe: ["vue", "vue-i18n", "primevue", "@primeuix/themes", "@biatec/chat-widget"],
      alias: {
        // dex.html reuses the widget playground's BiatecDEX-like page (T-068) instead of copying it; its host
        // modules import `@/i18n`, `@/theme` and `@/biatecChat`.
        "@/": here("../../packages/chat-widget/playground/dex/host/"),
      },
    },
    // The local dev tenant only allows the origin http://localhost:5173.
    server: { port: 5173, strictPort: true },
    build: {
      sourcemap: false,
      // The DEX page bundles PrimeVue (about 530 kB); it is a separate entry, so the main page does not load it.
      chunkSizeWarningLimit: 600,
      rollupOptions: { input: { index: here("index.html"), dex: here("dex.html") } },
    },
    test: { environment: "jsdom", include: ["test/**/*.test.ts"] },
  };
});
