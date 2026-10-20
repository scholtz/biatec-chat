export type ChatTheme = "auto" | "light" | "dark" | "host";
/**
 * `floating` (default): the launcher and a floating panel. `workspace`: the launcher, and the panel opens full
 * screen. `inline`: no launcher; a panel shows only where the host mounts `BiatecChatInline`.
 */
export type ChatMode = "floating" | "inline" | "workspace";
const MODES: readonly ChatMode[] = ["floating", "inline", "workspace"];

export interface BiatecChatOptions {
  backendUrl: string;
  /** Identifies the host, not a secret. */
  clientKey: string;
  locale?: string;
  /** `host` follows the host dark-mode class on <html>; `auto` follows prefers-color-scheme. */
  theme?: ChatTheme;
  /** Class on <html> meaning dark when theme is `host` (default `p-dark`, PrimeVue). */
  hostDarkClass?: string;
  /**
   * Attribute on <html> meaning dark when theme is `host`, as `name=value` (for example `data-theme=dark`, Docusaurus)
   * or a bare `name` (present means dark). When set it replaces `hostDarkClass`.
   */
  hostDarkAttribute?: string;
  suggestions?: "default" | string[];
  /** Opt-in page context string. Nothing is read from the host page unless this is set. */
  pageContext?: string;
  /** Mount the widget into document.body automatically (default true). */
  autoMount?: boolean;
  /** Panel mode (default `floating`). */
  mode?: ChatMode;
}

export type ResolvedOptions = Required<Omit<BiatecChatOptions, "pageContext">> & { pageContext?: string };

export function resolveOptions(o: BiatecChatOptions): ResolvedOptions {
  if (!o || !o.backendUrl) throw new Error("BiatecChat: backendUrl is required");
  if (!o.clientKey) throw new Error("BiatecChat: clientKey is required");
  return {
    backendUrl: o.backendUrl.replace(/\/+$/, ""),
    clientKey: o.clientKey,
    locale: o.locale ?? "en",
    theme: o.theme ?? "auto",
    hostDarkClass: o.hostDarkClass ?? "p-dark",
    hostDarkAttribute: o.hostDarkAttribute?.trim() ?? "",
    suggestions: o.suggestions ?? "default",
    autoMount: o.autoMount ?? true,
    mode: o.mode && MODES.includes(o.mode) ? o.mode : "floating",
    pageContext: o.pageContext,
  };
}
