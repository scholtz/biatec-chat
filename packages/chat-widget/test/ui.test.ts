import { describe, it, expect, afterEach, vi } from "vitest";
import { mount } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import { readFileSync } from "node:fs";
import { Button, IconButton, StatusBadge, Chip, Dialog, Toast, Spinner, BaiSymbol, STATUS_KEYS } from "../src/ui";
import { t } from "../src/i18n";

afterEach(() => {
  document.body.innerHTML = "";
  vi.restoreAllMocks();
});

describe("Button", () => {
  it("renders the three sizes and five variants", () => {
    for (const size of ["sm", "md", "lg"] as const) {
      expect(mount(Button, { props: { size }, slots: { default: "x" } }).attributes("data-size")).toBe(size);
    }
    for (const variant of ["primary", "secondary", "ghost", "danger", "soft"] as const) {
      expect(mount(Button, { props: { variant } }).attributes("data-variant")).toBe(variant);
    }
    expect(mount(Button).attributes("data-variant")).toBe("primary");
    expect(mount(Button).attributes("type")).toBe("button");
  });
  it("disabled blocks clicks", async () => {
    const w = mount(Button, { props: { disabled: true } });
    expect(w.attributes("disabled")).toBeDefined();
    await w.trigger("click");
    expect(w.emitted("click")).toBeUndefined();
  });
  it("loading sets aria-busy, disables and shows a decorative spinner", () => {
    const w = mount(Button, { props: { loading: true } });
    expect(w.attributes("aria-busy")).toBe("true");
    expect(w.attributes("disabled")).toBeDefined();
    expect(w.find(".bc-spinner").attributes("aria-hidden")).toBe("true");
    expect(mount(Button).attributes("aria-busy")).toBeUndefined();
  });
});

describe("IconButton", () => {
  it("carries aria-label and shows a tooltip on hover and on focus", async () => {
    const w = mount(IconButton, { props: { label: "Close chat" }, slots: { default: "x" }, attachTo: document.body });
    const b = w.get("button");
    expect(b.attributes("aria-label")).toBe("Close chat");
    expect(w.find("[role=tooltip]").exists()).toBe(false);
    await w.trigger("mouseenter");
    expect(w.get("[role=tooltip]").text()).toBe("Close chat");
    await w.trigger("mouseleave");
    expect(w.find("[role=tooltip]").exists()).toBe(false);
    await b.trigger("focus");
    expect(w.get("[role=tooltip]").attributes("id")).toBe(b.attributes("aria-describedby"));
    await b.trigger("blur");
    expect(w.find("[role=tooltip]").exists()).toBe(false);
    w.unmount();
  });
  it("requires a label", () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    mount(IconButton);
    expect(warn.mock.calls.flat().join(" ")).toMatch(/label/i);
  });
});

describe("StatusBadge", () => {
  // The rows of "Preview status mapping" in docs/design/README.md, in order.
  const labels: Record<string, string> = {
    awaitingConfirmation: "Awaiting your confirmation",
    awaitingScope: "Needs signing permission",
    awaitingApproval: "Waiting for approval on your phone",
    signing: "Approved · signing",
    submitted: "Submitted · waiting for confirmation",
    completed: "Completed",
    rejected: "Rejected on your phone",
    failed: "Failed",
    timedOut: "Approval timed out · nothing was signed",
    cancelled: "Cancelled",
    expired: "Expired",
  };
  it("covers every status of the mapping", () => {
    expect([...STATUS_KEYS].sort()).toEqual(Object.keys(labels).sort());
  });
  for (const [key, label] of Object.entries(labels)) {
    it(`${key}: word + icon + tone`, () => {
      const w = mount(StatusBadge, { props: { status: key as never } });
      expect(w.text()).toBe(label);
      expect(w.find("svg").exists()).toBe(true);
      expect(w.find("svg").attributes("aria-hidden")).toBe("true");
      expect(w.attributes("data-tone")).toBeTruthy();
    });
  }
  it("dashed only while awaiting", () => {
    expect(mount(StatusBadge, { props: { status: "awaitingApproval" } }).attributes("data-dashed")).toBe("true");
    expect(mount(StatusBadge, { props: { status: "completed" } }).attributes("data-dashed")).toBe("false");
  });
});

describe("Chip, Spinner, Toast, BaiSymbol", () => {
  it("Chip is static by default and a native toggle button when interactive", () => {
    expect(mount(Chip, { slots: { default: "BTC" } }).element.tagName).toBe("SPAN");
    const w = mount(Chip, { props: { interactive: true, selected: true }, slots: { default: "BTC" } });
    expect(w.element.tagName).toBe("BUTTON");
    expect(w.attributes("aria-pressed")).toBe("true");
  });
  it("Spinner announces Loading unless decorative", () => {
    expect(mount(Spinner).attributes("aria-label")).toBe("Loading");
    expect(mount(Spinner, { props: { decorative: true } }).attributes("aria-hidden")).toBe("true");
  });
  it("Toast is a polite live region present even when empty, and emits dismiss", async () => {
    const w = mount(Toast, { props: { messages: [] } });
    expect(w.attributes("aria-live")).toBe("polite");
    await w.setProps({ messages: [{ id: 1, text: "Copied" }] });
    expect(w.text()).toContain("Copied");
    await w.get("button").trigger("click");
    expect(w.emitted("dismiss")![0]).toEqual([1]);
  });
  it("BaiSymbol uses the small variant at 24 px or less and has an accessible name", () => {
    expect(mount(BaiSymbol, { props: { size: 24 } }).attributes("data-variant")).toBe("small");
    expect(mount(BaiSymbol, { props: { size: 40 } }).attributes("data-variant")).toBe("default");
    const m = mount(BaiSymbol, { props: { variant: "mono-dark", size: 44 } });
    expect(m.attributes("data-variant")).toBe("mono-dark");
    expect(m.attributes("aria-label")).toBe("Biatec AI");
  });
});

describe("Dialog", () => {
  const Host = defineComponent({
    setup() {
      const open = ref(false);
      return () =>
        h("div", [
          h("button", { id: "opener", onClick: () => (open.value = true) }, "open"),
          h(Dialog, { open: open.value, title: "Review", onClose: () => (open.value = false) }, () => [
            h("button", { id: "a" }, "A"),
            h("button", { id: "b" }, "B"),
          ]),
        ]);
    },
  });
  const flush = async () => {
    await Promise.resolve();
    await new Promise((r) => setTimeout(r, 0));
  };

  it("is a labelled modal, focuses inside, traps Tab, closes on Esc and returns focus", async () => {
    const w = mount(Host, { attachTo: document.body });
    const opener = document.getElementById("opener")!;
    opener.focus();
    await opener.click();
    await flush();
    const dlg = document.querySelector<HTMLElement>("[role=dialog]")!;
    expect(dlg.getAttribute("aria-modal")).toBe("true");
    expect(document.getElementById(dlg.getAttribute("aria-labelledby")!)!.textContent).toBe("Review");
    const a = document.getElementById("a")!;
    const b = document.getElementById("b")!;
    expect(document.activeElement).toBe(a);

    b.focus();
    const tab = new KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true });
    b.dispatchEvent(tab);
    expect(tab.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(a);

    const back = new KeyboardEvent("keydown", { key: "Tab", shiftKey: true, bubbles: true, cancelable: true });
    a.dispatchEvent(back);
    expect(back.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(b);

    b.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    await flush();
    expect(document.querySelector("[role=dialog]")).toBeNull();
    expect(document.activeElement).toBe(opener);
    w.unmount();
  });
});

describe("i18n", () => {
  it("t() resolves the English catalog", () => {
    expect(t("ui.close")).toBe("Close");
    expect(t("status.completed")).toBe("Completed");
  });
});

describe("no raw hex in primitives", () => {
  it("SFCs under src/ui contain no hex colour literal", () => {
    for (const f of ["Button", "IconButton", "StatusBadge", "Chip", "Dialog", "Toast", "Spinner", "BaiSymbol"]) {
      expect(readFileSync(`src/ui/${f}.vue`, "utf8"), f).not.toMatch(/#[0-9a-fA-F]{3,8}\b/);
    }
  });
});
