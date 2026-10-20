// Standalone build (`@biatec/chat-widget/standalone`): Vue and every dependency bundled, for hosts that are not Vue
// apps. ES module for bundlers (Docusaurus/webpack, Vite) and a minified IIFE for one <script> tag (global
// `BiatecChat`). Runs after the plugin build and writes next to it in dist/, so it must not empty the folder.
import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

export default defineConfig({
  plugins: [vue()],
  // Library mode leaves process.env.NODE_ENV for the consumer's bundler; a script tag has none, so fix it here, along
  // with Vue's and vue-i18n's compile-time feature flags (production, no devtools).
  define: {
    "process.env.NODE_ENV": JSON.stringify("production"),
    __VUE_OPTIONS_API__: "true",
    __VUE_PROD_DEVTOOLS__: "false",
    __VUE_PROD_HYDRATION_MISMATCH_DETAILS__: "false",
    __VUE_I18N_FULL_INSTALL__: "false",
    __VUE_I18N_LEGACY_API__: "false",
    __INTLIFY_PROD_DEVTOOLS__: "false",
  },
  build: {
    emptyOutDir: false,
    minify: "esbuild",
    // The widget CSS is injected into the shadow root as a string; no stylesheet file for this entry.
    cssCodeSplit: false,
    lib: {
      entry: "src/standalone/index.ts",
      name: "BiatecChat",
      formats: ["es", "iife"],
      fileName: (format) => (format === "es" ? "standalone/biatec-chat.standalone.js" : "standalone/biatec-chat.standalone.iife.js"),
      cssFileName: "standalone/unused",
    },
  },
});
