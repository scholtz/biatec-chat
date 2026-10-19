// Generates declarations from the source: options.d.ts comes from src/options.ts via tsc, so the public option
// types cannot drift. index.d.ts only re-exports them and declares the plugin and root component.
import { writeFileSync, mkdirSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { createRequire } from "node:module";

const tsc = createRequire(import.meta.url).resolve("typescript/bin/tsc");
mkdirSync("dist", { recursive: true });
execFileSync(
  process.execPath,
  [tsc, "src/options.ts", "--declaration", "--emitDeclarationOnly", "--outDir", "dist", "--skipLibCheck", "--target", "es2020", "--moduleResolution", "bundler", "--module", "esnext", "--strict"],
  { stdio: "inherit" }
);
writeFileSync(
  "dist/index.d.ts",
  `import type { Plugin, Component } from "vue";
import type { BiatecChatOptions } from "./options";
export type { BiatecChatOptions, ChatTheme } from "./options";
export declare const BiatecChatRoot: Component;
export declare const BiatecChat: Plugin<[BiatecChatOptions]>;
`
);
