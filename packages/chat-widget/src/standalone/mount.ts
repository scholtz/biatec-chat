import { createApp, type App } from "vue";
import BiatecChatRoot from "../components/BiatecChatRoot.vue";
import type { ResolvedOptions } from "../options";
// The widget CSS as a string, injected into the widget's shadow root (no host stylesheet needed).
import widgetCss from "../styles.css?inline";

export interface MountedWidget {
  host: HTMLElement;
  app: App;
}

/** The floating widget currently in the page, if any. */
let mounted: MountedWidget | null = null;

/**
 * Mounts the floating widget (the plugin root, BiatecChatRoot) into a `<div data-biatec-chat-host>` appended to
 * `container` (default `document.body`), as its own Vue app in an open shadow root. Shared by the Vue plugin's
 * auto-mount and the standalone `mountBiatecChat()`, so both render the very same component.
 * Idempotent: when a widget is already in the page it is returned instead of adding another.
 */
export function mountWidget(options: ResolvedOptions, container?: Element): MountedWidget | null {
  if (typeof document === "undefined") return null;
  if (mounted && mounted.host.isConnected) return mounted;
  if (document.querySelector("[data-biatec-chat-host]")) return null;
  const host = document.createElement("div");
  host.setAttribute("data-biatec-chat-host", "");
  (container ?? document.body).appendChild(host);
  // Shadow DOM: neither the host's CSS (PrimeVue, Tailwind preflight) nor the widget's leaks across.
  // Hosts restyle it only through CSS custom properties set on `host`.
  const shadow = host.attachShadow({ mode: "open" });
  const style = document.createElement("style");
  style.textContent = widgetCss;
  shadow.appendChild(style);
  const mountPoint = document.createElement("div");
  shadow.appendChild(mountPoint);
  // Separate Vue app so the widget never touches the host's component tree or root element.
  const app = createApp(BiatecChatRoot, { options, shadow: true });
  app.mount(mountPoint);
  mounted = { host, app };
  return mounted;
}

/** Unmounts the widget mounted by `mountWidget` and removes its host element. Returns whether one was mounted. */
export function unmountWidget(): boolean {
  if (!mounted) return false;
  const { app, host } = mounted;
  mounted = null;
  app.unmount();
  host.remove();
  return true;
}
