/// <reference types="vitest/config" />
import { defineConfig, loadEnv } from "vite";
import vue from "@vitejs/plugin-vue";

// The dashboard is served by the backend at /admin/ (docs/admin-dashboard.md), hence base. In dev, Vite listens on 5174
// and proxies the API and the sign-in redirect to the backend (VITE_BACKEND_URL, default http://localhost:5000).
export default defineConfig(({ mode, command }) => {
  const env = loadEnv(mode, process.cwd(), "VITE_");
  const target = env.VITE_BACKEND_URL?.trim() || "http://localhost:5000";
  const proxy = { target, changeOrigin: false, secure: false };
  return {
    base: command === "serve" ? "/admin/" : "./",
    plugins: [vue()],
    server: { port: 5174, strictPort: true, proxy: { "/admin/api": proxy, "/admin/auth": proxy } },
    build: { outDir: "dist", sourcemap: false },
    test: { environment: "jsdom", include: ["test/**/*.test.ts"] },
  };
});
