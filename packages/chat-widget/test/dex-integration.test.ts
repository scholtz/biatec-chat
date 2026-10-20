// The widget inside a BiatecDEX-shaped host (playground/dex.html, docs/integration-biatec-dex.md): PrimeVue 5 Aura
// with `.p-dark`, the host's own vue-i18n, and the host code from the hand-off doc, run against the widget source.
import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import { createApp, defineComponent, h, nextTick, ref, type App } from "vue";
import { flushPromises, mount } from "@vue/test-utils";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import PrimeVue from "primevue/config";
import Dialog from "primevue/dialog";
import Aura from "@primeuix/themes/aura";
import type { SessionState } from "@biatec/chat-protocol";
import BiatecChatRoot from "../src/components/BiatecChatRoot.vue";
import { resolveOptions } from "../src/options";
import type { ChatServices } from "../src/components/chat/useChat";
import { resetHostApi } from "../src/api";
import { setLocale as setWidgetLocale } from "../src/i18n";

// The playground's host modules import the package by name, like the DEX; here it is the source.
vi.mock("@biatec/chat-widget", async () => await import("../src/index"));

const { setTradingContext, clearTradingContext, askAboutOrder } = await import("../playground/dex/host/biatecChat");
const { setLocale: setDexLocale, getCurrentLocale } = await import("../playground/dex/host/i18n");
const { BiatecChat, useBiatecChat } = await import("../src/index");

const frame = () => new Promise<void>((r) => requestAnimationFrame(() => r()));
const widgetHost = () => document.querySelector<HTMLElement>("[data-biatec-chat-host]")!;
const shadow = () => widgetHost().shadowRoot!;

const apps: App[] = [];

/** A host app shaped like BiatecDEX src/main.ts: PrimeVue Aura with `.p-dark`, then the widget (step 1). */
function mountDex(root = defineComponent({ render: () => null })) {
  const el = document.createElement("div");
  document.body.appendChild(el);
  const app = createApp(root);
  apps.push(app);
  app.use(PrimeVue, { theme: { preset: Aura, options: { prefix: "p", darkModeSelector: ".p-dark" } } });
  // Step 1 as in playground/dex/main.ts (which the browser runs); only the backend URL differs.
  app.use(BiatecChat, { backendUrl: "https://chat.biatec.io", clientKey: "biatec-dex", locale: getCurrentLocale(), theme: "host", mode: "floating" });
  app.mount(el);
  return app;
}

beforeEach(() => {
  // PrimeVue 5 warns about the missing licence key (and adds its banner); not what these tests look at.
  vi.spyOn(console, "warn").mockImplementation(() => {});
  document.documentElement.className = "p-dark";
});

afterEach(async () => {
  // Unmount the host apps and the widget app the plugin created, so their MutationObservers and document listeners
  // stop before the next test; then restore what the DEX setLocale changed.
  for (const app of apps.splice(0)) app.unmount();
  const widgetMount = document.querySelector("[data-biatec-chat-host]")?.shadowRoot?.querySelector("div") as
    | (Element & { __vue_app__?: App })
    | null
    | undefined;
  widgetMount?.__vue_app__?.unmount();
  await setDexLocale("en");
  localStorage.clear();
  document.documentElement.removeAttribute("lang");
  document.body.innerHTML = "";
  document.documentElement.className = "";
  resetHostApi();
  setWidgetLocale("en");
  vi.restoreAllMocks();
});

describe("BiatecDEX host: theme", () => {
  it("follows the .p-dark class the DEX toggles on <html>, live", async () => {
    mountDex();
    await flushPromises();
    expect(widgetHost().getAttribute("data-theme")).toBe("dark");
    document.documentElement.classList.remove("p-dark"); // DEX useTheme(): light
    await flushPromises();
    expect(widgetHost().getAttribute("data-theme")).toBe("light");
    document.documentElement.classList.add("p-dark");
    await flushPromises();
    expect(widgetHost().getAttribute("data-theme")).toBe("dark");
  });
});

describe("BiatecDEX host: language", () => {
  it("the DEX setLocale (step 2) switches the widget; a locale without a catalog falls back to English", async () => {
    mountDex();
    const launcherLabel = () => shadow().querySelector(".bc-launcher")!.getAttribute("aria-label");
    expect(launcherLabel()).toBe("Ask Biatec AI");
    await setDexLocale("de");
    await nextTick();
    expect(shadow().querySelector(".biatec-chat")!.getAttribute("lang")).toBe("en");
    expect(launcherLabel()).toBe("Ask Biatec AI");
    await setDexLocale("en");
    await nextTick();
    expect(launcherLabel()).toBe("Ask Biatec AI");
  });
});

describe("BiatecDEX host: page context and Ask Biatec AI", () => {
  const anon: SessionState = { sessionId: "s", tier: "Anonymous", scopes: [], expiresAt: "2030-01-01T00:00:00Z" };
  function services() {
    const send = vi.fn(async function* () {});
    const svc: ChatServices = {
      transport: { open: vi.fn(async () => "success" as const), retryOnSignIn: vi.fn(async () => true), send, dispose: vi.fn() },
      auth: {
        session: vi.fn(async () => anon),
        startLogin: vi.fn(async () => null),
        startElevate: vi.fn(async () => null),
        status: vi.fn(async () => "Complete" as const),
        logoutUrl: vi.fn(() => "https://chat.biatec.io/auth/logout"),
      },
      pollMs: 1,
      timeoutMs: 50,
    };
    return { svc, send };
  }

  it("sends the page context only with a message, after the panel opened, and ask shows the prompt", async () => {
    const { svc, send } = services();
    const options = resolveOptions({ backendUrl: "https://chat.biatec.io", clientKey: "biatec-dex", theme: "host" });
    const w = mount(BiatecChatRoot, {
      props: { options, services: svc },
      attachTo: document.body,
      // Test-utils' transition stub outlives mount() and would skip PrimeVue's enter hooks in the layering test.
      global: { stubs: { transition: false } },
    });

    setTradingContext("testnet", "ALGO", "USDC", 1000); // step 3, on entering the trading route
    await flushPromises();
    // Nothing leaves the page on load or on setPageContext: no probe, no message.
    expect(svc.transport.open).not.toHaveBeenCalled();
    expect(send).not.toHaveBeenCalled();
    expect(w.find(".bc-panel").exists()).toBe(false);

    expect(await askAboutOrder("buy", 50, "ALGO", "USDC")).toBe("sent"); // step 4
    await flushPromises();
    expect(w.find(".bc-panel").exists()).toBe(true);
    expect(svc.transport.open).toHaveBeenCalledTimes(1);
    expect(send).toHaveBeenCalledTimes(1);
    expect((send.mock.calls[0] as unknown[])[0]).toMatchObject({
      message: "Swap 50 USDC to ALGO on Biatec DEX",
      context: { product: "Biatec DEX · testnet · pair ALGO/USDC · pool app 1000" },
    });
    // The prompt is a visible user message; the reply stream is empty, so no preview and nothing to confirm.
    expect(w.get(".bc-panel").text()).toContain("Swap 50 USDC to ALGO on Biatec DEX");
    expect(w.findAll("button").filter((b) => /confirm/i.test(b.text()))).toHaveLength(0);

    clearTradingContext(); // leaving the route
    await askAboutOrder("sell", 10, "ALGO", "USDC");
    await flushPromises();
    expect((send.mock.calls[1] as unknown[])[0]).toMatchObject({ message: "Swap 10 ALGO to USDC on Biatec DEX", context: null });
    w.unmount();
  });
});

describe("BiatecDEX host: layering", () => {
  it("a PrimeVue modal Dialog opened while the widget is open sits above the widget", async () => {
    const visible = ref(false);
    mountDex(
      defineComponent({
        render: () => h(Dialog, { visible: visible.value, modal: true, header: "Order settings" }, () => h("button", "Save")),
      }),
    );
    useBiatecChat().open();
    await flushPromises();
    expect(shadow().querySelector(".bc-panel")).toBeTruthy();

    visible.value = true;
    await flushPromises();
    await frame(); // PrimeVue assigns the z-index in the Dialog's enter transition
    const mask = document.querySelector<HTMLElement>(".p-dialog-mask")!;
    expect(mask).toBeTruthy();
    const dialogZ = Number(mask.style.zIndex);
    const css = shadow().querySelector("style")!.textContent!;
    const widgetZ = Number(/--bc-z:\s*(\d+)/.exec(css)![1]);
    expect(widgetZ).toBe(1000);
    expect(dialogZ).toBeGreaterThan(widgetZ);
    // Every fixed widget layer (launcher, minimized bar, floating and full-screen panel) uses --bc-z, never a literal.
    const fixedRules = css.match(/\{[^}]*position:\s*fixed[^}]*\}/g) ?? [];
    expect(fixedRules.length).toBeGreaterThan(0);
    for (const r of fixedRules) if (/z-index/.test(r)) expect(r).toMatch(/z-index:\s*var\(--bc-z\)/);
  });
});

describe("docs/integration-biatec-dex.md", () => {
  const doc = readFileSync(resolve(__dirname, "../../../docs/integration-biatec-dex.md"), "utf8");
  const dir = resolve(__dirname, "../playground/dex");
  const files = (d: string): string[] =>
    readdirSync(d).flatMap((f) => (statSync(join(d, f)).isDirectory() ? files(join(d, f)) : [join(d, f)]));
  const norm = (s: string) => s.trim().replace(/\s+/g, " ");
  const playground = files(dir)
    .filter((f) => /\.(ts|vue)$/.test(f))
    .map((f) => readFileSync(f, "utf8"))
    .join("\n")
    .split("\n")
    .map(norm);

  it("every ts and vue snippet line is in the playground host code, which compiles against dist (typecheck:playground)", () => {
    const blocks = [...doc.matchAll(/```(ts|vue)\n([\s\S]*?)```/g)].map((m) => m[2]!);
    expect(blocks.length).toBeGreaterThanOrEqual(5);
    const missing = blocks
      .flatMap((b) => b.split("\n"))
      .map(norm)
      .filter((l) => l && !l.startsWith("//"))
      .filter((l) => !playground.some((p) => p.includes(l)));
    // Lines the playground deliberately replaces (the doc says so next to the snippet).
    const differs = ["clientKey: 'biatec-dex', // must be listed on the Biatec tenant"];
    expect(missing).toEqual(differs);
  });
});
