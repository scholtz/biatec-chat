import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import ImpersonationBanner from "../src/components/ImpersonationBanner.vue";
import { flush, mockFetch } from "./helpers";

const NOW = new Date("2026-10-23T10:00:00Z");
const imp = (secs: number) => ({
  tenantId: "t", sub: "s", reason: "ticket 1234",
  startedAt: NOW.toISOString(), expiresAt: new Date(NOW.getTime() + secs * 1000).toISOString(),
});

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(NOW);
});
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("ImpersonationBanner", () => {
  it("shows who, why and counts down, then emits expired once", async () => {
    const w = mount(ImpersonationBanner, { props: { impersonation: imp(3) } });
    expect(w.text()).toContain("subject s");
    expect(w.text()).toContain("ticket 1234");
    expect(w.get("[data-test=countdown]").text()).toContain("0:03");
    await vi.advanceTimersByTimeAsync(2000);
    expect(w.get("[data-test=countdown]").text()).toContain("0:01");
    expect(w.emitted("expired")).toBeFalsy();
    await vi.advanceTimersByTimeAsync(3000);
    expect(w.get("[data-test=countdown]").text()).toContain("0:00");
    expect(w.emitted("expired")).toHaveLength(1);
  });

  it("links to the view and links to the view", () => {
    const w = mount(ImpersonationBanner, { props: { impersonation: imp(60) } });
    expect(w.get("[data-test=open-view]").attributes("href")).toBe("#/view");
    expect(w.text()).toContain("subject s");
  });

  it("Stop sends DELETE with the CSRF header and emits stopped", async () => {
    vi.useRealTimers();
    const calls = mockFetch(() => [204, undefined]);
    const w = mount(ImpersonationBanner, { props: { impersonation: imp(600) } });
    await w.get("[data-test=stop]").trigger("click");
    await flush();
    expect(calls[0].url).toBe("api/impersonation");
    expect(calls[0].init.method).toBe("DELETE");
    expect((calls[0].init.headers as Record<string, string>)["X-Biatec-Admin"]).toBe("1");
    expect(w.emitted("stopped")).toHaveLength(1);
    w.unmount();
  });

  it("Stop failure shows an alert and does not emit stopped", async () => {
    vi.useRealTimers();
    mockFetch(() => [500, { error: "nope" }]);
    const w = mount(ImpersonationBanner, { props: { impersonation: imp(600) } });
    await w.get("[data-test=stop]").trigger("click");
    await flush();
    expect(w.get("[role=alert]").text()).toBe("nope");
    expect(w.emitted("stopped")).toBeFalsy();
    w.unmount();
  });
});
