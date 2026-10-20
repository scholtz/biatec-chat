// `@biatec/chat-widget/standalone`: the widget for hosts that are not Vue apps (Docusaurus/React, plain HTML). Vue is
// bundled into this build. It renders the same root as the Vue plugin (mountWidget), and the host API is the same
// object `useBiatecChat()` returns. Importing it has no side effect without a DOM, so it is SSR-safe; in the IIFE
// build a `<script data-backend-url data-client-key>` tag mounts the widget by itself.
import { resolveOptions, type BiatecChatOptions, type ResolvedOptions } from "../options";
import { useBiatecChat } from "../api";
import { mountWidget, unmountWidget } from "./mount";
import type { BiatecChatHandle, MountBiatecChatOptions } from "./types";

export type { BiatecChatOptions, ChatTheme, ChatMode } from "../options";
export type { AskResult, BiatecChatApi } from "../api/types";
export type { BiatecChatHandle, MountBiatecChatOptions } from "./types";
export { useBiatecChat };

/** Default dark signal of the standalone build: Docusaurus (and many sites) set `<html data-theme="dark">`. */
export const DEFAULT_HOST_DARK_ATTRIBUTE = "data-theme=dark";

function standaloneDefaults(o: MountBiatecChatOptions): BiatecChatOptions {
  const { container: _container, ...rest } = o;
  const html = typeof document !== "undefined" ? document.documentElement : null;
  // Non-Vue hosts have no PrimeVue `.p-dark` class. A page that marks its theme with `<html data-theme>` (Docusaurus)
  // is followed; any other page follows prefers-color-scheme, unless the options say otherwise.
  const theme = rest.theme ?? (html?.hasAttribute("data-theme") ? "host" : "auto");
  return {
    ...rest,
    theme,
    hostDarkAttribute:
      rest.hostDarkAttribute ?? (theme === "host" && !rest.hostDarkClass ? DEFAULT_HOST_DARK_ATTRIBUTE : undefined),
    locale: rest.locale ?? (html?.lang || undefined),
    autoMount: true,
  };
}

const COMPARED = ["backendUrl", "clientKey", "locale", "theme", "hostDarkClass", "hostDarkAttribute", "suggestions", "mode", "pageContext"] as const;

function sameOptions(a: ResolvedOptions, b: ResolvedOptions): boolean {
  return COMPARED.every((k) => JSON.stringify(a[k]) === JSON.stringify(b[k]));
}

const noDomHandle = (): BiatecChatHandle => ({ ...useBiatecChat(), host: null, unmount });

function resolveContainer(c: Element | string | undefined): Element | undefined {
  if (c === undefined) return undefined;
  if (typeof c !== "string") return c;
  const el = document.querySelector(c);
  if (!el) throw new Error(`BiatecChat: container "${c}" not found`);
  return el;
}

/**
 * Mounts the floating widget into the page and returns the host API. Call it in the browser only (in Docusaurus from
 * a client module or behind `ExecutionEnvironment.canUseDOM`); without a DOM it does nothing and returns a handle
 * whose calls are safe no-ops.
 * Calling it again while a widget is mounted (by this call, the script tag, the Vue plugin or another copy of the
 * package) returns that widget; when the new options or container differ, they are ignored with a console warning:
 * call `unmount()` first to change them.
 * Throws when `backendUrl` or `clientKey` is missing, like the plugin, and when a `[data-biatec-chat-host]` element
 * that no copy of the package registered is already in the page.
 *
 *     mountBiatecChat({ backendUrl: 'https://chat.biatec.io', clientKey: 'biatec-clamm-docs' })
 *     mountBiatecChat('#chat-slot', { backendUrl, clientKey })
 */
export function mountBiatecChat(options: MountBiatecChatOptions): BiatecChatHandle;
export function mountBiatecChat(container: Element | string, options: MountBiatecChatOptions): BiatecChatHandle;
export function mountBiatecChat(a: Element | string | MountBiatecChatOptions, b?: MountBiatecChatOptions): BiatecChatHandle {
  const o: MountBiatecChatOptions = typeof a === "string" || isElement(a) ? { ...(b as MountBiatecChatOptions), container: a } : a;
  const resolved = resolveOptions(standaloneDefaults(o));
  if (typeof document === "undefined") return noDomHandle();
  const container = resolveContainer(o.container);
  const r = mountWidget(resolved, container);
  if (r.status === "no-dom") return noDomHandle();
  if (r.status === "foreign")
    throw new Error("BiatecChat: a [data-biatec-chat-host] element is already in the page but no widget is registered on it");
  const w = r.widget;
  if (r.status === "existing" && (!sameOptions(w.options, resolved) || (container && container !== w.container)))
    console.warn("[biatec-chat] mountBiatecChat: a widget is already mounted; the new options and container were ignored. Call unmount() first to change them.");
  // The API of the copy that mounted the widget: a plugin or second script's widget answers this handle too.
  return { ...w.api, host: w.host, unmount };
}

function isElement(v: unknown): v is Element {
  return typeof Element !== "undefined" && v instanceof Element;
}

/** Removes the widget mounted by `mountBiatecChat` (or the script-tag auto-mount). */
export function unmount(): void {
  unmountWidget();
}

/** `data-*` attributes of the script tag, in camelCase as `dataset` gives them, and the option each one sets. */
const ATTRIBUTES = {
  backendUrl: "backendUrl",
  clientKey: "clientKey",
  locale: "locale",
  // Not `data-theme`: that attribute name is a theme selector on many sites (Infima's `[data-theme='dark']`).
  chatTheme: "theme",
  mode: "mode",
  hostDarkClass: "hostDarkClass",
  hostDarkAttribute: "hostDarkAttribute",
  pageContext: "pageContext",
} as const;

/**
 * Reads the options from a script tag's `data-*` attributes and mounts the widget once the DOM is ready. Does nothing
 * when the tag has no `data-backend-url` (the host then calls `mountBiatecChat` itself). Errors go to the console,
 * never to the host page's own script.
 */
export function autoMount(script: HTMLOrSVGScriptElement | null | undefined): void {
  if (typeof document === "undefined" || !script || !(script instanceof HTMLElement)) return;
  const data = script.dataset;
  if (!data.backendUrl) return;
  const options: Record<string, string> = {};
  for (const [attr, option] of Object.entries(ATTRIBUTES)) {
    const v = data[attr];
    if (v !== undefined && v !== "") options[option] = v;
  }
  const run = () => {
    try {
      mountBiatecChat(options as unknown as MountBiatecChatOptions);
    } catch (e) {
      console.error("[biatec-chat]", e);
    }
  };
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", run, { once: true });
  else run();
}

// Classic <script> tag (IIFE build): `document.currentScript` is that tag. It is null in an ES module, so importing
// this entry never mounts anything by itself.
if (typeof document !== "undefined") autoMount(document.currentScript);
