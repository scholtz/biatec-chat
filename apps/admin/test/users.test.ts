import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import Users from "../src/views/Users.vue";
import { mockFetch } from "./helpers";

const item = (i: number) => ({
  tenantId: "biatec-chat", sub: `sub-${i}`, firstSeenAt: "2026-10-01T10:00:00Z", lastSeenAt: "2026-10-22T10:00:00Z",
  signInCount: 2, conversations: i, lastActivityAt: "2026-10-22T10:00:00Z",
});

function setup(total = 120) {
  return mockFetch((url) => {
    if (url.startsWith("api/stats")) return [200, { tenants: [{ id: "biatec-chat", host: "chat.biatec.io", users: 1, conversations: 1 }] }];
    return [200, { items: [item(1), item(2)], total, page: 1, pageSize: 25 }];
  });
}
const userCalls = (calls: { url: string }[]) => calls.filter((c) => c.url.startsWith("api/users")).map((c) => c.url);

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  const store = new Map<string, string>();
  vi.stubGlobal("localStorage", {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => void store.set(k, v),
    removeItem: (k: string) => void store.delete(k),
    clear: () => store.clear(),
  });
});
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

async function settle() {
  for (let i = 0; i < 6; i++) await vi.advanceTimersByTimeAsync(0);
}

describe("Users view", () => {
  it("loads with defaults and renders the subject as primary identity", async () => {
    const calls = setup();
    const w = mount(Users);
    await settle();
    expect(userCalls(calls)[0]).toBe("api/users?sort=lastActivity&dir=desc&page=1&pageSize=25");
    expect(w.get("[data-test=search]").attributes("placeholder")).toBe("Search by subject");
    expect(w.text()).toContain("sub-1");
    expect(w.text()).not.toContain("no email");
    expect(w.text()).toContain("1-25 of 120");
  });

  it("debounces search by 300 ms and resets to page 1", async () => {
    const calls = setup();
    const w = mount(Users);
    await settle();
    await w.get("[data-test=search]").setValue("bo");
    await vi.advanceTimersByTimeAsync(299);
    expect(userCalls(calls)).toHaveLength(1);
    await w.get("[data-test=search]").setValue("bob");
    await vi.advanceTimersByTimeAsync(300);
    const urls = userCalls(calls);
    expect(urls).toHaveLength(2);
    expect(urls[1]).toBe("api/users?q=bob&sort=lastActivity&dir=desc&page=1&pageSize=25");
  });

  it("sorts by header with aria-sort and toggles direction", async () => {
    const calls = setup();
    const w = mount(Users);
    await settle();
    await w.get("[data-test=sort-sub]").trigger("click");
    await settle();
    expect(userCalls(calls).at(-1)).toContain("sort=sub&dir=asc");
    expect(w.find("th[aria-sort=ascending]").exists()).toBe(true);
    await w.get("[data-test=sort-sub]").trigger("click");
    await settle();
    expect(userCalls(calls).at(-1)).toContain("sort=sub&dir=desc");
    await w.get("[data-test=sort-conversations]").trigger("click");
    await settle();
    expect(userCalls(calls).at(-1)).toContain("sort=conversations&dir=desc");
  });

  it("pages and changes page size (persisting the preference)", async () => {
    const calls = setup();
    const w = mount(Users);
    await settle();
    await w.get("[data-test=next]").trigger("click");
    await settle();
    expect(userCalls(calls).at(-1)).toContain("page=2&pageSize=25");
    await w.get("[data-test=page-size]").setValue(50);
    await settle();
    expect(userCalls(calls).at(-1)).toContain("page=1&pageSize=50");
    expect(localStorage.getItem("biatec-admin:pageSize")).toBe("50");
  });

  it("filters by tenant and opens the impersonation modal", async () => {
    const calls = setup();
    const w = mount(Users, { attachTo: document.body });
    await settle();
    await w.get("[data-test=tenant]").setValue("biatec-chat");
    await settle();
    expect(userCalls(calls).at(-1)).toContain("tenant=biatec-chat");
    await w.findAll("[data-test=impersonate]")[0].trigger("click");
    expect(document.body.querySelector("[data-test=modal]")).not.toBeNull();
    w.unmount();
  });

  it("shows an error with retry", async () => {
    let fail = true;
    mockFetch((url) => (url.startsWith("api/stats") ? [200, { tenants: [] }] : fail ? [500, { error: "boom" }] : [200, { items: [], total: 0, page: 1, pageSize: 25 }]));
    const w = mount(Users);
    await settle();
    expect(w.get("[data-test=error-text]").text()).toBe("boom");
    fail = false;
    await w.get("[data-test=retry]").trigger("click");
    await settle();
    expect(w.get("[data-test=empty]").text()).toMatch(/No users/);
  });
});
