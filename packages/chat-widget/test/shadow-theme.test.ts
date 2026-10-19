import { describe, it, expect, afterEach, vi } from "vitest";
import { createApp, createSSRApp, h, ref } from "vue";
import { renderToString } from "vue/server-renderer";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import BiatecChatRoot from "../src/components/BiatecChatRoot.vue";
import { Button, Dialog } from "../src/ui";
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

describe("review fixes", () => {
  it("hostDarkClass tolerates whitespace lists and an empty string", () => {
    document.documentElement.className = "p-dark dark";
    expect(resolveTheme({ theme: "host", hostDarkClass: "p-dark dark" })).toBe("dark");
    expect(resolveTheme({ theme: "host", hostDarkClass: "  " })).toBe("light");
    expect(resolveTheme({ theme: "host", hostDarkClass: "" })).toBe("light");
    document.documentElement.className = "dark";
    expect(resolveTheme({ theme: "host", hostDarkClass: "p-dark dark" })).toBe("light");
  });

  it("a standalone root carries data-standalone on its very first render", async () => {
    const html = await renderToString(createSSRApp(BiatecChatRoot, { options: resolveOptions(base) }));
    expect(html).toContain("data-standalone");
  });

  it("the plugin mount is not marked standalone", () => {
    const { shadow } = mountWidget();
    expect(shadow.querySelector(".biatec-chat")!.hasAttribute("data-standalone")).toBe(false);
  });

  it("Dialog and Button inside a shadow root: focus trap and Esc return use shadowRoot.activeElement", async () => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    const shadow = host.attachShadow({ mode: "open" });
    const mp = document.createElement("div");
    shadow.appendChild(mp);
    const open = ref(false);
    const app = createApp({
      render: () =>
        h("div", { class: "biatec-chat" }, [
          h(Button, { id: "opener", onClick: () => (open.value = true) }, () => "open"),
          h(Dialog, { open: open.value, title: "T", onClose: () => (open.value = false) }, () => [
            h(Button, { id: "a" }, () => "A"),
            h(Button, { id: "b", variant: "danger" }, () => "B"),
          ]),
        ]),
    });
    app.mount(mp);
    const flush = async () => {
      await Promise.resolve();
      await new Promise((r) => setTimeout(r, 0));
    };
    const opener = shadow.getElementById("opener")!;
    opener.focus();
    expect(shadow.activeElement).toBe(opener);
    opener.click();
    await flush();
    const a = shadow.getElementById("a")!;
    const b = shadow.getElementById("b")!;
    expect(shadow.activeElement).toBe(a);
    b.focus();
    const tab = new KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true });
    b.dispatchEvent(tab);
    expect(tab.defaultPrevented).toBe(true);
    expect(shadow.activeElement).toBe(a);
    a.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    await flush();
    expect(shadow.querySelector("[role=dialog]")).toBeNull();
    expect(shadow.activeElement).toBe(opener);
    app.unmount();
  });

  it("danger button and status badges follow the design (subtle fill, bordered badges)", () => {
    const css = readFileSync(resolve(__dirname, "../src/styles/ui.css"), "utf8");
    expect(css).toMatch(/data-variant="danger"\]\s*\{[^}]*background: var\(--color-status-danger-subtle\)[^}]*color: var\(--color-status-danger\)/);
    for (const tone of ["brand", "info", "success", "danger"]) {
      expect(css).toMatch(new RegExp(`data-tone="${tone}"\\] \\{ border-color`));
    }
  });
});
