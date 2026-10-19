import { describe, it, expect, afterEach, vi } from "vitest";
import { createApp } from "vue";
import { BiatecChat } from "../src/index";
import { resolveOptions } from "../src/options";
import { resolveTheme, watchTheme } from "../src/theme";

const base = { backendUrl: "https://x", clientKey: "k" };
const mountWidget = (extra: object = {}) => {
  createApp({ render: () => null }).use(BiatecChat, { ...base, ...extra });
  const host = document.querySelector<HTMLElement>("[data-biatec-chat-host]")!;
  return { host, shadow: host.shadowRoot! };
};
const frame = () => new Promise<void>((r) => requestAnimationFrame(() => r()));

afterEach(() => {
  document.body.innerHTML = "";
  document.head.innerHTML = "";
  document.documentElement.className = "";
  vi.unstubAllGlobals();
});

describe("shadow DOM", () => {
  it("renders the widget into an open shadow root with the CSS injected", async () => {
    const { host, shadow } = mountWidget();
    expect(shadow).toBeTruthy();
    expect(shadow.querySelector(".biatec-chat .bc-launcher")).toBeTruthy();
    expect(shadow.querySelector("style")!.textContent).toContain("--color-brand-primary");
    expect(host.querySelector(".biatec-chat")).toBeNull();
    // Light DOM stays empty: host CSS (Tailwind preflight, `button { background: red }`) cannot reach the buttons.
    document.head.innerHTML = "<style>button { background: red !important }</style>";
    expect(document.querySelectorAll("button")).toHaveLength(0);
    expect(document.querySelectorAll(".bc-launcher")).toHaveLength(0);
  });

  it("injected CSS declares tokens on :host so a host-element property overrides them", () => {
    const { host, shadow } = mountWidget();
    host.style.setProperty("--color-brand-primary", "rgb(1, 2, 3)");
    const css = shadow.querySelector("style")!.textContent!;
    expect(css).toMatch(/:host[^{]*\{[^}]*--color-brand-primary: #2dd4a7/);
    // The root element inside the shadow root must not redeclare brand tokens (it would shadow the host's value).
    expect(css).not.toMatch(/(^|\n)\.biatec-chat\s*\{[^}]*--color-brand-primary/);
    expect(host.style.getPropertyValue("--color-brand-primary")).toBe("rgb(1, 2, 3)");
  });

  it("ships no Google Fonts URL", () => {
    const { shadow } = mountWidget();
    expect(shadow.querySelector("style")!.textContent).not.toContain("fonts.googleapis.com");
  });
});

describe("theme", () => {
  it("defaults: option auto, hostDarkClass p-dark, dark when nothing says otherwise", () => {
    const o = resolveOptions(base);
    expect(o.theme).toBe("auto");
    expect(o.hostDarkClass).toBe("p-dark");
    vi.stubGlobal("matchMedia", undefined);
    expect(resolveTheme(o)).toBe("dark");
  });

  it("auto follows prefers-color-scheme", () => {
    const stub = (light: boolean) =>
      vi.stubGlobal("matchMedia", () => ({ matches: light, addEventListener() {}, removeEventListener() {} }));
    stub(true);
    expect(resolveTheme(resolveOptions(base))).toBe("light");
    stub(false);
    expect(resolveTheme(resolveOptions(base))).toBe("dark");
  });

  it("host: data-theme on the host and the root follows p-dark within one frame", async () => {
    const { host, shadow } = mountWidget({ theme: "host" });
    await frame();
    const root = shadow.querySelector(".biatec-chat")!;
    expect(host.getAttribute("data-theme")).toBe("light");
    document.documentElement.classList.add("p-dark");
    await frame();
    expect(host.getAttribute("data-theme")).toBe("dark");
    expect(root.getAttribute("data-theme")).toBe("dark");
    document.documentElement.classList.remove("p-dark");
    await frame();
    expect(host.getAttribute("data-theme")).toBe("light");
    expect(root.getAttribute("data-theme")).toBe("light");
  });

  it("honours a custom hostDarkClass and fixed light/dark", async () => {
    const seen: string[] = [];
    const stop = watchTheme({ theme: "host", hostDarkClass: "dark" }, (t) => seen.push(t));
    document.documentElement.classList.add("dark");
    await frame();
    stop();
    expect(seen).toEqual(["light", "dark"]);
    expect(resolveTheme({ theme: "light", hostDarkClass: "p-dark" })).toBe("light");
    expect(resolveTheme({ theme: "dark", hostDarkClass: "p-dark" })).toBe("dark");
  });
});
