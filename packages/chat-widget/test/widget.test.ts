import { describe, it, expect, afterEach } from "vitest";
import { mount } from "@vue/test-utils";
import { createApp } from "vue";
import BiatecChatRoot from "../src/components/BiatecChatRoot.vue";
import { BiatecChat } from "../src/index";
import { resolveOptions } from "../src/options";

const opts = resolveOptions({ backendUrl: "https://chat.biatec.io/", clientKey: "k" });

afterEach(() => {
  document.body.innerHTML = "";
});

describe("options", () => {
  it("applies defaults and trims trailing slash", () => {
    expect(opts).toMatchObject({
      backendUrl: "https://chat.biatec.io",
      locale: "en",
      theme: "auto",
      suggestions: "default",
      autoMount: true,
    });
    expect(opts.pageContext).toBeUndefined();
  });
  it("requires backendUrl and clientKey", () => {
    expect(() => resolveOptions({ backendUrl: "", clientKey: "k" })).toThrow();
    expect(() => resolveOptions({ backendUrl: "x", clientKey: "" })).toThrow();
  });
});

describe("launcher and panel", () => {
  it("renders a labelled native button launcher and no panel", () => {
    const w = mount(BiatecChatRoot, { props: { options: opts } });
    const b = w.get("button.bc-launcher");
    expect(b.element.tagName).toBe("BUTTON");
    expect(b.attributes("aria-label")).toBeTruthy();
    expect(b.attributes("aria-expanded")).toBe("false");
    expect(w.find(".bc-panel").exists()).toBe(false);
  });
  it("opens on click and shows tier placeholder", async () => {
    const w = mount(BiatecChatRoot, { props: { options: opts } });
    await w.get(".bc-launcher").trigger("click");
    expect(w.find(".bc-panel").exists()).toBe(true);
    expect(w.get(".bc-launcher").attributes("aria-expanded")).toBe("true");
    expect(w.get("[data-testid=tier]").text()).toBeTruthy();
  });
  it("closes with Escape and the close button", async () => {
    const w = mount(BiatecChatRoot, { props: { options: opts }, attachTo: document.body });
    await w.get(".bc-launcher").trigger("click");
    await w.get(".bc-panel").trigger("keydown", { key: "Escape" });
    expect(w.find(".bc-panel").exists()).toBe(false);
    await w.get(".bc-launcher").trigger("click");
    await w.get(".bc-close").trigger("click");
    expect(w.find(".bc-panel").exists()).toBe(false);
    w.unmount();
  });
  it("scopes everything under the biatec-chat root", () => {
    const w = mount(BiatecChatRoot, { props: { options: opts } });
    expect(w.classes()).toContain("biatec-chat");
  });
});

describe("plugin", () => {
  it("mounts a separate host in body and reads nothing from the page", () => {
    document.title = "secret title";
    createApp({ render: () => null }).use(BiatecChat, { backendUrl: "https://x", clientKey: "k" });
    const host = document.querySelector("[data-biatec-chat-host]");
    expect(host?.querySelector(".biatec-chat")).toBeTruthy();
    expect(host?.textContent).not.toContain("secret title");
  });
  it("does not mount with autoMount false", () => {
    createApp({ render: () => null }).use(BiatecChat, { backendUrl: "https://x", clientKey: "k", autoMount: false });
    expect(document.querySelector("[data-biatec-chat-host]")).toBeNull();
  });
});
