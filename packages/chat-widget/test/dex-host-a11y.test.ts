// Accessibility of the BiatecDEX-like host page itself (playground/dex, reused by the demo at /dex.html; T-102), not
// of the widget (test/i18n-a11y.test.ts covers that). The browser check is axe in Chromium on the built pages; jsdom
// has no layout, so here axe runs without its pixel-based contrast rule, button names are checked directly, and the
// light-mode PrimeVue overrides in playground/dex/dex.css are checked for contrast against Aura's own palette.
import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import { createApp, type App } from "vue";
import { flushPromises } from "@vue/test-utils";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import axe from "axe-core";
import PrimeVue from "primevue/config";
import ToastService from "primevue/toastservice";
import Aura from "@primeuix/themes/aura";
import { BiatecChat } from "../src/index";
import { resetHostApi } from "../src/api";
import DexApp from "../playground/dex/DexApp.vue";
import { i18n } from "../playground/dex/host/i18n";

// jsdom has no canvas; axe probes it for icon ligatures. A null context makes axe skip that probe quietly.
HTMLCanvasElement.prototype.getContext = (() => null) as never;

let app: App | undefined;

beforeEach(() => {
  vi.spyOn(console, "warn").mockImplementation(() => {}); // PrimeVue 5's missing-licence warning
  // jsdom has no matchMedia; PrimeVue's Select listens for orientation changes.
  vi.stubGlobal("matchMedia", (query: string) => ({
    matches: false,
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
  }));
});

afterEach(() => {
  app?.unmount();
  app = undefined;
  const widgetMount = document.querySelector("[data-biatec-chat-host]")?.shadowRoot?.querySelector("div") as
    | (Element & { __vue_app__?: App })
    | null
    | undefined;
  widgetMount?.__vue_app__?.unmount();
  document.body.innerHTML = "";
  document.documentElement.className = "";
  resetHostApi();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

async function mountDexPage(theme: "dark" | "light") {
  document.documentElement.className = theme === "dark" ? "p-dark" : "";
  const el = document.createElement("div");
  document.body.appendChild(el);
  app = createApp(DexApp);
  app.use(PrimeVue, { theme: { preset: Aura, options: { prefix: "p", darkModeSelector: ".p-dark" } } });
  app.use(ToastService);
  app.use(i18n);
  app.use(BiatecChat, { backendUrl: "https://chat.biatec.io", clientKey: "biatec-dex", locale: "en", theme: "host", mode: "floating" });
  app.mount(el);
  await flushPromises();
}

const visibleText = (el: Element): string => {
  // Text a sighted user sees, including SVG <text>; aria-hidden does not hide anything visually.
  return (el.textContent ?? "").replace(/\s+/g, " ").trim().toLowerCase();
};

describe.each(["dark", "light"] as const)("DEX host page, %s", (theme) => {
  it("axe finds no serious or critical violations (contrast is checked below)", async () => {
    await mountDexPage(theme);
    const result = await axe.run(document.body, {
      runOnly: { type: "tag", values: ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"] },
      rules: { "color-contrast": { enabled: false } },
    });
    const serious = result.violations
      .filter((v) => v.impact === "serious" || v.impact === "critical")
      .map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`);
    expect(serious).toEqual([]);
  });

  it("every labelled button's visible text is part of its accessible name (WCAG 2.5.3)", async () => {
    await mountDexPage(theme);
    const buttons = [...document.querySelectorAll("button[aria-label]")];
    expect(buttons.some((b) => b.getAttribute("data-testid") === "dex-ask")).toBe(true);
    for (const b of buttons) expect(b.getAttribute("aria-label")!.toLowerCase(), b.outerHTML).toContain(visibleText(b));
  });
});

describe("DEX host page: light-mode PrimeVue contrast overrides (playground/dex/dex.css)", () => {
  const css = readFileSync(resolve(__dirname, "../playground/dex/dex.css"), "utf8");
  const block = css.slice(css.indexOf(":root:root {"), css.indexOf("}", css.indexOf(":root:root {")));
  const decl = Object.fromEntries([...block.matchAll(/(--p-[\w-]+):\s*([^;]+);/g)].map((m) => [m[1]!, m[2]!.trim()]));
  const palette = Aura.primitive as unknown as Record<string, Record<string, string>>;
  // Aura's light surface is slate and its primary is emerald.
  const prim = (ref: string) => {
    const m = /^var\(--p-(\w+)-(\d+)\)$/.exec(ref)!;
    return palette[m[1] === "surface" ? "slate" : m[1]!]![m[2]!]!;
  };
  /** The light half of a token, following var() references to other tokens in the block. */
  const light = (name: string): string => {
    const v = decl[name]!;
    const ld = /^light-dark\(([^,]+),/.exec(v);
    if (ld) return prim(ld[1]!.trim());
    const ref = /^var\((--p-[\w-]+)\)$/.exec(v)![1]!;
    return light(ref);
  };
  const lum = (hex: string) => {
    const n = parseInt(hex.slice(1), 16);
    const c = [(n >> 16) & 255, (n >> 8) & 255, n & 255].map((x) => {
      const s = x / 255;
      return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * c[0]! + 0.7152 * c[1]! + 0.0722 * c[2]!;
  };
  const ratio = (a: string, b: string) => {
    const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x);
    return (hi! + 0.05) / (lo! + 0.05);
  };

  it("keeps Aura's dark half: every value is a light-dark() pair or a reference to one", () => {
    expect(Object.keys(decl).length).toBeGreaterThanOrEqual(20);
    for (const [k, v] of Object.entries(decl)) expect(v, k).toMatch(/^(light-dark\(.+, .+\)|var\(--p-[\w-]+\))$/);
  });

  it("white button labels reach 4.5:1 on the primary, success and danger backgrounds in every state", () => {
    for (const sev of ["primary", "success", "danger"])
      for (const state of ["", "hover-", "active-"]) {
        const bg = light(`--p-button-${sev}-${state}background`);
        expect(ratio("#ffffff", bg), `${sev} ${state}${bg}`).toBeGreaterThanOrEqual(4.5);
        expect(light(`--p-button-${sev}-${state}border-color`)).toBe(bg);
      }
  });

  it("SelectButton labels and link buttons reach 4.5:1 on what they are drawn on", () => {
    expect(ratio(light("--p-togglebutton-color"), palette.slate!["100"]!)).toBeGreaterThanOrEqual(4.5);
    // Link buttons sit on the translucent white card over the page background (#eef2f6), the darker of the two.
    for (const bg of ["#ffffff", "#eef2f6"]) expect(ratio(light("--p-button-link-color"), bg)).toBeGreaterThanOrEqual(4.5);
  });
});
