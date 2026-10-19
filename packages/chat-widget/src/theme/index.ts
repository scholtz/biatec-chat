import { onBeforeUnmount, onMounted, ref, type Ref } from "vue";
import type { ChatTheme } from "../options";

export type ResolvedTheme = "light" | "dark";

export interface ThemeOptions {
  theme: ChatTheme;
  hostDarkClass: string;
}

/** Dark is the base look: without a usable matchMedia or `<html>` the result is "dark". */
export function resolveTheme(o: ThemeOptions): ResolvedTheme {
  if (o.theme === "light" || o.theme === "dark") return o.theme;
  if (o.theme === "host") {
    if (typeof document === "undefined") return "dark";
    return document.documentElement.classList.contains(o.hostDarkClass) ? "dark" : "light";
  }
  if (typeof window === "undefined" || !window.matchMedia) return "dark";
  return window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
}

/**
 * Follows the theme option: `host` observes the host's dark-mode class on `<html>` (MutationObserver, so a toggle
 * lands in the next microtask), `auto` follows prefers-color-scheme.
 */
export function watchTheme(o: ThemeOptions, onChange: (t: ResolvedTheme) => void): () => void {
  const emit = () => onChange(resolveTheme(o));
  emit();
  if (o.theme === "host" && typeof MutationObserver !== "undefined" && typeof document !== "undefined") {
    const mo = new MutationObserver(emit);
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ["class"] });
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
