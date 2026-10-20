// Generates declarations from the source: options.d.ts comes from src/options.ts via tsc, so the public option
// types cannot drift. index.d.ts only re-exports them and declares the plugin and root component.
import { writeFileSync, mkdirSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { createRequire } from "node:module";

const tsc = createRequire(import.meta.url).resolve("typescript/bin/tsc");
mkdirSync("dist", { recursive: true });
execFileSync(
  process.execPath,
  [tsc, "src/options.ts", "src/api/types.ts", "--declaration", "--emitDeclarationOnly", "--outDir", "dist", "--skipLibCheck", "--target", "es2020", "--moduleResolution", "bundler", "--module", "esnext", "--strict"],
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
