// Stand-in for BiatecDEX `src/composables/useTheme.ts`: dark by default, the class `p-dark` on <html>, the choice
// kept in localStorage['biatec-theme']. The widget is never told about the toggle: `theme: 'host'` watches the class.
import { ref } from "vue";

const STORAGE_KEY = "biatec-theme";
type Mode = "light" | "dark";

function initial(): Mode {
  const fromUrl = new URLSearchParams(globalThis.location?.search ?? "").get("theme");
  if (fromUrl === "light" || fromUrl === "dark") return fromUrl;
  try {
    const stored = globalThis.localStorage?.getItem(STORAGE_KEY);
    if (stored === "light" || stored === "dark") return stored;
  } catch {
    // storage blocked: default below
  }
  return "dark";
}

const mode = ref<Mode>(initial());

function apply(m: Mode) {
  document.documentElement.classList.toggle("p-dark", m === "dark");
  try {
    localStorage.setItem(STORAGE_KEY, m);
  } catch {
    // storage blocked: the choice lasts for this page only
  }
}

export function useTheme() {
  apply(mode.value);
  return {
    isDark: () => mode.value === "dark",
    mode,
    toggle() {
      mode.value = mode.value === "dark" ? "light" : "dark";
      apply(mode.value);
    },
  };
}
