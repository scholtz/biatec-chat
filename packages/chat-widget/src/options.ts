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

export type ResolvedOptions = Required<Omit<BiatecChatOptions, "pageContext">> & { pageContext?: string };

export function resolveOptions(o: BiatecChatOptions): ResolvedOptions {
  if (!o || !o.backendUrl) throw new Error("BiatecChat: backendUrl is required");
  if (!o.clientKey) throw new Error("BiatecChat: clientKey is required");
  return {
    backendUrl: o.backendUrl.replace(/\/+$/, ""),
    clientKey: o.clientKey,
    locale: o.locale ?? "en",
    theme: o.theme ?? "auto",
    suggestions: o.suggestions ?? "default",
    autoMount: o.autoMount ?? true,
    pageContext: o.pageContext,
  };
}
