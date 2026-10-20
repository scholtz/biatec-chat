import { describe, it, expect, afterEach, vi } from "vitest";
import { mount, flushPromises } from "@vue/test-utils";
import { defineComponent, h, nextTick } from "vue";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import type { ChatEvent, SessionState } from "@biatec/chat-protocol";
import BiatecChatRoot from "../src/components/BiatecChatRoot.vue";
import BiatecChatInline from "../src/components/shell/BiatecChatInline.vue";
import { resolveOptions, type BiatecChatOptions } from "../src/options";
import type { ChatServices } from "../src/components/chat/useChat";
import { useBiatecChat, resetHostApi } from "../src/api";
import { agentModeOf } from "../src/components/shell/agentMode";
import { setLocale } from "../src/i18n";

const base: BiatecChatOptions = { backendUrl: "https://chat.biatec.io", clientKey: "k" };
const opts = resolveOptions(base);
const css = readFileSync(resolve(__dirname, "../src/components/shell/shell.css"), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
const rule = (selector: string) => {
  const i = css.indexOf(selector + " {");
  expect(i, selector).toBeGreaterThanOrEqual(0);
  return css.slice(i, css.indexOf("}", i));
};

afterEach(() => {
  document.body.innerHTML = "";
  resetHostApi();
  setLocale("en");
  vi.unstubAllGlobals();
});

const anon: SessionState = { sessionId: "s", tier: "Anonymous", scopes: [], expiresAt: "2030-01-01T00:00:00Z" };
const authed: SessionState = {
  sessionId: "s",
  tier: "Authenticated",
  email: "alice@example.com",
  primaryAddress: "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567ABCDEFGHIJKLMNOPQRSTUVWXYZ",
  scopes: ["openid"],
  expiresAt: "2030-01-01T00:00:00Z",
};

function controlledStream() {
  const queue: (ChatEvent | "end")[] = [];
  let wake: (() => void) | null = null;
  return {
    push(e: ChatEvent | "end") {
      queue.push(e);
      wake?.();
    },
    async *gen(): AsyncGenerator<ChatEvent> {
      for (;;) {
        while (!queue.length) await new Promise<void>((r) => (wake = r));
        const e = queue.shift()!;
        if (e === "end") return;
        yield e;
      }
    },
  };
}

function services(session: SessionState = anon, send?: ChatServices["transport"]["send"]): ChatServices {
  return {
    transport: {
      open: vi.fn(async () => "success" as const),
      retryOnSignIn: vi.fn(async () => true),
      send: send ?? vi.fn(async function* () {}),
      dispose: vi.fn(),
    },
    auth: {
      session: vi.fn(async () => session),
      startLogin: vi.fn(async () => null),
      startElevate: vi.fn(async () => null),
      status: vi.fn(async () => "Complete" as const),
      logoutUrl: vi.fn(() => "https://chat.biatec.io/auth/logout"),
    },
    pollMs: 1,
    timeoutMs: 50,
  };
}

function viewport(width: number) {
  vi.stubGlobal("matchMedia", (q: string) => {
    const max = /max-width:\s*([\d.]+)px/.exec(q);
    return { matches: max ? width <= Number(max[1]) : false, addEventListener() {}, removeEventListener() {} };
  });
}

const mountRoot = (svc = services(), o = opts) =>
  mount(BiatecChatRoot, { props: { options: o, services: svc }, attachTo: document.body });

describe("shell walk: launcher, panel, minimized bar, workspace", () => {
  it("moves through the modes with the design geometry", async () => {
    viewport(1280);
    const w = mountRoot();
    const launcher = w.get(".bc-launcher");
    expect(launcher.attributes("aria-label")).toBe("Ask Biatec AI");
    expect(w.find(".bc-launcher-wrap .bc-symbol").attributes("data-variant")).toBe("mono-dark");

    await launcher.trigger("click");
    await flushPromises();
    const panel = () => w.get(".bc-panel");
    expect(panel().attributes("data-mode")).toBe("floating");
    expect(panel().attributes("data-fullscreen")).toBeUndefined();
    expect(w.find(".bc-launcher").exists()).toBe(false);
    // Focus moved to the composer.
    expect(document.activeElement).toBe(w.get(".bc-panel textarea").element);

    await w.get("[data-testid=minimize] button").trigger("click");
    expect(w.find(".bc-panel").exists()).toBe(false);
    expect(w.get("[data-testid=minimized-status]").text()).toBe("Analyze only");
    await w.get("[data-testid=restore]").trigger("click");
    expect(panel().attributes("data-mode")).toBe("floating");

    await w.get("[data-testid=expand] button").trigger("click");
    expect(panel().attributes("data-mode")).toBe("workspace");
    expect(panel().attributes("aria-modal")).toBe("true");
    expect(w.find("[data-testid=minimize]").exists()).toBe(false);
    expect(w.get("[data-testid=expand] button").attributes("aria-label")).toBe("Exit full screen");
    await w.get("[data-testid=expand] button").trigger("click");
    expect(panel().attributes("data-mode")).toBe("floating");

    await w.get("[data-testid=close] button").trigger("click");
    await flushPromises();
    expect(w.find(".bc-panel").exists()).toBe(false);
    expect(document.activeElement).toBe(w.get(".bc-launcher").element);
    w.unmount();
  });

  it("geometry in the stylesheet: launcher 60 px at 24 px, floating 420 x 700, inline without shadow, workspace inset 0", () => {
    const launcher = rule(".biatec-chat .bc-launcher");
    expect(launcher).toMatch(/width: 60px/);
    expect(launcher).toMatch(/height: 60px/);
    expect(launcher).toMatch(/border-radius: var\(--radius-md\)/);
    expect(rule(".biatec-chat .bc-launcher-wrap")).toMatch(/right: var\(--bc-offset-right, var\(--space-6\)\)/);
    const floating = rule('.biatec-chat .bc-panel[data-mode="floating"]');
    expect(floating).toMatch(/width: min\(420px, calc\(100vw - 32px\)\)/);
    expect(floating).toMatch(/box-shadow: var\(--shadow-lg\)/);
    expect(rule('.biatec-chat .bc-panel[data-mode="inline"]')).toMatch(/box-shadow: none/);
    expect(css).toMatch(/\.bc-panel\[data-mode="workspace"\],\s*\.biatec-chat \.bc-panel\[data-fullscreen\] \{[^}]*inset: 0/);
    expect(rule(".biatec-chat .bc-minbar")).toMatch(/width: min\(320px, calc\(100vw - 32px\)\)/);
    expect(css).toMatch(/animation: bc-shell-in var\(--motion-slow\)/);
    expect(css).toMatch(/prefers-reduced-motion: reduce\)[^@]*animation: none/);
  });

  it("floating panel height leaves room for --bc-offset-top and --bc-offset-bottom (T-104)", () => {
    const floating = rule('.biatec-chat .bc-panel[data-mode="floating"]');
    const expr = /height: (min\([^;]*\));/.exec(floating)?.[1];
    expect(expr, "floating height").toBeDefined();
    // Resolve the declaration the way a browser would for a given viewport height and host variables (all in px).
    const heightAt = (vh: number, host: Record<string, number>) => {
      const space6 = 24; // --space-6 in tokens.css
      let e = expr!.replace(/\s+/g, " ");
      // Innermost var() first, so a fallback that is itself a var() resolves.
      for (let guard = 0; /var\(/.test(e) && guard < 10; guard++)
        e = e.replace(/var\((--[\w-]+)(?:, ([^()]*))?\)/g, (_m, name: string, fb?: string) =>
          name in host ? `${host[name]}px` : name === "--space-6" ? `${space6}px` : (fb ?? "0px"),
        );
      e = e.replace(/100vh/g, `${vh}px`).replace(/(\d+(?:\.\d+)?)px/g, "$1").replace(/calc/g, "");
      const [a, b] = /^min\((.*?), (.*)\)$/.exec(e)!.slice(1);
      return Math.min(Number(a), Function(`return (${b});`)() as number);
    };
    // Defaults keep the T-063 geometry: min(700px, 100vh - 48px).
    expect(heightAt(1080, {})).toBe(700);
    expect(heightAt(600, {})).toBe(552);
    // Docusaurus at 800 px high: 60 px navbar, launcher lifted 4.5rem above "Back to top".
    const host = { "--bc-offset-top": 60, "--bc-offset-bottom": 72 };
    const height = heightAt(800, host);
    const top = 800 - host["--bc-offset-bottom"] - height;
    expect(top).toBeGreaterThanOrEqual(host["--bc-offset-top"]);
    expect(top).toBe(60 + 24);
    // Without the top offset the old rule (100vh - 48px) would have put the top at 800 - 72 - 700 = 28 px, under the navbar.
    expect(800 - 72 - Math.min(700, 800 - 48)).toBeLessThan(60);
    // Inline and workspace / full screen frames are unchanged.
    expect(rule('.biatec-chat .bc-panel[data-mode="inline"]')).toMatch(/height: min\(700px, calc\(100vh - 48px\)\)/);
    expect(css).toMatch(/\.bc-panel\[data-fullscreen\] \{[^}]*inset: 0;[^}]*height: 100dvh/);
  });

  it("every selector is scoped to the widget root or :host", () => {
    const sel = [...css.matchAll(/(?:^|[};])\s*([^{}@;]+)\{/g)].map((m) => m[1]!.trim()).filter((s) => !/^(from|to)$/.test(s));
    for (const group of sel) for (const s of group.split(",")) expect(s.trim()).toMatch(/^(\.biatec-chat|:host)/);
  });
});

describe("viewport", () => {
  it("at 375 px an open panel is full screen, at 1280 px it floats", async () => {
    viewport(375);
    const small = mountRoot();
    await small.get(".bc-launcher").trigger("click");
    expect(small.get(".bc-panel").attributes("data-fullscreen")).toBe("");
    expect(small.get(".bc-panel").attributes("aria-modal")).toBe("true");
    small.unmount();

    viewport(1280);
    const wide = mountRoot();
    await wide.get(".bc-launcher").trigger("click");
    expect(wide.get(".bc-panel").attributes("data-fullscreen")).toBeUndefined();
    expect(wide.get(".bc-panel").attributes("data-mode")).toBe("floating");
    wide.unmount();
  });
});

describe("header and status bar", () => {
  it("shows Guest and Analyze only before sign-in", async () => {
    const w = mountRoot(services(anon));
    await w.get(".bc-launcher").trigger("click");
    await flushPromises();
    expect(w.get("[data-testid=tier]").text()).toBe("Guest");
    expect(w.get("[data-testid=sign-in]").text()).toBe("Sign in");
    expect(w.get("[data-testid=agent-mode]").text()).toBe("Analyze only");
    w.unmount();
  });

  it("shows email and short primary address, and Prepare for approval, when signed in", async () => {
    const w = mountRoot(services(authed));
    await w.get(".bc-launcher").trigger("click");
    await flushPromises();
    expect(w.get("[data-testid=tier]").text()).toBe("alice@example.com · ABCDEF…WXYZ");
    expect(w.find("[data-testid=sign-out]").exists()).toBe(true);
    expect(w.get("[data-testid=agent-mode]").text()).toBe("Prepare for approval");
    w.unmount();
  });

  it("reads the agent mode from the session", () => {
    expect(agentModeOf(null)).toBe("analyze");
    expect(agentModeOf({ ...anon, agentMode: "prepare" })).toBe("analyze");
    expect(agentModeOf(authed)).toBe("prepare");
    expect(agentModeOf({ ...authed, agentMode: "analyze" })).toBe("analyze");
    expect(agentModeOf({ ...authed, paused: true })).toBe("paused");
  });

  it("status pill and Activity are buttons with aria-expanded / aria-pressed; icon buttons are labelled with tooltips", async () => {
    const w = mountRoot();
    await w.get(".bc-launcher").trigger("click");
    const pill = w.get("[data-testid=status-pill]");
    expect(pill.element.tagName).toBe("BUTTON");
    expect(pill.attributes("aria-expanded")).toBe("false");
    expect(pill.attributes("aria-label")).toBe("Agent status: Analyze only. Change permissions");
    await pill.trigger("click");
    expect(w.get("[data-testid=status-pill]").attributes("aria-expanded")).toBe("true");
    const act = w.get("[data-testid=activity-toggle]");
    expect(act.element.tagName).toBe("BUTTON");
    expect(act.attributes("aria-pressed")).toBe("false");
    await act.trigger("click");
    expect(w.get("[data-testid=activity-toggle]").attributes("aria-pressed")).toBe("true");

    const icons = w.findAll(".bc-shell-actions .bc-iconbtn");
    expect(icons).toHaveLength(3);
    for (const b of icons) {
      expect(b.attributes("aria-label")).toBeTruthy();
      await b.trigger("focus");
      expect(b.element.parentElement!.querySelector("[role=tooltip]")!.textContent).toBe(b.attributes("aria-label"));
      await b.trigger("blur");
    }
    w.unmount();
  });

  it("Esc closes the permissions state, then the Activity sheet, and keeps the panel while the composer has text", async () => {
    const w = mountRoot();
    await w.get(".bc-launcher").trigger("click");
    await w.get("[data-testid=status-pill]").trigger("click");
    await w.get("[data-testid=activity-toggle]").trigger("click");
    const esc = async () => {
      document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
      await nextTick();
    };
    await esc();
    expect(w.get("[data-testid=status-pill]").attributes("aria-expanded")).toBe("false");
    expect(w.get("[data-testid=activity-toggle]").attributes("aria-pressed")).toBe("true");
    await esc();
    expect(w.get("[data-testid=activity-toggle]").attributes("aria-pressed")).toBe("false");
    await w.get(".bc-panel textarea").setValue("half a message");
    await esc();
    expect(w.find(".bc-panel").exists()).toBe(true);
    await w.get(".bc-panel textarea").setValue("");
    await esc();
    expect(w.find(".bc-panel").exists()).toBe(false);
    w.unmount();
  });
});

describe("useBiatecChat()", () => {
  it("ask opens the panel and sends the prompt once as a visible user message; a second ask while streaming is refused visibly", async () => {
    const s = controlledStream();
    const send = vi.fn(() => s.gen());
    const svc = services(anon, send);
    const w = mountRoot(svc);
    expect(w.find(".bc-panel").exists()).toBe(false);

    const api = useBiatecChat();
    const first = api.ask("Swap 50 USDC to ALGO");
    const second = api.ask("Swap 50 USDC to ALGO");
    expect(await second).toBe("busy");
    expect(await first).toBe("sent");
    await flushPromises();
    expect(svc.transport.open).toHaveBeenCalledTimes(1);
    expect(w.find(".bc-panel").exists()).toBe(true);
    const users = w.findAll(".bc-msg-user");
    expect(users).toHaveLength(1);
    expect(users[0]!.text()).toBe("Swap 50 USDC to ALGO");
    expect(send).toHaveBeenCalledTimes(1);
    expect((send.mock.calls[0] as unknown[])[0]).toMatchObject({ message: "Swap 50 USDC to ALGO" });

    // Still streaming: refused, and the panel says why. Nothing hidden is sent.
    expect(await api.ask("Another one")).toBe("busy");
    expect(w.get("[data-testid=shell-notice]").text()).toContain("not sent");
    expect(send).toHaveBeenCalledTimes(1);
    expect(w.findAll(".bc-msg-user")).toHaveLength(1);

    s.push({ type: "text_delta", text: "Here is a preview." } as ChatEvent);
    s.push("end");
    await flushPromises();
    expect(await api.ask("Another one")).toBe("sent");
    await flushPromises();
    expect(send).toHaveBeenCalledTimes(2);
    expect(w.findAll(".bc-msg-user")).toHaveLength(2);
    expect(w.find("[data-testid=shell-notice]").exists()).toBe(false);
    w.unmount();
  });

  it("open and close drive the panel; ask before any widget is mounted sends nothing", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    expect(await useBiatecChat().ask("hello")).toBe("unavailable");
    expect(await useBiatecChat().ask("   ")).toBe("empty");
    warn.mockRestore();
    const w = mountRoot();
    useBiatecChat().open();
    await nextTick();
    expect(w.find(".bc-panel").exists()).toBe(true);
    useBiatecChat().close();
    await nextTick();
    expect(w.find(".bc-panel").exists()).toBe(false);
    w.unmount();
  });

  it("setTheme and setLocale apply without remount", async () => {
    const w = mountRoot(services(), resolveOptions({ ...base, theme: "dark" }));
    const el = w.element as HTMLElement;
    expect(el.getAttribute("data-theme")).toBe("dark");
    useBiatecChat().setTheme("light");
    await flushPromises();
    expect(w.element).toBe(el);
    expect(el.getAttribute("data-theme")).toBe("light");
    useBiatecChat().setLocale("sk-SK");
    await nextTick();
    // lang announces the catalog that is shown, not the requested locale.
    expect(el.getAttribute("lang")).toBe("sk");
    expect(w.get(".bc-launcher").attributes("aria-label")).toBe("Opýtať sa Biatec AI");
    useBiatecChat().setLocale("de");
    await nextTick();
    // Unsupported locale: English strings and lang="en".
    expect(el.getAttribute("lang")).toBe("en");
    expect(w.get(".bc-launcher").attributes("aria-label")).toBe("Ask Biatec AI");
    w.unmount();
  });

  it("the page context is sent only with messages, after the panel was opened", async () => {
    const send = vi.fn(async function* () {});
    const svc = services(anon, send);
    useBiatecChat().setPageContext("Biatec DEX · testnet · pair ALGO/USDC");
    const w = mountRoot(svc);
    await flushPromises();
    expect(svc.transport.open).not.toHaveBeenCalled();
    expect(send).not.toHaveBeenCalled();
    expect(await useBiatecChat().ask("What pool is this?")).toBe("sent");
    await flushPromises();
    expect((send.mock.calls[0] as unknown[])[0]).toMatchObject({
      context: { product: "Biatec DEX · testnet · pair ALGO/USDC", locale: "en" },
    });
    useBiatecChat().setPageContext(undefined);
    await useBiatecChat().ask("And now?");
    await flushPromises();
    expect((send.mock.calls[1] as unknown[])[0]).toMatchObject({ context: null });
    w.unmount();
  });

  it("unread badge counts a reply that finished while the panel was closed, and clears on open", async () => {
    const s = controlledStream();
    const w = mountRoot(services(anon, vi.fn(() => s.gen())));
    await useBiatecChat().ask("Explain the blockchain");
    await flushPromises();
    useBiatecChat().close();
    s.push({ type: "text_delta", text: "It is a ledger." } as ChatEvent);
    s.push("end");
    await flushPromises();
    expect(w.get("[data-testid=unread]").text()).toBe("1");
    await w.get(".bc-launcher").trigger("click");
    await flushPromises();
    useBiatecChat().close();
    await nextTick();
    expect(w.find("[data-testid=unread]").exists()).toBe(false);
    w.unmount();
  });
});

describe("inline mode", () => {
  it("BiatecChatInline renders the shared panel in its own shadow root and the floating launcher hides", async () => {
    const svc = services();
    const Host = defineComponent({
      setup: () => () =>
        h("div", [h(BiatecChatRoot, { options: opts, services: svc }), h(BiatecChatInline, { class: "w-full" })]),
    });
    const w = mount(Host, { attachTo: document.body });
    await flushPromises();
    expect(w.find(".bc-launcher").exists()).toBe(false);
    const host = document.querySelector<HTMLElement>("[data-biatec-chat-inline-host]")!;
    expect(host.classList.contains("w-full")).toBe(true);
    const shadow = host.shadowRoot!;
    const panel = shadow.querySelector<HTMLElement>(".bc-panel")!;
    expect(panel.getAttribute("data-mode")).toBe("inline");
    expect(shadow.querySelector("[data-testid=close]")).toBeNull();
    expect(shadow.querySelector("[data-testid=minimize]")).toBeNull();
    expect(shadow.querySelector("style")!.textContent).toContain('[data-mode="inline"]');
    // The inline panel is on screen, so the probe ran and ask sends into it.
    expect(svc.transport.open).toHaveBeenCalledTimes(1);
    expect(await useBiatecChat().ask("Add liquidity")).toBe("sent");
    await flushPromises();
    expect(shadow.querySelectorAll(".bc-msg-user")).toHaveLength(1);
    w.unmount();
  });

  it("mode 'inline' has no launcher, and ask without an inline panel sends nothing", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const svc = services();
    const w = mountRoot(svc, resolveOptions({ ...base, mode: "inline" }));
    expect(w.find(".bc-launcher").exists()).toBe(false);
    expect(await useBiatecChat().ask("hi")).toBe("unavailable");
    expect(svc.transport.send).not.toHaveBeenCalled();
    warn.mockRestore();
    w.unmount();
  });

  it("mode 'workspace' opens full screen", async () => {
    const w = mountRoot(services(), resolveOptions({ ...base, mode: "workspace" }));
    await w.get(".bc-launcher").trigger("click");
    expect(w.get(".bc-panel").attributes("data-mode")).toBe("workspace");
    w.unmount();
  });
});

describe("review fixes", () => {
  it("an inline panel never becomes the workspace, even when the shared core is expanded", async () => {
    viewport(1280);
    const svc = services();
    const Host = defineComponent({
      setup: () => () =>
        h("div", [h(BiatecChatRoot, { options: resolveOptions({ ...base, mode: "workspace" }), services: svc }), h(BiatecChatInline)]),
    });
    const w = mount(Host, { attachTo: document.body });
    await flushPromises();
    const shadow = document.querySelector<HTMLElement>("[data-biatec-chat-inline-host]")!.shadowRoot!;
    const panel = shadow.querySelector<HTMLElement>(".bc-panel")!;
    expect(panel.getAttribute("data-mode")).toBe("inline");
    expect(panel.hasAttribute("data-fullscreen")).toBe(false);
    expect(panel.getAttribute("aria-modal")).toBeNull();
    expect(shadow.querySelector("[data-testid=expand]")).toBeNull();
    w.unmount();
  });

  it("the refused-ask notice clears when the streaming reply finishes (inline, no close)", async () => {
    const s = controlledStream();
    const svc = services(anon, vi.fn(() => s.gen()));
    const Host = defineComponent({ setup: () => () => h(BiatecChatInline, { options: base, services: svc }) });
    const w = mount(Host, { attachTo: document.body });
    await flushPromises();
    const shadow = document.querySelector<HTMLElement>("[data-biatec-chat-inline-host]")!.shadowRoot!;
    expect(await useBiatecChat().ask("first")).toBe("sent");
    expect(await useBiatecChat().ask("second")).toBe("busy");
    await nextTick();
    expect(shadow.querySelector("[data-testid=shell-notice]")).toBeTruthy();
    s.push("end");
    await flushPromises();
    expect(shadow.querySelector("[data-testid=shell-notice]")).toBeNull();
    w.unmount();
  });

  it("closing from the minimized bar returns focus to the launcher", async () => {
    viewport(1280);
    const w = mountRoot();
    await w.get(".bc-launcher").trigger("click");
    await w.get("[data-testid=minimize] button").trigger("click");
    await flushPromises();
    expect(document.activeElement).toBe(w.get("[data-testid=restore]").element);
    useBiatecChat().close();
    await flushPromises();
    expect(document.activeElement).toBe(w.get(".bc-launcher").element);
    w.unmount();
  });
});
