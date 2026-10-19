import { describe, it, expect, afterEach, vi } from "vitest";
import { mount, flushPromises } from "@vue/test-utils";
import type { ChatEvent, SessionState } from "@biatec/chat-protocol";
import BiatecChatRoot from "../src/components/BiatecChatRoot.vue";
import { resolveOptions } from "../src/options";
import type { ChatServices } from "../src/components/chat/useChat";
import { AuthClient } from "../src/components/chat/auth";
import { promptsFor } from "../src/components/chat/suggestions";

const opts = resolveOptions({ backendUrl: "https://chat.biatec.io", clientKey: "k" });

afterEach(() => {
  document.body.innerHTML = "";
});

const anon: SessionState = { sessionId: "s", tier: "Anonymous", scopes: [], expiresAt: "2030-01-01T00:00:00Z" };
const authed: SessionState = {
  sessionId: "s",
  tier: "Authenticated",
  email: "alice@example.com",
  primaryAddress: "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567ABCDEFGHIJKLMNOPQRSTUVWXYZ",
  scopes: ["openid"],
  expiresAt: "2030-01-01T00:00:00Z",
};

/** Events the test pushes by hand, so it controls exactly when the stream advances. */
function controlledStream() {
  const queue: (ChatEvent | "end")[] = [];
  let wake: (() => void) | null = null;
  return {
    push(e: ChatEvent | "end") {
      queue.push(e);
      wake?.();
    },
    async *gen(): AsyncGenerator<ChatEvent> {
      for (;;) {
        while (!queue.length) await new Promise<void>((r) => (wake = r));
        const e = queue.shift()!;
        if (e === "end") return;
        yield e;
      }
    },
  };
}

function services(over: Partial<ChatServices> & { sessions?: SessionState[] } = {}): ChatServices {
  const sessions = [...(over.sessions ?? [anon])];
  return {
    transport: over.transport ?? {
      open: vi.fn(async () => "success" as const),
      retryOnSignIn: vi.fn(async () => true),
      send: vi.fn(async function* () {}),
      dispose: vi.fn(),
    },
    auth: over.auth ?? {
      session: vi.fn(async () => (sessions.length > 1 ? sessions.shift()! : sessions[0]!)),
      startLogin: vi.fn(async () => ({ authRequestId: "r1", loginUrl: "https://chat.biatec.io/auth/login?req=r1&nonce=n" })),
      startElevate: vi.fn(async () => ({ authRequestId: "r2", loginUrl: "https://chat.biatec.io/auth/elevate?req=r2" })),
      status: vi.fn(async () => "Complete" as const),
      logoutUrl: vi.fn(() => "https://chat.biatec.io/auth/logout"),
    },
    pollMs: 1,
    timeoutMs: 2000,
    openPopup: over.openPopup,
  };
}

async function open(svc: ChatServices) {
  const w = mount(BiatecChatRoot, { props: { options: opts, services: svc }, attachTo: document.body });
  await w.get(".bc-launcher").trigger("click");
  await flushPromises();
  return w;
}

const fakePopup = () => ({ location: { href: "" }, closed: false, close: vi.fn() }) as unknown as Window & { close: ReturnType<typeof vi.fn> };

describe("streaming", () => {
  it("renders text progressively and shows compact tool lines", async () => {
    const s = controlledStream();
    const svc = services();
    svc.transport.send = vi.fn(() => s.gen());
    const w = await open(svc);
    await w.get("textarea").setValue("hello");
    await w.get("form").trigger("submit");
    await flushPromises();
    expect(w.get("[data-role=user]").text()).toBe("hello");

    s.push({ type: "tool_status", toolCallId: "t1", label: "Checking your USDC balance", state: "Started" });
    s.push({ type: "text_delta", text: "Your " });
    await flushPromises();
    expect(w.get(".bc-tool").text()).toBe("Checking your USDC balance");
    expect(w.get("[data-role=assistant] .bc-text").text()).toBe("Your");
    // still streaming: nothing is announced yet and the composer is locked
    expect(w.get("[data-testid=announcer]").text()).toBe("");
    expect(w.get("button[type=submit]").attributes("disabled")).toBeDefined();

    s.push({ type: "tool_status", toolCallId: "t1", label: "Checking your USDC balance", state: "Succeeded" });
    s.push({ type: "text_delta", text: "balance is 5." });
    await flushPromises();
    expect(w.get("[data-role=assistant] .bc-text").text()).toBe("Your balance is 5.");
    expect(w.findAll(".bc-tool")).toHaveLength(1);
    expect(w.get(".bc-tool").attributes("data-state")).toBe("Succeeded");

    s.push("end");
    await flushPromises();
    // the finished reply is announced once, in a polite live region
    const live = w.get("[data-testid=announcer]");
    expect(live.attributes("aria-live")).toBe("polite");
    expect(live.text()).toBe("Your balance is 5.");
    w.unmount();
  });

  it("renders model text as text, never as HTML", async () => {
    const svc = services();
    svc.transport.send = vi.fn(async function* () {
      yield { type: "text_delta", text: "<img src=x onerror=alert(1)>" } as ChatEvent;
    });
    const w = await open(svc);
    await w.get("textarea").setValue("x");
    await w.get("form").trigger("submit");
    await flushPromises();
    expect(w.find("[data-role=assistant] img").exists()).toBe(false);
    expect(w.get("[data-role=assistant] .bc-text").text()).toContain("<img");
    w.unmount();
  });

  it("shows stream errors in an alert", async () => {
    const svc = services();
    svc.transport.send = vi.fn(async function* () {
      yield { type: "error", code: "x", message: "Rate limited", retryable: true } as ChatEvent;
    });
    const w = await open(svc);
    await w.get("textarea").setValue("x");
    await w.get("form").trigger("submit");
    await flushPromises();
    expect(w.get("[role=alert]").text()).toBe("Rate limited");
    w.unmount();
  });
});

describe("sample prompts by tier", () => {
  it("anonymous sees only the no-account prompt, authenticated sees all", () => {
    expect(promptsFor("Anonymous", "default")).toHaveLength(1);
    expect(promptsFor("Authenticated", "default")).toHaveLength(6);
    expect(promptsFor("Anonymous", ["a", "b"]).map((p) => p.prompt)).toEqual(["a", "b"]);
  });
  it("renders per tier and sends the prompt on click", async () => {
    const svc = services({ sessions: [authed] });
    const w = await open(svc);
    expect(w.findAll(".bc-chip")).toHaveLength(6);
    await w.findAll(".bc-chip")[3]!.trigger("click");
    expect(svc.transport.send).toHaveBeenCalledWith(expect.objectContaining({ message: "Swap 50 USDC to ALGO." }));
    w.unmount();
    const w2 = await open(services());
    expect(w2.findAll(".bc-chip")).toHaveLength(1);
    w2.unmount();
  });
});

describe("header", () => {
  it("shows Guest, then email with primary address after sign-in, without reload", async () => {
    const popup = fakePopup();
    const svc = services({ sessions: [anon, anon, authed], openPopup: () => popup });
    const w = await open(svc);
    expect(w.get("[data-testid=tier]").text()).toBe("Guest");
    expect(w.findAll(".bc-chip")).toHaveLength(1);

    await w.get("[data-testid=sign-in]").trigger("click");
    await vi.waitFor(() => expect(w.find("[data-testid=sign-out]").exists()).toBe(true));
    expect(svc.transport.retryOnSignIn).toHaveBeenCalled();
    expect(popup.location.href).toContain("/auth/login?req=r1");
    expect(popup.close).toHaveBeenCalled();
    expect(w.get("[data-testid=tier]").text()).toContain("alice@example.com");
    expect(w.get("[data-testid=tier]").text()).toContain("ABCDEF...UVWXYZ".slice(0, 6));
    // tier switched in place: the authenticated prompts are now offered
    expect(w.findAll(".bc-chip")).toHaveLength(6);
    expect(w.get("[data-testid=announcer]").text()).toBe("Signed in as alice@example.com");
    w.unmount();
  });
});

describe("sign-in popup", () => {
  it("polls until complete, tolerating pending and transient errors", async () => {
    const popup = fakePopup();
    const svc = services({ sessions: [anon, anon, authed], openPopup: () => popup });
    const status = vi.fn().mockResolvedValueOnce("Pending").mockResolvedValueOnce(null).mockResolvedValueOnce("Complete");
    svc.auth.status = status;
    const w = await open(svc);
    await w.get("[data-testid=sign-in]").trigger("click");
    await vi.waitFor(() => expect(w.find("[data-testid=sign-out]").exists()).toBe(true));
    expect(status).toHaveBeenCalledTimes(3);
    expect(status).toHaveBeenCalledWith("r1");
    w.unmount();
  });

  it("reports a blocked popup", async () => {
    const svc = services({ openPopup: () => null });
    const w = await open(svc);
    await w.get("[data-testid=sign-in]").trigger("click");
    await flushPromises();
    expect(w.get(".bc-signin-status").text()).toContain("blocked");
    expect(svc.auth.startLogin).not.toHaveBeenCalled();
    w.unmount();
  });

  it("closes the popup and reports failure when the flow fails", async () => {
    const popup = fakePopup();
    const svc = services({ openPopup: () => popup });
    svc.auth.status = vi.fn(async () => "Failed" as const);
    const w = await open(svc);
    await w.get("[data-testid=sign-in]").trigger("click");
    await vi.waitFor(() => expect(w.find(".bc-signin-status").text()).toContain("did not complete"));
    expect(popup.close).toHaveBeenCalled();
    expect(w.find("[data-testid=sign-out]").exists()).toBe(false);
    w.unmount();
  });

  it("gives up when the user closes the popup", async () => {
    const popup = fakePopup();
    (popup as { closed: boolean }).closed = true;
    const svc = services({ openPopup: () => popup });
    svc.auth.status = vi.fn(async () => "Pending" as const);
    const w = await open(svc);
    await w.get("[data-testid=sign-in]").trigger("click");
    await vi.waitFor(() => expect(w.get(".bc-signin-status").text()).toContain("did not complete"));
    w.unmount();
  });

  it("a sign-in card from the assistant starts the matching flow", async () => {
    const popup = fakePopup();
    const svc = services({ openPopup: () => popup });
    svc.transport.send = vi.fn(async function* () {
      yield { type: "sign_in_card", signInUrl: "javascript:alert(1)", reason: "ElevateScope", scope: "sign", description: "Authorize signing" } as ChatEvent;
    });
    const w = await open(svc);
    await w.get("textarea").setValue("swap");
    await w.get("form").trigger("submit");
    await flushPromises();
    expect(w.get(".bc-signin-card").text()).toContain("Authorize signing");
    await w.get(".bc-signin-card button").trigger("click");
    await flushPromises();
    expect(svc.auth.startElevate).toHaveBeenCalledWith("sign");
    // the event's own URL is never opened
    expect(popup.location.href).not.toContain("javascript");
    w.unmount();
  });
});

describe("AuthClient", () => {
  const cfg = (f: typeof fetch) => ({ backendUrl: "https://b", clientKey: "k", fetch: f });
  it("sends cookie credentials and the CSRF header, and returns only ids and URLs", async () => {
    const f = vi.fn(async () => new Response(JSON.stringify({ authRequestId: "a", loginUrl: "https://b/auth/login?req=a" })));
    const c = new AuthClient(cfg(f as unknown as typeof fetch));
    expect(await c.startLogin()).toEqual({ authRequestId: "a", loginUrl: "https://b/auth/login?req=a" });
    const [url, init] = f.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("https://b/auth/popup");
    expect(init.credentials).toBe("include");
    expect((init.headers as Record<string, string>)["X-Biatec-Chat"]).toBe("1");
  });
  it("refuses a non-web login URL", async () => {
    const f = vi.fn(async () => new Response(JSON.stringify({ authRequestId: "a", loginUrl: "javascript:alert(1)" })));
    expect(await new AuthClient(cfg(f as unknown as typeof fetch)).startLogin()).toBeNull();
  });
  it("maps status codes", async () => {
    const mk = (r: () => Response) => new AuthClient(cfg((async () => r()) as unknown as typeof fetch));
    expect(await mk(() => new Response('{"status":"Complete"}')).status("x")).toBe("Complete");
    expect(await mk(() => new Response("", { status: 404 })).status("x")).toBe("Failed");
    expect(await mk(() => new Response("", { status: 500 })).status("x")).toBeNull();
  });
});

describe("sign out", () => {
  it("navigates a popup to /auth/logout and clears the session once it is anonymous", async () => {
    const popup = fakePopup();
    const svc = services({ sessions: [authed, anon], openPopup: () => popup });
    const w = await open(svc);
    await w.get("[data-testid=sign-out]").trigger("click");
    await vi.waitFor(() => expect(w.find("[data-testid=sign-out]").exists()).toBe(false));
    expect(popup.location.href).toBe("https://chat.biatec.io/auth/logout");
    expect(popup.close).toHaveBeenCalled();
    w.unmount();
  });

  it("keeps the session and messages when sign-out does not complete", async () => {
    const popup = fakePopup();
    (popup as unknown as { closed: boolean }).closed = true;
    const svc = services({ sessions: [authed], openPopup: () => popup });
    const w = await open(svc);
    const chat = (w.vm as unknown as { chat?: { messages: unknown[] } }).chat;
    await w.get("textarea").setValue("hello");
    await w.get("textarea").trigger("keydown", { key: "Enter" });
    await flushPromises();
    const before = w.text();
    await w.get("[data-testid=sign-out]").trigger("click");
    await vi.waitFor(() => expect(w.text()).toContain("Sign-out did not complete"));
    expect(w.find("[data-testid=sign-out]").exists()).toBe(true);
    expect(w.text()).toContain("hello");
    expect(before).toContain("hello");
    void chat;
    w.unmount();
  });
});

describe("sign-in transport and announcements", () => {
  it("explains blocked cookies when the transport retry fails and sign-in cannot start", async () => {
    const popup = fakePopup();
    const svc = services({ openPopup: () => popup });
    svc.transport.retryOnSignIn = vi.fn(async () => false);
    svc.auth.startLogin = vi.fn(async () => null);
    const w = await open(svc);
    await w.get("[data-testid=sign-in]").trigger("click");
    await vi.waitFor(() => expect(w.text()).toContain("blocking third-party cookies"));
    w.unmount();
  });

  it("re-announces an identical consecutive error", async () => {
    const svc = services();
    svc.transport.send = vi.fn(async function* () {
      yield { type: "error", message: "Boom" } as ChatEvent;
    });
    const w = await open(svc);
    const send = async () => {
      await w.get("textarea").setValue("hi");
      await w.get("textarea").trigger("keydown", { key: "Enter" });
      await flushPromises();
    };
    await send();
    expect(w.get("[data-testid=announcer]").text()).toBe("Boom");
    const seen: string[] = [];
    const el = w.get("[data-testid=announcer]").element;
    new MutationObserver(() => seen.push(el.textContent ?? "")).observe(el, { childList: true, characterData: true, subtree: true });
    await send();
    await flushPromises();
    expect(seen).toContain("");
    expect(seen[seen.length - 1]).toBe("Boom");
    w.unmount();
  });
});

describe("accessibility", () => {
  it("labels the composer, marks the log, and keeps native buttons", async () => {
    const w = await open(services());
    expect(w.get("label[for=biatec-chat-input]").text()).toBe("Message");
    expect(w.find("[role=log]").exists()).toBe(true);
    expect(w.get("textarea").attributes("id")).toBe("biatec-chat-input");
    w.unmount();
  });
});
