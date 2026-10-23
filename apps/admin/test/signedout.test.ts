import { afterEach, describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import SignedOut from "../src/components/SignedOut.vue";

afterEach(() => vi.unstubAllGlobals());

describe("SignedOut", () => {
  it.each([
    ["not_admin", /not allowed/i],
    ["email_not_verified", /not verified/i],
    ["login_failed", /did not complete/i],
    ["access_denied", /cancelled or denied/i],
  ])("shows a plain message for %s", (code, re) => {
    const w = mount(SignedOut, { props: { error: code } });
    expect(w.get("[data-test=signin-error]").text()).toMatch(re);
  });

  it("shows nothing for unknown or missing codes (no leaking)", () => {
    expect(mount(SignedOut, { props: { error: "<script>x" } }).find("[data-test=signin-error]").exists()).toBe(false);
    expect(mount(SignedOut).find("[data-test=signin-error]").exists()).toBe(false);
  });

  it("navigates top-level to the relative auth/login", async () => {
    const assign = vi.fn();
    vi.stubGlobal("location", { ...window.location, assign });
    const w = mount(SignedOut);
    await w.get("[data-test=signin]").trigger("click");
    expect(assign).toHaveBeenCalledWith("auth/login");
  });
});
