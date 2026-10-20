// The standalone entry (`@biatec/chat-widget/standalone`) as a non-Vue host uses it: one <script> tag or one
// mountBiatecChat() call, following `<html data-theme>`. The source tests run on a fresh checkout; the dist tests
// load the built IIFE into a fresh jsdom page exactly as a plain HTML page would, and run after `pnpm build` (CI).
import { describe, it, expect, afterEach, vi } from "vitest";
import { existsSync, readFileSync } from "node:fs";
import { gzipSync } from "node:zlib";
import { resolve } from "node:path";
import { createRequire } from "node:module";
import { autoMount, mountBiatecChat, unmount, useBiatecChat } from "../src/standalone";
import { resolveTheme, watchTheme } from "../src/theme";
import { resetHostApi } from "../src/api";
import { BiatecChat } from "../src/index";
import { createApp } from "vue";
import { currentWidget } from "../src/standalone/mount";
// @ts-expect-error plain .mjs build script without declarations
import { foreignSpecifiers } from "../scripts/check-dts.mjs";

// jsdom ships no types and @types/jsdom is not a dependency: a fresh page is all this file needs from it.
const { JSDOM } = createRequire(import.meta.url)("jsdom") as {
  JSDOM: new (html: string, o: object) => { window: Window & typeof globalThis };
};

const BACKEND = "https://chat.biatec.io";
const anon = { sessionId: "s", tier: "Anonymous", scopes: [], expiresAt: "2030-01-01T00:00:00Z" };
const authed = { ...anon, tier: "Authenticated", email: "alice@example.com", primaryAddress: "A".repeat(58), scopes: ["openid"] };

const shadow = () => document.querySelector<HTMLElement>("[data-biatec-chat-host]")?.shadowRoot ?? null;
const root = () => shadow()?.querySelector<HTMLElement>(".biatec-chat") ?? null;
const tick = () => new Promise<void>((r) => setTimeout(r, 0));

/** A backend for the cookie transport and the sign-in popup flow; the session turns authenticated after the popup. */
function fakeBackend() {
  let signedIn = false;
  const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    expect(url.origin).toBe(BACKEND);
    const method = (init?.method ?? "GET").toUpperCase();
    // The browser never receives a token: the widget only sends the cookie (credentials) and its client key.
    expect(init?.credentials).toBe("include");
    if (url.pathname === "/session/probe") return json({ cookieReturned: true });
    if (url.pathname === "/session") return json(signedIn ? authed : anon);
    if (url.pathname === "/auth/popup" && method === "POST") return json({ authRequestId: "r1", loginUrl: `${BACKEND}/auth/login?req=r1&nonce=n` });
    if (url.pathname === "/auth/popup/r1") {
      signedIn = true;
      return json({ status: "Complete" });
    }
    return json({}, 404);
  });
  return fetchMock;
}

function scriptTag(attrs: Record<string, string>): HTMLScriptElement {
  const s = document.createElement("script");
  for (const [k, v] of Object.entries(attrs)) s.setAttribute(k, v);
  document.head.appendChild(s);
  return s;
}

afterEach(() => {
  unmount();
  resetHostApi();
  document.body.innerHTML = "";
  document.head.innerHTML = "";
  document.documentElement.removeAttribute("data-theme");
  document.documentElement.removeAttribute("lang");
  document.documentElement.className = "";
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("hostDarkAttribute", () => {
  it("name=value, bare name, and it replaces hostDarkClass", async () => {
    const html = document.documentElement;
    const o = { theme: "host" as const, hostDarkClass: "p-dark", hostDarkAttribute: "data-theme=dark" };
    html.classList.add("p-dark");
    expect(resolveTheme(o)).toBe("light");
    html.setAttribute("data-theme", "dark");
    expect(resolveTheme(o)).toBe("dark");
    html.setAttribute("data-theme", "light");
    expect(resolveTheme(o)).toBe("light");
    expect(resolveTheme({ ...o, hostDarkAttribute: "data-dark" })).toBe("light");
    html.setAttribute("data-dark", "");
    expect(resolveTheme({ ...o, hostDarkAttribute: "data-dark" })).toBe("dark");
    expect(resolveTheme({ ...o, hostDarkAttribute: 'data-theme="light"' })).toBe("dark");
    html.removeAttribute("data-dark");
    // Blank: the class watcher applies again.
    expect(resolveTheme({ ...o, hostDarkAttribute: "  " })).toBe("dark");

    const seen: string[] = [];
    const stop = watchTheme(o, (t) => seen.push(t));
    html.setAttribute("data-theme", "dark");
    await tick();
    stop();
    expect(seen).toEqual(["light", "dark"]);
  });
});

describe("one script tag (source)", () => {
  it("shows the launcher, signs in, and follows a data-theme toggle on <html>", async () => {
    const fetchMock = fakeBackend();
    vi.stubGlobal("fetch", fetchMock);
    const popup = { location: { href: "" }, closed: false, close: vi.fn() };
    const openSpy = vi.fn(() => popup as unknown as Window);
    vi.stubGlobal("open", openSpy);
    document.documentElement.setAttribute("data-theme", "dark");
    document.documentElement.lang = "sk";

    autoMount(scriptTag({ "data-backend-url": BACKEND, "data-client-key": "biatec-clamm-docs" }));

    await tick();
    // Launcher shown, nothing touched the network on page load.
    expect(shadow()!.querySelector(".bc-launcher")).toBeTruthy();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(document.querySelector<HTMLElement>("[data-biatec-chat-host]")!.getAttribute("data-theme")).toBe("dark");
    // Locale from <html lang> (Docusaurus i18n).
    expect(root()!.getAttribute("lang")).toBe("sk");

    // Theme follows Docusaurus' toggle without remount.
    document.documentElement.setAttribute("data-theme", "light");
    await tick();
    expect(document.querySelector<HTMLElement>("[data-biatec-chat-host]")!.getAttribute("data-theme")).toBe("light");
    document.documentElement.setAttribute("data-theme", "dark");
    await tick();
    expect(document.querySelector<HTMLElement>("[data-biatec-chat-host]")!.getAttribute("data-theme")).toBe("dark");

    // Open, then sign in through the popup.
    shadow()!.querySelector<HTMLButtonElement>(".bc-launcher")!.click();
    await vi.waitFor(() => expect(shadow()!.querySelector("[data-testid=sign-in]")).toBeTruthy());
    shadow()!.querySelector<HTMLButtonElement>("[data-testid=sign-in]")!.click();
    await vi.waitFor(() => expect(shadow()!.querySelector("[data-testid=sign-out]")).toBeTruthy(), { timeout: 4000 });
    expect(openSpy).toHaveBeenCalled();
    expect(popup.location.href).toBe(`${BACKEND}/auth/login?req=r1&nonce=n`);
    expect(popup.close).toHaveBeenCalled();
    expect(shadow()!.querySelector("[data-testid=tier]")!.textContent).toContain("alice@example.com");
    const urls = fetchMock.mock.calls.map(([u]) => new URL(String(u)).pathname);
    expect(urls).toContain("/session/probe");
    expect(urls).toContain("/auth/popup");
  });

  it("does nothing without data-backend-url and reports a missing clientKey on the console only", () => {
    autoMount(scriptTag({}));
    autoMount(null);
    expect(document.querySelector("[data-biatec-chat-host]")).toBeNull();
    const err = vi.spyOn(console, "error").mockImplementation(() => {});
    expect(() => autoMount(scriptTag({ "data-backend-url": BACKEND }))).not.toThrow();
    expect(err).toHaveBeenCalled();
    expect(document.querySelector("[data-biatec-chat-host]")).toBeNull();
  });

  it("reads the other options from data-* attributes (data-chat-theme, not data-theme)", async () => {
    autoMount(scriptTag({ "data-backend-url": BACKEND, "data-client-key": "k", "data-chat-theme": "light", "data-locale": "en", "data-mode": "workspace" }));
    await tick();
    expect(document.querySelector("[data-biatec-chat-host]")!.getAttribute("data-theme")).toBe("light");
    expect(root()!.getAttribute("lang")).toBe("en");
  });
});

describe("mountBiatecChat", () => {
  it("returns the host API, is idempotent, unmounts, and accepts a container", async () => {
    const slot = document.createElement("section");
    slot.id = "slot";
    document.body.appendChild(slot);
    const a = mountBiatecChat("#slot", { backendUrl: BACKEND + "/", clientKey: "k" });
    expect(a.host!.parentElement).toBe(slot);
    const b = mountBiatecChat({ backendUrl: BACKEND, clientKey: "k" });
    expect(b.host).toBe(a.host);
    expect(document.querySelectorAll("[data-biatec-chat-host]")).toHaveLength(1);
    await tick();
    for (const k of ["open", "close", "ask", "setPageContext", "setTheme", "setLocale"] as const) expect(a[k]).toBe(useBiatecChat()[k]);
    // Without data-theme on <html> the standalone default is `auto` (no matchMedia in jsdom: dark).
    expect(a.host!.getAttribute("data-theme")).toBe("dark");
    a.setTheme("light");
    await tick();
    expect(a.host!.getAttribute("data-theme")).toBe("light");
    a.unmount();
    expect(document.querySelector("[data-biatec-chat-host]")).toBeNull();
    const c = mountBiatecChat({ backendUrl: BACKEND, clientKey: "k", theme: "dark" });
    expect(c.host).not.toBe(a.host);
    expect(c.host!.parentElement).toBe(document.body);
  });

  it("throws like the plugin on missing options and on an unknown container", () => {
    expect(() => mountBiatecChat({ backendUrl: "", clientKey: "k" })).toThrow(/backendUrl/);
    expect(() => mountBiatecChat("#nope", { backendUrl: BACKEND, clientKey: "k" })).toThrow(/container/);
  });
});

describe("one widget per page", () => {
  const base = { backendUrl: BACKEND, clientKey: "k" };

  it("unmounts a widget whose host element was removed without unmount() before mounting a new one", () => {
    mountBiatecChat(base);
    const old = currentWidget()!;
    const spy = vi.spyOn(old.app, "unmount");
    old.host.remove(); // a host page wiping <body> (or a SPA re-render) without calling unmount()
    const h = mountBiatecChat(base);
    expect(spy).toHaveBeenCalledOnce();
    expect(h.host).not.toBe(old.host);
    expect(document.querySelectorAll("[data-biatec-chat-host]")).toHaveLength(1);
  });

  it("refuses a [data-biatec-chat-host] element no copy registered, instead of returning a dead handle", () => {
    const stray = document.createElement("div");
    stray.setAttribute("data-biatec-chat-host", "");
    document.body.appendChild(stray);
    expect(() => mountBiatecChat(base)).toThrow(/already in the page/);
    const err = vi.spyOn(console, "error").mockImplementation(() => {});
    createApp({ render: () => null }).use(BiatecChat, base);
    expect(err).toHaveBeenCalledWith(expect.stringContaining("already in the page"));
    expect(document.querySelectorAll("[data-biatec-chat-host]")).toHaveLength(1);
  });

  it("reuses the Vue plugin's widget: the handle talks to it (ask is not 'unavailable')", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response("{}", { status: 503 })));
    createApp({ render: () => null }).use(BiatecChat, base);
    const pluginHost = document.querySelector("[data-biatec-chat-host]");
    const h = mountBiatecChat(base);
    expect(h.host).toBe(pluginHost);
    expect(await h.ask("hello")).toBe("sent");
  });

  it("answers through the API of the copy that mounted the widget (plugin + standalone bundles, script twice)", async () => {
    mountBiatecChat(base);
    const w = currentWidget()!;
    const otherCopy = { ...w.api, ask: vi.fn(async () => "sent" as const) };
    w.api = otherCopy; // what a second bundle's registry entry looks like from this copy
    const h = mountBiatecChat(base);
    await h.ask("hi");
    expect(otherCopy.ask).toHaveBeenCalledWith("hi");
  });

  it("warns when a second call brings different options or a container, and keeps the mounted widget", () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const a = mountBiatecChat(base);
    mountBiatecChat(base);
    expect(warn).not.toHaveBeenCalled();
    const b = mountBiatecChat({ ...base, locale: "sk" });
    expect(b.host).toBe(a.host);
    expect(warn).toHaveBeenCalledWith(expect.stringContaining("ignored"));
    warn.mockClear();
    const slot = document.createElement("section");
    document.body.appendChild(slot);
    mountBiatecChat(slot, base);
    expect(warn).toHaveBeenCalledOnce();
    expect(a.host!.parentElement).toBe(document.body);
  });
});

describe("standalone theme default", () => {
  const base = { backendUrl: BACKEND, clientKey: "k" };
  const stubScheme = (light: boolean) =>
    vi.stubGlobal("matchMedia", (q: string) => ({ matches: q.includes("light") ? light : false, media: q, addEventListener() {}, removeEventListener() {} }));

  it("a page without <html data-theme> follows prefers-color-scheme", async () => {
    stubScheme(true);
    const h = mountBiatecChat(base);
    await tick();
    expect(h.host!.getAttribute("data-theme")).toBe("light");
    h.unmount();
    stubScheme(false);
    const d = mountBiatecChat(base);
    await tick();
    expect(d.host!.getAttribute("data-theme")).toBe("dark");
  });

  it("a page with <html data-theme> follows the attribute, not the OS", async () => {
    stubScheme(true);
    document.documentElement.setAttribute("data-theme", "dark");
    const h = mountBiatecChat(base);
    await tick();
    expect(h.host!.getAttribute("data-theme")).toBe("dark");
  });

  it("explicit theme 'host' without a class or attribute watches data-theme=dark", async () => {
    stubScheme(false);
    const h = mountBiatecChat({ ...base, theme: "host" });
    await tick();
    expect(h.host!.getAttribute("data-theme")).toBe("light");
    document.documentElement.setAttribute("data-theme", "dark");
    await tick();
    expect(h.host!.getAttribute("data-theme")).toBe("dark");
  });
});

describe("self-contained declaration check (scripts/check-dts.mjs)", () => {
  it("catches every form of reference to a package other than vue", () => {
    const text = [
      'import type { A } from "@biatec/chat-protocol";',
      'export * from "lodash";',
      'type B = import("zod").Z;',
      'import "side-effect-pkg";',
      '/// <reference types="node" />',
      'import type { Ref } from "vue";',
      'export type { X } from "./options";',
      '/// <reference path="./local.d.ts" />',
    ].join("\n");
    expect(foreignSpecifiers(text)).toEqual(["@biatec/chat-protocol", "lodash", "zod", "side-effect-pkg", "node"]);
  });
});

const IIFE = resolve(__dirname, "../dist/standalone/biatec-chat.standalone.iife.js");
const ESM = resolve(__dirname, "../dist/standalone/biatec-chat.standalone.js");
/** Gzip budget of the one-script-tag bundle (Vue, vue-i18n, icons and CSS included); see the package README. */
const IIFE_GZIP_BUDGET = 110 * 1024;

describe.skipIf(!existsSync(IIFE))("built standalone bundle (dist)", () => {
  it("mounts from its own <script> tag in a plain HTML page and follows <html data-theme>", async () => {
    const code = readFileSync(IIFE, "utf8");
    const dom = new JSDOM(`<!doctype html><html data-theme="dark" lang="en"><head></head><body><h1>Docs</h1></body></html>`, {
      runScripts: "dangerously",
      pretendToBeVisual: true,
      url: "https://beta.dex.biatec.io/docs/intro",
    });
    const { window } = dom;
    const s = window.document.createElement("script");
    s.setAttribute("data-backend-url", BACKEND);
    s.setAttribute("data-client-key", "biatec-clamm-docs");
    s.textContent = code;
    window.document.body.appendChild(s);
    // The page is still parsing, so the tag mounts on DOMContentLoaded, as a <script> in <head> would.
    await vi.waitFor(() => expect(window.document.querySelector("[data-biatec-chat-host]")).toBeTruthy());
    const host = window.document.querySelector<HTMLElement>("[data-biatec-chat-host]");
    await new Promise((r) => window.setTimeout(r, 0));
    expect(host!.shadowRoot!.querySelector(".bc-launcher")).toBeTruthy();
    expect(host!.shadowRoot!.querySelector("style")!.textContent).toContain("--color-brand-primary");
    expect(host!.getAttribute("data-theme")).toBe("dark");
    window.document.documentElement.setAttribute("data-theme", "light");
    await new Promise((r) => window.setTimeout(r, 0));
    expect(host!.getAttribute("data-theme")).toBe("light");
    // Global for hosts that call it later.
    const g = (window as unknown as { BiatecChat: Record<string, unknown> }).BiatecChat;
    expect(typeof g.mountBiatecChat).toBe("function");
    expect(typeof g.useBiatecChat).toBe("function");
    window.close();
  });

  it("bundles Vue, ships no process.env reference, and stays within the gzip budget", () => {
    for (const f of [IIFE, ESM]) {
      const code = readFileSync(f, "utf8");
      expect(code).not.toMatch(/\bprocess\.env\b/);
      expect(code).not.toMatch(/from\s*["']vue["']/);
    }
    expect(gzipSync(readFileSync(IIFE)).length).toBeLessThan(IIFE_GZIP_BUDGET);
  });

  it("the ES build has no side effect when imported without a DOM (SSR)", async () => {
    const { execFileSync } = await import("node:child_process");
    const out = execFileSync(process.execPath, ["--input-type=module", "-e", `const m = await import(${JSON.stringify("file://" + ESM)}); const h = m.mountBiatecChat({ backendUrl: "https://x", clientKey: "k" }); console.log(typeof m.mountBiatecChat, h.host);`], { encoding: "utf8" });
    expect(out.trim()).toBe("function null");
  });
});

describe.skipIf(!existsSync(resolve(__dirname, "../dist/index.d.ts")))("published types", () => {
  it("dist/standalone/index.d.ts matches the source entry (tsc -p test-types)", async () => {
    const { execFileSync } = await import("node:child_process");
    const vueTsc = createRequire(import.meta.url).resolve("vue-tsc/bin/vue-tsc.js");
    // Throws with the compiler output when an export's name or type differs.
    execFileSync(process.execPath, [vueTsc, "--noEmit", "-p", resolve(__dirname, "../test-types")], { encoding: "utf8" });
  }, 120_000);

  it("never reference @biatec/chat-protocol", () => {
    for (const f of ["index.d.ts", "options.d.ts", "api/types.d.ts", "standalone/index.d.ts", "standalone/types.d.ts"]) {
      expect(readFileSync(resolve(__dirname, "../dist", f), "utf8")).not.toContain("@biatec/");
    }
  });
});
