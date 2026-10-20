// Stand-in for BiatecDEX `src/composables/useTheme.ts`: dark by default, the class `p-dark` on <html>, the choice
// kept in localStorage['biatec-theme']. The widget is never told about the toggle: `theme: 'host'` watches the class.
import { ref } from "vue";

const STORAGE_KEY = "biatec-theme";
type Mode = "light" | "dark";

/** `?theme=` is for screenshots: it applies to this load only and is never written to storage. */
function fromQuery(): Mode | null {
  const fromUrl = new URLSearchParams(globalThis.location?.search ?? "").get("theme");
  return fromUrl === "light" || fromUrl === "dark" ? fromUrl : null;
}

function initial(): Mode {
  const q = fromQuery();
  if (q) return q;
  try {
    const stored = globalThis.localStorage?.getItem(STORAGE_KEY);
    if (stored === "light" || stored === "dark") return stored;
  } catch {
    // storage blocked: default below
  }
  return "dark";
}

const mode = ref<Mode>(initial());

function apply(m: Mode, persist: boolean) {
  document.documentElement.classList.toggle("p-dark", m === "dark");
  if (!persist) return;
  try {
    localStorage.setItem(STORAGE_KEY, m);
  } catch {
    // storage blocked: the choice lasts for this page only
  }
}

export function useTheme() {
  apply(mode.value, false);
  return {
    isDark: () => mode.value === "dark",
    mode,
    toggle() {
      mode.value = mode.value === "dark" ? "light" : "dark";
      apply(mode.value, true); // an explicit toggle is the user's choice
    },
  };
}
