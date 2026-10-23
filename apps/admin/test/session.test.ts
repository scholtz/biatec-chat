import { afterEach, describe, expect, it, vi } from "vitest";
import { refreshSession, session } from "../src/session";
import { mockFetch } from "./helpers";

afterEach(() => {
  vi.unstubAllGlobals();
  session.status = "loading";
  session.me = null;
  session.error = "";
});

describe("refreshSession", () => {
  it("keeps a running session on a transient failure and reports an error only while loading", async () => {
    const me = { email: "a@x.com", tenantId: "t", expiresAt: "", impersonation: null };
    session.status = "in";
    session.me = me;
    mockFetch(() => [503, { error: "busy" }]);
    await refreshSession();
    expect(session.status).toBe("in");
    expect(session.me).toEqual(me);

    session.status = "loading";
    await refreshSession();
    expect(session.status).toBe("error");
  });

  it("signs out on 401", async () => {
    session.status = "in";
    mockFetch(() => [401, { error: "Sign in required." }]);
    await refreshSession();
    expect(session.status).toBe("out");
  });
});
