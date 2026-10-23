import { describe, it, expect, afterEach, vi } from "vitest";
import { mount, flushPromises } from "@vue/test-utils";
import type { ChatEvent, SessionState } from "@biatec/chat-protocol";
import BiatecChatRoot from "../src/components/BiatecChatRoot.vue";
import { resolveOptions } from "../src/options";
import { MAX_REPLY_CHARS, type ChatServices } from "../src/components/chat/useChat";
import { isChatEvent, MAX_FRAME_CHARS, SseParser, readChatEvents } from "../src/transport/sse";

// BCHAT-025: preview_card and consent_status frames are validated like every other card, and nothing grows without bound.

const validPreview = () => ({
  previewId: "p1",
  capability: "dex.swap",
  intent: "Swap 100 USDC",
  network: "algorand-testnet",
  genesisId: "testnet-v1.0",
  status: "AwaitingConfirmation",
  expectsAuthenticatorApproval: false,
  approvalReason: null,
  limit: { bucket: "Daily", remaining: "900", currency: "USD" },
  expiresAt: "2030-01-01T00:00:00Z",
  steps: [
    {
      stepId: "s1",
      index: 0,
      title: "Swap",
      status: "AwaitingConfirmation",
      groupHash: "hash",
      from: "AAA",
      to: null,
      assets: [{ assetId: "1", symbol: "USDC", amount: "100", decimals: 6, fiatValue: null, fiatCurrency: null }],
      feeAmount: "0.001 ALGO",
    },
  ],
});
const frame = (preview: unknown) => ({ type: "preview_card", preview });
const mutate = (f: (p: Record<string, any>) => void) => {
  const p = validPreview() as Record<string, any>;
  f(p);
  return p;
};

describe("preview_card frames", () => {
  it("accepts a well-formed preview", () => {
    expect(isChatEvent(frame(validPreview()))).toBe(true);
  });

  it.each<[string, (p: Record<string, any>) => void]>([
    ["steps is not an array", (p) => (p.steps = "x")],
    ["unknown status", (p) => (p.status = "Hacked")],
    ["unknown step status", (p) => (p.steps[0].status = "Hacked")],
    ["groupHash is not a string", (p) => (p.steps[0].groupHash = 7)],
    ["previewId missing", (p) => delete p.previewId],
    ["intent is not a string", (p) => (p.intent = { html: "<b>" })],
    ["expiresAt missing", (p) => delete p.expiresAt],
    ["an asset has no amount", (p) => delete p.steps[0].assets[0].amount],
    ["an asset's decimals is not a number", (p) => (p.steps[0].assets[0].decimals = "six")],
    ["limit is malformed", (p) => (p.limit = { bucket: 1 })],
    ["expectsAuthenticatorApproval is not a boolean", (p) => (p.expectsAuthenticatorApproval = "yes")],
    ["a step is null", (p) => (p.steps = [null])],
  ])("drops a preview where %s", (_name, f) => {
    expect(isChatEvent(frame(mutate(f)))).toBe(false);
  });

  it("drops a preview_card whose preview is not an object", () => {
    expect(isChatEvent(frame(null))).toBe(false);
    expect(isChatEvent(frame("x"))).toBe(false);
    expect(isChatEvent({ type: "preview_card" })).toBe(false);
  });
});

describe("consent_status frames", () => {
  const consent = (c: unknown) => ({ type: "consent_status", consent: c });
  const ok = { previewId: "p1", stepId: "s1", state: "Pending", bindingCode: "123456", expiresAt: "2030-01-01T00:00:00Z" };

  it("accepts a well-formed status, with optional fields absent or null", () => {
    expect(isChatEvent(consent(ok))).toBe(true);
    expect(isChatEvent(consent({ previewId: "p1", stepId: "s1", state: "Approved" }))).toBe(true);
    expect(isChatEvent(consent({ ...ok, bindingCode: null, expiresAt: null }))).toBe(true);
  });

  it.each<[string, Record<string, unknown>]>([
    ["unknown state", { ...ok, state: "Maybe" }],
    ["stepId missing", { previewId: "p1", state: "Pending" }],
    ["bindingCode is a number", { ...ok, bindingCode: 123456 }],
    ["expiresAt is an object", { ...ok, expiresAt: {} }],
  ])("drops a status with %s", (_name, c) => {
    expect(isChatEvent(consent(c))).toBe(false);
  });
});

describe("size limits", () => {
  const body = (chunks: string[]) =>
    new ReadableStream<Uint8Array>({
      start(c) {
        for (const s of chunks) c.enqueue(new TextEncoder().encode(s));
        c.close();
      },
    });

  it("a line with no break above the limit stops the parser", () => {
    const p = new SseParser();
    expect(p.push("data: " + "x".repeat(MAX_FRAME_CHARS + 1))).toEqual([]);
    expect(p.overflowed).toBe(true);
    expect(p.push('data: {"type":"text_delta","text":"late"}\n\n')).toEqual([]);
  });

  it("an event whose data lines add up above the limit stops the parser", () => {
    const p = new SseParser();
    const line = "data: " + "y".repeat(MAX_FRAME_CHARS / 4) + "\n";
    p.push(line.repeat(5));
    expect(p.overflowed).toBe(true);
  });

  it("many small complete events are fine (the limit is per frame, not per stream)", () => {
    const p = new SseParser();
    for (let i = 0; i < 50; i++) expect(p.push('data: {"type":"text_delta","text":"' + "z".repeat(50_000) + '"}\n\n')).toHaveLength(1);
    expect(p.overflowed).toBe(false);
  });

  it("readChatEvents ends the stream with an error event and reads no more", async () => {
    const events: ChatEvent[] = [];
    for await (const e of readChatEvents(body(['data: {"type":"text_delta","text":"ok"}\n\n', "data: " + "x".repeat(MAX_FRAME_CHARS + 10), 'data: {"type":"text_delta","text":"never"}\n\n']))) events.push(e);
    expect(events.map((e) => e.type)).toEqual(["text_delta", "error"]);
    expect((events[1] as { code: string; retryable: boolean }).code).toBe("stream_too_large");
    expect((events[1] as { retryable: boolean }).retryable).toBe(false);
  });
});

describe("reply cap", () => {
  const anon: SessionState = { sessionId: "s", tier: "Anonymous", scopes: [], expiresAt: "2030-01-01T00:00:00Z" };
  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = "";
  });

  it("stops appending text at the cap", async () => {
    const chunk = "a".repeat(100_000);
    const events: ChatEvent[] = [
      { type: "text_delta", text: chunk },
      { type: "text_delta", text: chunk },
      { type: "text_delta", text: chunk },
      { type: "text_delta", text: "tail" },
    ];
    const svc: ChatServices = {
      transport: {
        open: vi.fn(async () => "success" as const),
        retryOnSignIn: vi.fn(async () => true),
        send: vi.fn(async function* () {
          for (const e of events) yield e;
        }),
        dispose: vi.fn(),
      },
      auth: {
        session: vi.fn(async () => anon),
        startLogin: vi.fn(async () => null),
        startElevate: vi.fn(async () => null),
        status: vi.fn(async () => "Pending" as const),
        logoutUrl: vi.fn(() => "https://chat.biatec.io/auth/logout"),
      },
      pollMs: 1,
      timeoutMs: 50,
    };
    const w = mount(BiatecChatRoot, { props: { options: resolveOptions({ backendUrl: "https://chat.biatec.io", clientKey: "k" }), services: svc }, attachTo: document.body });
    await w.get(".bc-launcher").trigger("click");
    await flushPromises();
    await w.get("textarea").setValue("hi");
    await w.get("form").trigger("submit");
    await flushPromises();
    const shown = w.get("[data-role=assistant]").text().length;
    expect(shown).toBeGreaterThan(0);
    expect(shown).toBeLessThan(MAX_REPLY_CHARS + 500); // the bubble also renders a few characters of chrome (label, time)
    expect(shown).toBeLessThan(3 * 100_000);
    expect(w.get("[data-role=assistant]").text()).not.toContain("tail");
    w.unmount();
  });
});
