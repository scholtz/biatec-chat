import { afterEach, describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import ViewAs from "../src/views/ViewAs.vue";
import { session } from "../src/session";
import { flush, mockFetch } from "./helpers";

const XSS = "<img src=x onerror=alert(1)>";
const imp = { tenantId: "t", sub: "s", reason: "support", startedAt: "", expiresAt: "2099-01-01T00:00:00Z" };

afterEach(() => {
  vi.unstubAllGlobals();
  session.me = null;
});

describe("ViewAs", () => {
  it("shows an empty state with a link to Users without an impersonation", () => {
    session.me = { email: "a@x.com", tenantId: "t", expiresAt: "", impersonation: null };
    const calls = mockFetch(() => [200, []]);
    const w = mount(ViewAs);
    expect(w.get("[data-test=no-impersonation]").find("a").attributes("href")).toBe("#/users");
    expect(calls).toHaveLength(0);
  });

  it("renders messages as literal text, keeps line breaks and creates no elements from content", async () => {
    session.me = { email: "a@x.com", tenantId: "t", expiresAt: "", impersonation: imp };
    mockFetch((url) => {
      if (url === "api/view/conversations")
        return [200, { user: { tenantId: "t", sub: "s" }, conversations: [{ id: "c1", createdAt: "2026-10-22T10:00:00Z", lastMessageAt: null, endedAt: null, messages: 2, previews: 1 }] }];
      return [200, {
        conversation: { id: "c1", createdAt: "2026-10-22T10:00:00Z", lastMessageAt: null, endedAt: null },
        messages: [
          { seq: 1, role: "User", content: `${XSS}\nsecond line`, createdAt: "2026-10-22T10:00:00Z", modelId: null, toolNames: [] },
          { seq: 2, role: "Assistant", content: "**bold** <b>x</b>", createdAt: "2026-10-22T10:00:05Z", modelId: "m", toolNames: ["get_balance"] },
        ],
        previews: [{ id: "p1", capability: "wallet.transfer", status: "Completed", predictedValueEur: 12.5, needsConsent: false, expiresAt: null,
          steps: [{ seq: 1, kind: "Sign", status: "Confirmed", network: "algorand-mainnet", txIds: ["TX1"] }] }],
      }];
    });
    const w = mount(ViewAs);
    await flush(8);
    const texts = w.findAll("[data-test=msg-text]");
    expect(texts).toHaveLength(2);
    expect(texts[0].text()).toContain(XSS);
    expect(texts[0].element.textContent).toBe(`${XSS}\nsecond line`);
    expect(texts[1].element.textContent).toBe("**bold** <b>x</b>");
    expect(w.find("img").exists()).toBe(false);
    expect(w.find("b").exists()).toBe(false);
    expect(w.get("[data-test=tool]").text()).toBe("get_balance");
    expect(w.text()).toContain("wallet.transfer");
    expect(w.text()).toContain("Confirmed");
    expect(w.get("[data-test=readonly-notice]").text()).toMatch(/Nothing can be sent, confirmed or signed/);
  });

  it("says so when older messages are left out", async () => {
    session.me = { email: "a@x.com", tenantId: "t", expiresAt: "", impersonation: imp };
    mockFetch((url) => {
      if (url === "api/view/conversations")
        return [200, { user: { tenantId: "t", sub: "s" }, conversations: [{ id: "c1", createdAt: "2026-10-22T10:00:00Z", lastMessageAt: null, endedAt: null, messages: 900, previews: 0 }] }];
      return [200, {
        conversation: { id: "c1", createdAt: "2026-10-22T10:00:00Z", lastMessageAt: null, endedAt: null },
        truncated: true,
        messages: [{ seq: 401, role: "User", content: "recent", createdAt: "2026-10-22T10:00:00Z", modelId: null, toolNames: [] }],
        previews: [],
      }];
    });
    const w = mount(ViewAs);
    await flush(8);
    expect(w.get("[data-test=truncated]").text()).toMatch(/only the newest 1 messages/);
  });
});
