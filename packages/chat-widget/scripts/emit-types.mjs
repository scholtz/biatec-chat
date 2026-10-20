// Generates declarations from the source: options.d.ts, api/types.d.ts and standalone/types.d.ts come from the
// source via tsc, so the public types cannot drift. index.d.ts and standalone/index.d.ts only re-export them and
// declare the plugin, components and functions. The published types must be self-contained: nothing may import
// @biatec/chat-protocol (or any other workspace package), which is not published; the check at the end enforces it.
import { writeFileSync, mkdirSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { execFileSync } from "node:child_process";
import { createRequire } from "node:module";

const tsc = createRequire(import.meta.url).resolve("typescript/bin/tsc");
mkdirSync("dist/standalone", { recursive: true });
execFileSync(
  process.execPath,
  [
    tsc,
    "src/options.ts",
    "src/api/types.ts",
    "src/standalone/types.ts",
    "--declaration",
    "--emitDeclarationOnly",
    "--rootDir",
    "src",
    "--outDir",
    "dist",
    "--skipLibCheck",
    "--target",
    "es2020",
    "--lib",
    "es2020,dom",
    "--moduleResolution",
    "bundler",
    "--module",
    "esnext",
    "--strict",
  ],
  { stdio: "inherit" }
);
writeFileSync(
  "dist/index.d.ts",
  `import type { Plugin, Component } from "vue";
import type { BiatecChatOptions } from "./options";
import type { BiatecChatApi } from "./api/types";
export type { BiatecChatOptions, ChatTheme, ChatMode } from "./options";
export type { AskResult, BiatecChatApi } from "./api/types";
export declare const BiatecChatRoot: Component;
/** The widget as a panel in the host's layout; shares the floating widget's session, which hides meanwhile. */
export declare const BiatecChatInline: Component;
/** The 16 px Biatec AI chip symbol; props: size (default 32), variant ("default" | "small" | "mono-dark"). */
export declare const BiatecAiChip: Component;
export declare const BiatecChat: Plugin<[BiatecChatOptions]>;
/** Host API: open, close, ask, setPageContext, setTheme, setLocale. */
export declare function useBiatecChat(): BiatecChatApi;
`
);
// No Vue types here: the standalone entry bundles Vue, so React and plain-JS hosts type-check without it.
writeFileSync(
  "dist/standalone/index.d.ts",
  `import type { BiatecChatApi } from "../api/types";
import type { BiatecChatHandle, MountBiatecChatOptions } from "./types";
export type { BiatecChatOptions, ChatTheme, ChatMode } from "../options";
export type { AskResult, BiatecChatApi } from "../api/types";
export type { BiatecChatHandle, MountBiatecChatOptions } from "./types";
/** \`<html>\` attribute the standalone build treats as dark by default (\`theme: 'host'\`). */
export declare const DEFAULT_HOST_DARK_ATTRIBUTE = "data-theme=dark";
/**
 * Mounts the floating widget and returns the host API plus \`unmount()\`. Browser only; without a DOM it does nothing.
 * Defaults differ from the plugin: \`theme: 'host'\` following \`<html data-theme="dark">\`, and \`locale\` from \`<html lang>\`.
 */
export declare function mountBiatecChat(options: MountBiatecChatOptions): BiatecChatHandle;
export declare function mountBiatecChat(container: Element | string, options: MountBiatecChatOptions): BiatecChatHandle;
/** Removes the widget mounted by \`mountBiatecChat\` or by the script-tag auto-mount. */
export declare function unmount(): void;
/** Mounts from a script tag's \`data-*\` attributes (what the IIFE build does with its own tag). */
export declare function autoMount(script: HTMLOrSVGScriptElement | null | undefined): void;
/** Host API: open, close, ask, setPageContext, setTheme, setLocale. */
export declare function useBiatecChat(): BiatecChatApi;
`
);

// Self-contained check: every import in the published declarations is relative or "vue".
const bad = [];
const walk = (dir) => {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, e.name);
    if (e.isDirectory()) walk(p);
    else if (p.endsWith(".d.ts")) {
      for (const m of readFileSync(p, "utf8").matchAll(/from\s+["']([^"']+)["']|import\(\s*["']([^"']+)["']\s*\)/g)) {
        const spec = m[1] ?? m[2];
        if (!spec.startsWith(".") && spec !== "vue") bad.push(`${p}: ${spec}`);
      }
    }
  }
};
walk("dist");
if (bad.length) {
  console.error("emit-types: published declarations import packages that are not dependencies:\n  " + bad.join("\n  "));
  process.exit(1);
}
