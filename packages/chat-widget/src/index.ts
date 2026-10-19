import { createApp, type App, type Plugin } from "vue";
import BiatecChatRoot from "./components/BiatecChatRoot.vue";
import { resolveOptions, type BiatecChatOptions } from "./options";
// Normal import: emitted as dist/style.css for hosts that mount BiatecChatRoot themselves.
import "./styles.css";
// Inline import: the same CSS as a string, injected into the widget's shadow root.
import widgetCss from "./styles.css?inline";

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
    // Shadow DOM: neither the host's CSS (PrimeVue, Tailwind preflight) nor the widget's leaks across.
    // Hosts restyle it only through CSS custom properties set on `host`.
    const shadow = host.attachShadow({ mode: "open" });
    const style = document.createElement("style");
    style.textContent = widgetCss;
    shadow.appendChild(style);
    const mountPoint = document.createElement("div");
    shadow.appendChild(mountPoint);
    createApp(BiatecChatRoot, { options: resolved, shadow: true }).mount(mountPoint);
  },
};
