import { createApp, type App, type Plugin } from "vue";
import BiatecChatRoot from "./components/BiatecChatRoot.vue";
import { resolveOptions, type BiatecChatOptions } from "./options";
import "./styles.css";

export type { BiatecChatOptions, ChatTheme } from "./options";
export { BiatecChatRoot };

export const BiatecChat: Plugin<[BiatecChatOptions]> = {
  install(app: App, options: BiatecChatOptions) {
    const resolved = resolveOptions(options);
    app.provide("biatec-chat-options", resolved);
    if (!resolved.autoMount || typeof document === "undefined") return;
    // Separate Vue app so the widget never touches the host's component tree or root element.
    // Idempotent: a second install (HMR, two apps) reuses the existing widget instead of adding another.
    if (document.querySelector("[data-biatec-chat-host]")) return;
    const host = document.createElement("div");
    host.setAttribute("data-biatec-chat-host", "");
    document.body.appendChild(host);
    createApp(BiatecChatRoot, { options: resolved }).mount(host);
  },
};
