import { describe, it, expect, afterEach, vi } from "vitest";
import { createApp } from "vue";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { BiatecChat } from "../src/index";

const frame = () => new Promise<void>((r) => requestAnimationFrame(() => r()));

afterEach(() => {
  document.body.innerHTML = "";
  vi.unstubAllGlobals();
});

describe("mobile focus trap in the shadow root", () => {
  it("lets Tab move focus inside the panel and wraps only at the ends", async () => {
    vi.stubGlobal("matchMedia", (q: string) => ({
      matches: q.includes("max-width"),
      addEventListener() {},
      removeEventListener() {},
    }));
    createApp({ render: () => null }).use(BiatecChat, { backendUrl: "https://x", clientKey: "k" });
    const shadow = document.querySelector<HTMLElement>("[data-biatec-chat-host]")!.shadowRoot!;
    shadow.querySelector<HTMLElement>(".bc-launcher")!.click();
    await frame();
    await new Promise((r) => setTimeout(r, 0));
    const root = shadow.querySelector<HTMLElement>(".biatec-chat")!;
    const items = [
      ...root.querySelectorAll<HTMLElement>(
        "button:not([disabled]), [href], input:not([disabled]), select, textarea, [tabindex]:not([tabindex='-1'])",
      ),
    ];
    expect(items.length).toBeGreaterThan(2);
    const tab = (shift = false) => {
      const e = new KeyboardEvent("keydown", { key: "Tab", shiftKey: shift, bubbles: true, cancelable: true });
      document.dispatchEvent(e);
      return e;
    };
    items[1].focus();
    expect(shadow.activeElement).toBe(items[1]);
    // A middle control is not hijacked back to the first one.
    expect(tab().defaultPrevented).toBe(false);
    expect(shadow.activeElement).toBe(items[1]);
    // Wraps at the ends.
    items[items.length - 1].focus();
    expect(tab().defaultPrevented).toBe(true);
    expect(shadow.activeElement).toBe(items[0]);
    expect(tab(true).defaultPrevented).toBe(true);
    expect(shadow.activeElement).toBe(items[items.length - 1]);
  });
});

describe("color scheme", () => {
  it("keeps color-scheme dark after the all:initial reset", () => {
    const css = readFileSync(resolve(__dirname, "../src/styles.css"), "utf8");
    expect(css).toMatch(/:host\s*\{[^}]*all:\s*initial;[^}]*color-scheme:\s*dark/);
  });
});
