import { afterEach, describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import ImpersonateModal from "../src/components/ImpersonateModal.vue";
import { session } from "../src/session";
import { flush, mockFetch } from "./helpers";

const target = { tenantId: "t1", sub: "sub-1" };
afterEach(() => {
  vi.unstubAllGlobals();
  session.me = null;
});

describe("ImpersonateModal", () => {
  it("rejects a short reason without calling the API", async () => {
    const calls = mockFetch(() => [200, {}]);
    const w = mount(ImpersonateModal, { props: { target } });
    await w.get("textarea").setValue("short");
    await w.get("form").trigger("submit");
    expect(w.get("[data-test=reason-error]").text()).toMatch(/at least 8/);
    expect(calls).toHaveLength(0);
  });

  it("shows a live counter and flags over-long reasons", async () => {
    const w = mount(ImpersonateModal, { props: { target } });
    await w.get("textarea").setValue("x".repeat(501));
    expect(w.get("[data-test=counter]").text()).toBe("501 / 500");
    await w.get("form").trigger("submit");
    expect(w.get("[data-test=reason-error]").text()).toMatch(/at most 500/i);
  });

  it("starts the impersonation, stores it and closes", async () => {
    session.me = { email: "a@x.com", tenantId: "t1", expiresAt: "2099-01-01T00:00:00Z", impersonation: null };
    const imp = { tenantId: "t1", sub: "sub-1", reason: "support ticket 42", startedAt: "", expiresAt: "2099-01-01T00:00:00Z" };
    const calls = mockFetch(() => [200, imp]);
    const w = mount(ImpersonateModal, { props: { target } });
    await w.get("textarea").setValue("  support ticket 42  ");
    await w.get("form").trigger("submit");
    await flush();
    expect(calls[0].url).toBe("api/impersonation");
    expect(JSON.parse(calls[0].init.body as string)).toEqual({ tenantId: "t1", sub: "sub-1", reason: "support ticket 42" });
    expect(session.me?.impersonation?.reason).toBe("support ticket 42");
    expect(w.emitted("close")).toBeTruthy();
    expect(location.hash).toBe("#/view");
  });

  it("shows the server error and stays open", async () => {
    mockFetch(() => [400, { error: "user not found" }]);
    const w = mount(ImpersonateModal, { props: { target } });
    await w.get("textarea").setValue("a valid reason");
    await w.get("form").trigger("submit");
    await flush();
    expect(w.get("[data-test=server-error]").text()).toBe("user not found");
    expect(w.emitted("close")).toBeFalsy();
  });
});
