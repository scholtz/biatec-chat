import { afterEach, describe, expect, it, vi } from "vitest";
import { api, ApiError, buildUsersQuery, normalizeConversations } from "../src/api";
import { refreshSession, session } from "../src/session";
import { header, mockFetch } from "./helpers";

afterEach(() => {
  vi.unstubAllGlobals();
  session.status = "loading";
  session.me = null;
});

describe("api client", () => {
  it("sends GET with same-origin credentials, JSON accept and no CSRF header", async () => {
    const calls = mockFetch(() => [200, { email: "a@x.com" }]);
    await api.me();
    expect(calls[0].url).toBe("api/me");
    expect(calls[0].init.method).toBe("GET");
    expect(calls[0].init.credentials).toBe("same-origin");
    expect(header(calls[0], "Accept")).toBe("application/json");
    expect(header(calls[0], "X-Biatec-Admin")).toBeUndefined();
  });

  it("adds X-Biatec-Admin and Content-Type on every non-GET", async () => {
    const calls = mockFetch(() => [200, {}]);
    await api.startImpersonation("t", "s", "a good reason");
    await api.stopImpersonation();
    await api.logout();
    expect(calls.map((c) => c.init.method)).toEqual(["POST", "DELETE", "POST"]);
    for (const c of calls) {
      expect(header(c, "X-Biatec-Admin")).toBe("1");
      expect(header(c, "Content-Type")).toBe("application/json");
    }
    expect(JSON.parse(calls[0].init.body as string)).toEqual({ tenantId: "t", sub: "s", reason: "a good reason" });
  });

  it("maps 401 to the signed-out state without retrying", async () => {
    const calls = mockFetch(() => [401, { error: "no" }]);
    await refreshSession();
    expect(session.status).toBe("out");
    expect(session.me).toBeNull();
    expect(calls).toHaveLength(1);
  });

  it("surfaces the {error} message of 4xx/5xx and falls back for other bodies", async () => {
    mockFetch(() => [400, { error: "reason too short" }]);
    await expect(api.stats(30)).rejects.toMatchObject({ status: 400, message: "reason too short" });
    mockFetch(() => [503, undefined]);
    const e = (await api.stats(30).catch((x) => x)) as ApiError;
    expect(e).toBeInstanceOf(ApiError);
    expect(e.status).toBe(503);
    expect(e.message).toMatch(/server/i);
  });

  it("reports network failure as an ApiError(0)", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("x"); }));
    await expect(api.me()).rejects.toMatchObject({ status: 0 });
  });

  it("builds query strings", () => {
    expect(buildUsersQuery({})).toBe("sort=lastActivity&dir=desc&page=1&pageSize=25");
    expect(buildUsersQuery({ q: " bob ", tenant: "t1", sort: "sub", dir: "asc", page: 3, pageSize: 50 })).toBe(
      "q=bob&tenant=t1&sort=sub&dir=asc&page=3&pageSize=50",
    );
  });

  it("requests stats, user and transcript URLs", async () => {
    const calls = mockFetch(() => [200, {}]);
    await api.stats(7, "biatec-chat");
    await api.user("t 1", "s/1");
    await api.viewMessages("a/b");
    expect(calls.map((c) => c.url)).toEqual([
      "api/stats?days=7&tenant=biatec-chat",
      "api/user?tenant=t+1&sub=s%2F1",
      "api/view/conversations/a%2Fb/messages",
    ]);
  });

  it("normalizes the conversation list shape", () => {
    const c = [{ id: "1" }];
    expect(normalizeConversations(c)).toEqual(c);
    expect(normalizeConversations({ conversations: c })).toEqual(c);
    expect(normalizeConversations({ items: c })).toEqual(c);
    expect(normalizeConversations(null)).toEqual([]);
  });
});
