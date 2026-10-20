import { reactive } from "vue";
import type { ChatTheme } from "../options";
import { setLocale as setI18nLocale } from "../i18n";
import type { AskResult, BiatecChatApi } from "./types";

/** What a mounted widget exposes to the host API. Implemented by `createCore` (core.ts). */
export interface CoreHandle {
  openPanel(): void;
  closePanel(): void;
  ask(prompt: string): Promise<AskResult>;
}

/**
 * Settings the host makes through `useBiatecChat()`. They live outside any widget instance, so a host may call
 * `setLocale` or `setPageContext` before the widget is mounted, and every instance (floating and inline) sees them.
 * `null` / unset means "use the plugin option".
 */
export const hostSettings = reactive({
  theme: null as ChatTheme | null,
  locale: null as string | null,
  pageContextSet: false,
  pageContext: undefined as string | undefined,
  /** `open()` called before any widget was mounted: the first one opens as soon as it mounts. */
  pendingOpen: false,
});

const cores: CoreHandle[] = [];

/** The most recently mounted widget receives the host calls. Returns the unregister function. */
export function registerCore(core: CoreHandle): () => void {
  cores.push(core);
  return () => {
    const i = cores.indexOf(core);
    if (i >= 0) cores.splice(i, 1);
  };
}

export function currentCore(): CoreHandle | null {
  return cores[cores.length - 1] ?? null;
}

const THEMES: readonly ChatTheme[] = ["auto", "light", "dark", "host"];

const api: BiatecChatApi = {
  open() {
    const c = currentCore();
    if (c) c.openPanel();
    else hostSettings.pendingOpen = true;
  },
  close() {
    hostSettings.pendingOpen = false;
    currentCore()?.closePanel();
  },
  async ask(prompt: string): Promise<AskResult> {
    if (typeof prompt !== "string" || !prompt.trim()) return "empty";
    const c = currentCore();
    if (!c) {
      console.warn("[biatec-chat] ask() was called before the widget was mounted; nothing was sent.");
      return "unavailable";
    }
    return c.ask(prompt);
  },
  setPageContext(context?: string | null) {
    hostSettings.pageContextSet = true;
    hostSettings.pageContext = typeof context === "string" && context.trim() ? context : undefined;
  },
  setTheme(theme: ChatTheme) {
    if (!THEMES.includes(theme)) {
      console.warn(`[biatec-chat] setTheme: unknown theme "${String(theme)}"`);
      return;
    }
    hostSettings.theme = theme;
  },
  setLocale(locale: string) {
    if (typeof locale !== "string" || !locale) return;
    hostSettings.locale = locale;
    setI18nLocale(locale);
  },
};

/**
 * Host API: `open`, `close`, `ask(prompt)`, `setPageContext`, `setTheme`, `setLocale`. Safe to call anywhere (it is
 * not a Vue composable bound to a component), including before the widget is mounted.
 */
export function useBiatecChat(): BiatecChatApi {
  return api;
}

/** Test seam: forget host settings and registered widgets. */
export function resetHostApi(): void {
  cores.splice(0, cores.length);
  hostSettings.theme = null;
  hostSettings.locale = null;
  hostSettings.pageContextSet = false;
  hostSettings.pageContext = undefined;
  hostSettings.pendingOpen = false;
}
