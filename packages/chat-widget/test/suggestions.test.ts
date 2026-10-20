import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import ChatView from "../src/components/chat/ChatView.vue";
import { promptsFor } from "../src/components/chat/suggestions";

describe("promptsFor", () => {
  it("uses the built-in prompts when the tenant has none", () => {
    expect(promptsFor("Anonymous", "default", null)).toHaveLength(4);
    expect(promptsFor("Anonymous", "default", [])).toHaveLength(4);
    expect(promptsFor("Anonymous", "default")).toHaveLength(4);
  });

  it("shows the tenant's prompts, capped at four, to every tier", () => {
    const tenant = ["a", "b", "c", "d", "e"];
    for (const tier of ["Anonymous", "Authenticated"] as const) {
      const p = promptsFor(tier, "default", tenant);
      expect(p.map((x) => x.prompt)).toEqual(["a", "b", "c", "d"]);
    }
  });

  it("lets a host-supplied list win over the tenant's", () => {
    expect(promptsFor("Anonymous", ["host"], ["tenant"]).map((x) => x.prompt)).toEqual(["host"]);
  });
});

describe("ChatView wiring", () => {
  const base = { messages: [], tier: "Anonymous" as const, busy: false, phase: "idle" as const, announcement: "" };

  it("shows the tenant's suggestions when the option is 'default'", () => {
    const w = mount(ChatView, { props: { ...base, suggestions: "default", tenantSuggestions: ["What does Acme offer?"] } });
    expect(w.text()).toContain("What does Acme offer?");
    w.unmount();
  });

  it("lets a host-supplied list win over the tenant's", () => {
    const w = mount(ChatView, { props: { ...base, suggestions: ["Host prompt"], tenantSuggestions: ["Tenant prompt"] } });
    expect(w.text()).toContain("Host prompt");
    expect(w.text()).not.toContain("Tenant prompt");
    w.unmount();
  });
});
