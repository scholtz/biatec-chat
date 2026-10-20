import { afterEach, describe, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import axe from "axe-core";
import App from "../src/App.vue";
import { SECTIONS } from "../src/sections";
import { applyPageTheme, pageTheme } from "../src/theme";

let wrapper: VueWrapper | null = null;
afterEach(() => {
  wrapper?.unmount();
  wrapper = null;
  document.body.innerHTML = "";
  vi.restoreAllMocks();
});

function render(theme: "dark" | "light") {
  pageTheme.value = theme;
  applyPageTheme(theme);
  const root = document.createElement("div");
  document.body.appendChild(root);
  wrapper = mount(App, { props: { mode: "floating" }, attachTo: root });
  return wrapper;
}

describe("sections", () => {
  it("never offers a prompt for something that is coming next", () => {
    for (const s of SECTIONS) {
      for (const item of s.items) {
        if (item.status === "next") {
          expect(item.prompt, `${s.id}: ${item.label}`).toBeUndefined();
          expect(item.note, `${s.id}: ${item.label} must say why`).toBeTruthy();
        }
      }
    }
  });

  it("renders every section with a status on each item and a one-click prompt where it works", () => {
    const w = render("dark");
    for (const s of SECTIONS) {
      const el = w.find(`#${s.id}`);
      expect(el.exists()).toBe(true);
      expect(el.findAll(".item .status").length).toBe(s.items.length);
      expect(el.findAll("button.prompt").length).toBe(s.items.filter((i) => i.prompt && i.status !== "next").length);
    }
    expect(w.find("#modes").exists()).toBe(true);
    expect(w.find("#how").exists()).toBe(true);
  });

  it("sends a prompt through useBiatecChat().ask() and reports the result", async () => {
    const api = await import("@biatec/chat-widget");
    const spy = vi.spyOn(api.useBiatecChat(), "ask").mockResolvedValue("sent");
    const w = render("dark");
    await w.find("#guest button.prompt").trigger("click");
    await new Promise((r) => setTimeout(r, 0));
    expect(spy).toHaveBeenCalledWith(SECTIONS[0].items[0].prompt);
    expect(w.find("#guest .notice").text()).toContain("Sent to Biatec AI");
  });

  it("shows the not-ready notice when ask() rejects (backend unreachable)", async () => {
    const api = await import("@biatec/chat-widget");
    vi.spyOn(api.useBiatecChat(), "ask").mockRejectedValue(new TypeError("Failed to fetch"));
    const w = render("dark");
    await w.find("#guest button.prompt").trigger("click");
    await new Promise((r) => setTimeout(r, 0));
    expect(w.find("#guest .notice").text()).toContain("The chat is not ready yet");
  });
});

describe.each(["dark", "light"] as const)("accessibility (%s)", (theme) => {
  it("has no serious or critical axe violations", async () => {
    render(theme);
    expect(document.documentElement.dataset.theme).toBe(theme);
    // jsdom has no layout, so contrast is checked in a real browser (docs/demo-site.md).
    const res = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    const serious = res.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
    expect(serious.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`)).toEqual([]);
  });
});
