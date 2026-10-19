import { describe, it, expect, afterEach, vi } from "vitest";
import { mount, flushPromises } from "@vue/test-utils";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import type { ChatEvent, SessionState } from "@biatec/chat-protocol";
import BiatecChatRoot from "../src/components/BiatecChatRoot.vue";
import ChatView from "../src/components/chat/ChatView.vue";
import { parseBlocks, safeHttpUrl } from "../src/components/chat/format";
import { resolveOptions } from "../src/options";
import type { ChatServices } from "../src/components/chat/useChat";
import { en } from "../src/i18n";

const base = { backendUrl: "https://chat.biatec.io", clientKey: "k" };

const anon: SessionState = { sessionId: "s", tier: "Anonymous", scopes: [], expiresAt: "2030-01-01T00:00:00Z" };
const authed: SessionState = {
  sessionId: "s",
  tier: "Authenticated",
  email: "alice@example.com",
  scopes: ["openid"],
  expiresAt: "2030-01-01T00:00:00Z",
};

function services(session: SessionState = anon, events: ChatEvent[] = []): ChatServices {
  return {
    transport: {
      open: vi.fn(async () => "success" as const),
      retryOnSignIn: vi.fn(async () => true),
      send: vi.fn(async function* () {
        for (const e of events) yield e;
      }),
      dispose: vi.fn(),
    },
    auth: {
      session: vi.fn(async () => session),
      startLogin: vi.fn(async () => null),
      startElevate: vi.fn(async () => null),
      status: vi.fn(async () => "Pending" as const),
      logoutUrl: vi.fn(() => "https://chat.biatec.io/auth/logout"),
    },
    pollMs: 1,
    timeoutMs: 50,
  };
}

async function open(svc: ChatServices, extra: Record<string, unknown> = {}) {
  const w = mount(BiatecChatRoot, { props: { options: resolveOptions({ ...base, ...extra }), services: svc }, attachTo: document.body });
  await w.get(".bc-launcher").trigger("click");
  await flushPromises();
  return w;
}

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
});

describe("empty state", () => {
  it("shows the headline, intro, four guest prompts, and the agent mode note", async () => {
    const w = await open(services());
    expect(w.get(".bc-empty-title").text()).toBe("Meet Biatec AI.");
    expect(w.findAll(".bc-suggest")).toHaveLength(4);
    expect(w.findAll(".bc-suggest svg")).toHaveLength(4);
    expect(w.get("[data-testid=shield-note]").text()).toContain("Analyze only");
    w.unmount();
  });

  it("shows four different prompts for a signed-in session, and a click sends one", async () => {
    const svc = services(authed);
    const w = await open(svc);
    const labels = w.findAll(".bc-suggest").map((b) => b.text());
    expect(labels).toHaveLength(4);
    expect(labels).toContain("Swap USDC to ALGO");
    expect(labels).not.toContain("What is the blockchain?");
    expect(w.get("[data-testid=shield-note]").text()).toContain("Prepare for approval");
    await w.findAll(".bc-suggest")[1]!.trigger("click");
    await flushPromises();
    expect(svc.transport.send).toHaveBeenCalledWith(expect.objectContaining({ message: "Add liquidity to the Biatec DEX EURS/USDC pool." }));
    w.unmount();
  });
});

describe("message rendering", () => {
  it("splits paragraphs and bullets as plain text", () => {
    expect(parseBlocks("Intro line\n\n- one\n- two\n\nOutro")).toEqual([
      { kind: "p", text: "Intro line" },
      { kind: "ul", items: ["one", "two"] },
      { kind: "p", text: "Outro" },
    ]);
    expect(safeHttpUrl("javascript:alert(1)")).toBeNull();
    expect(safeHttpUrl("https://web.biatec.io/signin")).toBe("https://web.biatec.io/signin");
  });

  it("renders paragraphs and bullet items, never HTML", async () => {
    const svc = services(anon, [{ type: "text_delta", text: "Hello <b>x</b>\n\n- first\n- second" }]);
    const w = await open(svc);
    await w.get("textarea").setValue("hi");
    await w.get("form").trigger("submit");
    await flushPromises();
    expect(w.find("[data-role=assistant] b").exists()).toBe(false);
    expect(w.get("[data-role=assistant] .bc-para").text()).toBe("Hello <b>x</b>");
    expect(w.findAll("[data-role=assistant] .bc-bullets li").map((l) => l.text())).toEqual(["first", "second"]);
    w.unmount();
  });

  it("shows a thinking status while nothing has streamed, and removes it after", async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const svc = services();
    svc.transport.send = vi.fn(async function* () {
      await gate;
      yield { type: "text_delta", text: "Done." } as ChatEvent;
    });
    const w = await open(svc);
    await w.get("textarea").setValue("hi");
    await w.get("form").trigger("submit");
    await flushPromises();
    const thinking = w.get(".bc-thinking");
    expect(thinking.attributes("role")).toBe("status");
    expect(thinking.text()).toBe("Analyzing…");
    release();
    await flushPromises();
    expect(w.find(".bc-thinking").exists()).toBe(false);
    w.unmount();
  });

  it("renders backend follow-ups as chips that send their prompt", async () => {
    const send = vi.fn();
    const w = mount(ChatView, {
      props: {
        messages: [
          { id: 1, role: "user", text: "q", tools: [], streaming: false },
          { id: 2, role: "assistant", text: "a", tools: [], streaming: false, followUps: [{ id: "f", label: "Tell me more", prompt: "More detail please" }] },
        ],
        tier: "Anonymous",
        suggestions: "default",
        busy: false,
        phase: "idle",
        announcement: "",
        onSend: send,
      },
    });
    await w.get(".bc-followup").trigger("click");
    expect(send).toHaveBeenCalledWith("More detail please", undefined);
  });
});

describe("copy and retry", () => {
  it("copies the plain text, shows Copied for 1.5 s, and Retry resends the last user message", async () => {
    const writeText = vi.fn(async () => {});
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
    const svc = services(anon, [{ type: "text_delta", text: "Answer\n\n- a\n- b" }]);
    const w = await open(svc);
    await w.get("textarea").setValue("question one");
    await w.get("form").trigger("submit");
    await flushPromises();

    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const copyBtn = w.get("[data-role=assistant] .bc-action");
    await copyBtn.trigger("click");
    await flushPromises();
    expect(writeText).toHaveBeenCalledWith("Answer\n\n- a\n- b");
    expect(w.get("[data-testid=copy-label]").text()).toBe("Copied");
    vi.advanceTimersByTime(1400);
    await flushPromises();
    expect(w.get("[data-testid=copy-label]").text()).toBe("Copied");
    vi.advanceTimersByTime(200);
    await flushPromises();
    expect(w.get("[data-testid=copy-label]").text()).toBe("Copy");
    vi.useRealTimers();

    await w.get("[data-testid=retry]").trigger("click");
    await flushPromises();
    expect(svc.transport.send).toHaveBeenCalledTimes(2);
    expect(svc.transport.send).toHaveBeenLastCalledWith(expect.objectContaining({ message: "question one" }));
    expect(w.findAll("[data-role=user]")).toHaveLength(2);
    w.unmount();
  });
});

describe("context chip", () => {
  it("is absent without pageContext", async () => {
    const w = await open(services());
    expect(w.find("[data-testid=context-chip]").exists()).toBe(false);
    w.unmount();
  });

  it("is present with pageContext and sends it; toggled off the next request carries none", async () => {
    const svc = services();
    const w = await open(svc, { pageContext: "BTC/USDC" });
    const chip = w.get("[data-testid=context-chip]");
    expect(chip.text()).toContain("BTC/USDC");
    expect(chip.attributes("aria-pressed")).toBe("true");

    await w.get("textarea").setValue("first");
    await w.get("form").trigger("submit");
    await flushPromises();
    expect(vi.mocked(svc.transport.send).mock.calls[0]![0].context).toMatchObject({ product: "BTC/USDC" });

    await w.get("[data-testid=context-chip]").trigger("click");
    expect(w.get("[data-testid=context-chip]").attributes("aria-pressed")).toBe("false");
    await w.get("textarea").setValue("second");
    await w.get("form").trigger("submit");
    await flushPromises();
    expect(vi.mocked(svc.transport.send).mock.calls[1]![0].context).toBeNull();

    // The exclusion applies to one message only.
    expect(w.get("[data-testid=context-chip]").attributes("aria-pressed")).toBe("true");
    await w.get("textarea").setValue("third");
    await w.get("form").trigger("submit");
    await flushPromises();
    expect(vi.mocked(svc.transport.send).mock.calls[2]![0].context).toMatchObject({ product: "BTC/USDC" });
    w.unmount();
  });
});

describe("composer keys", () => {
  it("Enter sends, Shift+Enter does not, and Enter during IME composition does not", async () => {
    const svc = services();
    const w = await open(svc);
    const ta = w.get("textarea");
    expect(ta.attributes("rows")).toBe("2");
    expect(ta.attributes("placeholder")).toBe("Ask Biatec AI about Biatec, Algorand or your wallet…");
    await ta.setValue("hello");

    await ta.trigger("keydown", { key: "Enter", shiftKey: true });
    await flushPromises();
    expect(svc.transport.send).not.toHaveBeenCalled();

    await ta.trigger("keydown", { key: "Enter", isComposing: true });
    await flushPromises();
    expect(svc.transport.send).not.toHaveBeenCalled();

    await ta.trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(svc.transport.send).toHaveBeenCalledTimes(1);
    w.unmount();
  });

  it("disables Send when empty or busy and shows the footer", async () => {
    const w = await open(services());
    expect(w.get("[data-testid=send]").attributes("disabled")).toBeDefined();
    await w.get("textarea").setValue("x");
    expect(w.get("[data-testid=send]").attributes("disabled")).toBeUndefined();
    expect(w.text()).toContain("Biatec AI can be wrong. Check before acting. Not financial advice.");
    w.unmount();
  });
});

describe("sign-in cards", () => {
  it("shows the primary Sign in with Biatec button", async () => {
    const svc = services(anon, [{ type: "sign_in_card", signInUrl: "https://x", reason: "SignIn", scope: null, description: "Swapping needs an account.", unavailable: false }]);
    const w = await open(svc);
    await w.get("textarea").setValue("swap");
    await w.get("form").trigger("submit");
    await flushPromises();
    expect(w.get(".bc-signin-card").text()).toContain("Swapping needs an account.");
    expect(w.get(".bc-signin-card button").text()).toBe("Sign in with Biatec");
    w.unmount();
  });

  it("shows the unavailable variant with the tenant link, only for http(s)", async () => {
    const svc = services(anon, [{ type: "sign_in_card", signInUrl: "https://web.biatec.io/signin", reason: "SignIn", scope: null, description: null, unavailable: true }]);
    const w = await open(svc);
    await w.get("textarea").setValue("swap");
    await w.get("form").trigger("submit");
    await flushPromises();
    const card = w.get(".bc-signin-card");
    expect(card.attributes("data-variant")).toBe("unavailable");
    expect(card.find("button").exists()).toBe(false);
    expect(card.get("a").attributes("href")).toBe("https://web.biatec.io/signin");
    expect(card.get("a").attributes("rel")).toContain("noopener");
    w.unmount();

    const bad = services(anon, [{ type: "sign_in_card", signInUrl: "javascript:alert(1)", reason: "SignIn", scope: null, description: null, unavailable: true }]);
    const w2 = await open(bad);
    await w2.get("textarea").setValue("swap");
    await w2.get("form").trigger("submit");
    await flushPromises();
    expect(w2.find(".bc-signin-card a").exists()).toBe(false);
    w2.unmount();
  });
});

describe("i18n", () => {
  const source = readFileSync(resolve(process.cwd(), "src/components/chat/ChatView.vue"), "utf8");
  const template = source.slice(source.indexOf("<template>\n  <div class=\"bc-chat\">"));

  it("has no string literal in the template outside the catalog", () => {
    const text = template
      .replace(/<!--[\s\S]*?-->/g, "")
      .replace(/\{\{[\s\S]*?\}\}/g, "")
      .replace(/<[^>]*>/g, (tag) => (/\s(?:placeholder|aria-label|title|alt|label)="[^"]*[A-Za-z]/.test(tag) ? "LITERAL-ATTR" : ""));
    expect(text.replace(/\s+/g, "")).toBe("");
  });

  it("every t() key used by the chat view exists in the English catalog", () => {
    const keys = [...source.matchAll(/"(chat\.[A-Za-z]+)"/g)].map((m) => m[1]!);
    expect(keys.length).toBeGreaterThan(15);
    const chat = en.chat as Record<string, string>;
    for (const k of keys) expect(chat[k.slice("chat.".length)], k).toBeTruthy();
    for (const k of Object.keys(chat)) expect(chat[k]).not.toMatch(/\p{Extended_Pictographic}/u);
  });
});
