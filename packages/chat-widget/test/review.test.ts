import { describe, it, expect, afterEach } from "vitest";
import { mount } from "@vue/test-utils";
import { createApp } from "vue";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import BiatecChatRoot from "../src/components/BiatecChatRoot.vue";
import { BiatecChat } from "../src/index";
import { resolveOptions } from "../src/options";

const opts = resolveOptions({ backendUrl: "https://chat.biatec.io/", clientKey: "k" });

afterEach(() => {
  document.body.innerHTML = "";
});

describe("accessibility", () => {
  it("labels the launcher by state", async () => {
    const w = mount(BiatecChatRoot, { props: { options: opts } });
    expect(w.get(".bc-launcher").attributes("aria-label")).toBe("Open Biatec Chat");
    await w.get(".bc-launcher").trigger("click");
    expect(w.get(".bc-launcher").attributes("aria-label")).toBe("Close Biatec Chat");
  });
  it("closes on Escape when focus is outside the panel", async () => {
    const w = mount(BiatecChatRoot, { props: { options: opts }, attachTo: document.body });
    await w.get(".bc-launcher").trigger("click");
    document.body.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    await w.vm.$nextTick();
    expect(w.find(".bc-panel").exists()).toBe(false);
    w.unmount();
  });
});

describe("plugin", () => {
  it("installing twice keeps a single host", () => {
    createApp({ render: () => null }).use(BiatecChat, { backendUrl: "https://x", clientKey: "k" });
    createApp({ render: () => null }).use(BiatecChat, { backendUrl: "https://x", clientKey: "k" });
    expect(document.querySelectorAll("[data-biatec-chat-host]").length).toBe(1);
  });
});

describe("style isolation", () => {
  it("every selector carries a class, so host bare-element rules such as `button` lose on specificity", () => {
    const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
    const selectors = [...css.matchAll(/(?:^|})\s*([^{}@]+)\{/g)].flatMap((m) => m[1]!.split(",").map((s: string) => s.trim()));
    for (const s of selectors) {
      const classes = s.match(/\.[\w-]+/g) ?? [];
      expect(classes.length, s).toBeGreaterThanOrEqual(1);
    }
  });
});

describe("published typings", () => {
  it("declare every public export of src/index.ts", () => {
    const src = readFileSync(resolve(process.cwd(), "src/index.ts"), "utf8");
    const emit = readFileSync(resolve(process.cwd(), "scripts/emit-types.mjs"), "utf8");
    const exported = [...src.matchAll(/export (?:const|\{)\s*(\w+)/g)].map((m) => m[1]);
    for (const name of exported) expect(emit, name).toContain(name);
    // option types come from src/options.ts through tsc, never hand-copied
    expect(emit).not.toContain("backendUrl");
  });
});
