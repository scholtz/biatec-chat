import { ref } from "vue";

// Page theme: dark by default, light on request, kept per visitor in localStorage (a convenience only; the page works
// without storage). It sets data-theme for the page tokens and the class `demo-dark`, which the widget follows
// because it is installed with theme 'host' and hostDarkClass 'demo-dark'.
export const HOST_DARK_CLASS = "demo-dark";
const KEY = "biatec-demo-theme";
type Mode = "dark" | "light";

function initial(): Mode {
  const q = new URLSearchParams(globalThis.location?.search ?? "").get("theme");
  if (q === "light" || q === "dark") return q;
  try {
    const stored = globalThis.localStorage?.getItem(KEY);
    if (stored === "light" || stored === "dark") return stored;
  } catch {
    // storage blocked
  }
  return "dark";
}

export const pageTheme = ref<Mode>(initial());

export function applyPageTheme(m: Mode = pageTheme.value) {
  const html = document.documentElement;
  html.dataset.theme = m;
  html.classList.toggle(HOST_DARK_CLASS, m === "dark");
}

export function togglePageTheme() {
  pageTheme.value = pageTheme.value === "dark" ? "light" : "dark";
  applyPageTheme();
  try {
    localStorage.setItem(KEY, pageTheme.value);
  } catch {
    // storage blocked: this page load only
  }
}
