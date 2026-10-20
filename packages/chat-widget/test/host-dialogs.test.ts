import { describe, it, expect, afterEach, vi } from "vitest";
import { createApp } from "vue";
import { BiatecChat } from "../src/index";
import { resolvedLocale } from "../src/i18n";

const frame = () => new Promise<void>((r) => requestAnimationFrame(() => r()));

afterEach(() => {
  document.body.innerHTML = "";
  vi.unstubAllGlobals();
});

async function openWidget(opts: Record<string, unknown> = {}, small = false) {
  if (small)
    vi.stubGlobal("matchMedia", (q: string) => ({ matches: q.includes("max-width"), addEventListener() {}, removeEventListener() {} }));
  createApp({ render: () => null }).use(BiatecChat, { backendUrl: "https://x", clientKey: "k", ...opts });
  const shadow = document.querySelector<HTMLElement>("[data-biatec-chat-host]")!.shadowRoot!;
  shadow.querySelector<HTMLElement>(".bc-launcher")!.click();
  await frame();
  await new Promise((r) => setTimeout(r, 0));
  return shadow;
}

function hostDialog() {
  const d = document.createElement("div");
  d.setAttribute("role", "dialog");
  d.setAttribute("aria-modal", "true");
  d.innerHTML = '<button id="a">A</button><button id="b">B</button>';
  d.getClientRects = () => [{}] as unknown as DOMRectList;
  document.body.appendChild(d);
  return d;
}

describe("coexisting with host dialogs", () => {
  it("Esc inside a host dialog does not close the panel; Esc elsewhere still does", async () => {
    const shadow = await openWidget();
    expect(shadow.querySelector(".bc-panel")).not.toBeNull();
    const d = hostDialog();
    d.querySelector("button")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, composed: true }));
    await frame();
    expect(shadow.querySelector(".bc-panel")).not.toBeNull();
    d.remove();
    document.body.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    await frame();
    expect(shadow.querySelector(".bc-panel")).toBeNull();
  });

  it("the full-screen Tab trap yields while a host modal dialog is open", async () => {
    const shadow = await openWidget({}, true);
    const root = shadow.querySelector<HTMLElement>(".biatec-chat")!;
    const items = [...root.querySelectorAll<HTMLElement>("button:not([disabled]), [href], input:not([disabled]), select, textarea, [tabindex]:not([tabindex='-1'])")];
    const d = hostDialog();
    const b = d.querySelector<HTMLElement>("#b")!;
    b.focus();
    const e = new KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true, composed: true });
    b.dispatchEvent(e);
    expect(e.defaultPrevented).toBe(false);
    expect(document.activeElement).toBe(b);
    items[items.length - 1].focus();
    const e2 = new KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true });
    document.dispatchEvent(e2);
    expect(e2.defaultPrevented).toBe(false);
    d.remove();
    // Without the dialog the trap wraps again.
    items[items.length - 1].focus();
    const e3 = new KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true });
    document.dispatchEvent(e3);
    expect(e3.defaultPrevented).toBe(true);
  });

  it("lang is the resolved catalog locale", async () => {
    const shadow = await openWidget({ locale: "de" });
    expect(shadow.querySelector(".biatec-chat")!.getAttribute("lang")).toBe("en");
    expect(resolvedLocale("de-AT")).toBe("en");
    expect(resolvedLocale("en-GB")).toBe("en");
  });

  it("exports the chip", async () => {
    const m = await import("../src/index");
    expect(m.BiatecAiChip).toBeTruthy();
  });
});
