import { onBeforeUnmount, onMounted, ref, type Ref } from "vue";
import type { ChatTheme } from "../options";

export type ResolvedTheme = "light" | "dark";

export interface ThemeOptions {
  theme: ChatTheme;
  hostDarkClass: string;
  /** `name=value` or a bare `name` on <html> meaning dark (Docusaurus: `data-theme=dark`); replaces `hostDarkClass`. */
  hostDarkAttribute?: string;
}

/** `data-theme=dark` → ["data-theme", "dark"]; a bare name → [name, null] (present means dark); blank → null. */
function parseAttribute(spec: string | undefined): [string, string | null] | null {
  const raw = (spec ?? "").trim();
  if (!raw) return null;
  const eq = raw.indexOf("=");
  if (eq < 0) return [raw, null];
  const name = raw.slice(0, eq).trim();
  const value = raw
    .slice(eq + 1)
    .trim()
    .replace(/^(["'])(.*)\1$/, "$2");
  return name ? [name, value] : null;
}

/** Dark is the base look: without a usable matchMedia or `<html>` the result is "dark". */
export function resolveTheme(o: ThemeOptions): ResolvedTheme {
  if (o.theme === "light" || o.theme === "dark") return o.theme;
  if (o.theme === "host") {
    if (typeof document === "undefined") return "dark";
    const attr = parseAttribute(o.hostDarkAttribute);
    if (attr) {
      const v = document.documentElement.getAttribute(attr[0]);
      return v !== null && (attr[1] === null || v === attr[1]) ? "dark" : "light";
    }
    const wanted = (o.hostDarkClass ?? "").split(/\s+/).filter(Boolean);
    if (!wanted.length) return "light";
    const cl = document.documentElement.classList;
    return wanted.every((c) => cl.contains(c)) ? "dark" : "light";
  }
  if (typeof window === "undefined" || !window.matchMedia) return "dark";
  return window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
}

/**
 * Follows the theme option: `host` observes the host's dark-mode class (or `hostDarkAttribute`) on `<html>`
 * (MutationObserver, so a toggle lands in the next microtask), `auto` follows prefers-color-scheme.
 */
export function watchTheme(o: ThemeOptions, onChange: (t: ResolvedTheme) => void): () => void {
  const emit = () => onChange(resolveTheme(o));
  emit();
  if (o.theme === "host" && typeof MutationObserver !== "undefined" && typeof document !== "undefined") {
    const mo = new MutationObserver(emit);
    const attr = parseAttribute(o.hostDarkAttribute);
    mo.observe(document.documentElement, { attributes: true, attributeFilter: [attr ? attr[0] : "class"] });
    return () => mo.disconnect();
  }
  if (o.theme === "auto" && typeof window !== "undefined" && window.matchMedia) {
    const mq = window.matchMedia("(prefers-color-scheme: light)");
    mq.addEventListener?.("change", emit);
    return () => mq.removeEventListener?.("change", emit);
  }
  return () => {};
}

/** Reactive resolved theme for a component; the initial value is correct on first render. */
export function useTheme(o: ThemeOptions): Ref<ResolvedTheme> {
  const current = ref<ResolvedTheme>(resolveTheme(o));
  let stop = () => {};
  onMounted(() => {
    stop = watchTheme(o, (t) => (current.value = t));
  });
  onBeforeUnmount(() => stop());
  return current;
}
