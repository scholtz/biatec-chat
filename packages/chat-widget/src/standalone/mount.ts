import { createApp, type App } from "vue";
import BiatecChatRoot from "../components/BiatecChatRoot.vue";
import type { ResolvedOptions } from "../options";
import { useBiatecChat } from "../api";
import type { BiatecChatApi } from "../api/types";
// The widget CSS as a string, injected into the widget's shadow root (no host stylesheet needed).
import widgetCss from "../styles.css?inline";

export interface MountedWidget {
  host: HTMLElement;
  app: App;
  /** Host API of the bundle that mounted this widget (another copy of the package has its own). */
  api: BiatecChatApi;
  options: ResolvedOptions;
  container: Element;
  unmount(): void;
}

/**
 * The floating widget in the page, shared through a well-known global symbol so that every copy of the package (Vue
 * plugin and standalone build together, or the script tag loaded twice) sees the same widget and reuses it instead of
 * adding a second one or handing back a dead handle.
 */
const KEY = Symbol.for("biatec-chat.widget");
type Registry = { [KEY]?: MountedWidget };
const registry = (): Registry => globalThis as unknown as Registry;

/** The widget currently mounted by any copy of the package, if its host element is still in the page. */
export function currentWidget(): MountedWidget | null {
  const w = registry()[KEY];
  return w && w.host.isConnected ? w : null;
}

export type MountResult =
  | { status: "mounted" | "existing"; widget: MountedWidget }
  /** No DOM (server rendering). */
  | { status: "no-dom" }
  /** A `[data-biatec-chat-host]` element exists that no copy of this package registered. */
  | { status: "foreign" };

/**
 * Mounts the floating widget (the plugin root, BiatecChatRoot) into a `<div data-biatec-chat-host>` appended to
 * `container` (default `document.body`), as its own Vue app in an open shadow root. Shared by the Vue plugin's
 * auto-mount and the standalone `mountBiatecChat()`, so both render the very same component.
 * A widget already in the page is returned (`existing`) instead of adding another. One whose host element was removed
 * from the page without `unmount()` is unmounted first, so its watchers, timers and session are released.
 */
export function mountWidget(options: ResolvedOptions, container?: Element): MountResult {
  if (typeof document === "undefined") return { status: "no-dom" };
  const live = currentWidget();
  if (live) return { status: "existing", widget: live };
  const stale = registry()[KEY];
  if (stale) stale.unmount();
  if (document.querySelector("[data-biatec-chat-host]")) return { status: "foreign" };

  const target = container ?? document.body;
  const host = document.createElement("div");
  host.setAttribute("data-biatec-chat-host", "");
  target.appendChild(host);
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
  const widget: MountedWidget = {
    host,
    app,
    api: useBiatecChat(),
    options,
    container: target,
    unmount() {
      if (registry()[KEY] === widget) delete registry()[KEY];
      app.unmount();
      host.remove();
    },
  };
  registry()[KEY] = widget;
  return { status: "mounted", widget };
}

/** Unmounts the widget in the page (whichever copy mounted it) and removes its host element. */
export function unmountWidget(): boolean {
  const w = registry()[KEY];
  if (!w) return false;
  w.unmount();
  return true;
}
