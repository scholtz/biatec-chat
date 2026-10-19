import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import tailwindcss from "@tailwindcss/vite";
import { fileURLToPath } from "node:url";

const here = (p: string) => fileURLToPath(new URL(p, import.meta.url));

// The DEX page reads the backend URL like Biatec DEX would (docs/integration-biatec-dex.md, step 1). Locally it is
// the backend from docker-compose unless the shell sets VITE_BIATEC_CHAT_URL.
process.env.VITE_BIATEC_CHAT_URL ??= "http://localhost:5000";

export default defineConfig({
  root: here("."),
  // PRIMEVUE_LICENSE (optional, from the shell or playground/.env.local) removes PrimeVue 5's licence banner on dex.html.
  envPrefix: ["VITE_", "PRIMEVUE_"],
  plugins: [vue(), tailwindcss()],
  resolve: {
    alias: {
      // dex.html imports the package by name, like a host does; in dev that is the source, so edits hot-reload.
      // `pnpm typecheck:playground` checks the same files against the built dist/index.d.ts instead.
      "@biatec/chat-widget": here("../src/index.ts"),
      // The BiatecDEX-like host modules the hand-off snippets import (`@/i18n`).
      "@/": here("./dex/host/"),
    },
  },
});
