import type { App, Plugin } from "vue";
import BiatecChatRoot from "./components/BiatecChatRoot.vue";
import BaiSymbol from "./ui/BaiSymbol.vue";
import BiatecChatInline from "./components/shell/BiatecChatInline.vue";
import { resolveOptions, type BiatecChatOptions } from "./options";
import { mountWidget } from "./standalone/mount";
// Normal import: emitted as dist/style.css for hosts that mount BiatecChatRoot themselves. The shadow root gets the
// same CSS as a string (see standalone/mount.ts).
import "./styles.css";

export type { BiatecChatOptions, ChatTheme, ChatMode } from "./options";
export type { AskResult, BiatecChatApi } from "./api";
export { useBiatecChat } from "./api";
/** The 16 px Biatec AI chip symbol, for "Ask Biatec AI" links on host pages. */
export const BiatecAiChip = BaiSymbol;
export { BiatecChatRoot, BiatecChatInline };

export const BiatecChat: Plugin<[BiatecChatOptions]> = {
  install(app: App, options: BiatecChatOptions) {
    const resolved = resolveOptions(options);
    app.provide("biatec-chat-options", resolved);
    if (!resolved.autoMount) return;
    // Separate Vue app in its own shadow root; a second install (HMR, two apps) reuses the existing widget.
    mountWidget(resolved);
  },
};
