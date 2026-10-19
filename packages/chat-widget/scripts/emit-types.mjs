// Writes a small declaration entry so consumers get types without a vue-tsc step.
import { writeFileSync, mkdirSync } from "node:fs";

mkdirSync("dist", { recursive: true });
writeFileSync(
  "dist/index.d.ts",
  `import type { Plugin } from "vue";
export type ChatTheme = "auto" | "light" | "dark";
export interface BiatecChatOptions {
  backendUrl: string;
  /** Identifies the host, not a secret. */
  clientKey: string;
  locale?: string;
  theme?: ChatTheme;
  suggestions?: "default" | string[];
  /** Opt-in page context string. Nothing is read from the host page unless this is set. */
  pageContext?: string;
  /** Mount the widget into document.body automatically (default true). */
  autoMount?: boolean;
}
export declare const BiatecChat: Plugin<[BiatecChatOptions]>;
`
);
