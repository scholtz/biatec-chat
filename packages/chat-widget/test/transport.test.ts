import { describe, expect, it, vi, afterEach } from "vitest";
import type { ChatEvent } from "@biatec/chat-protocol";
import { CookieTransport, Keepalive, SESSION_EXPIRED, SseParser, TransportError, TransportManager, readChatEvents } from "../src/transport";

const enc = new TextEncoder();
function sse(chunks: string[], headers: Record<string, string> = {}): Response {
  const stream = new ReadableStream<Uint8Array>({
    start(c) {
      for (const ch of chunks) c.enqueue(enc.encode(ch));
      c.close();
    },
  });
  return new Response(stream, { status: 200, headers });
}
const json = (o: unknown, status = 200) => new Response(JSON.stringify(o), { status, headers: { "Content-Type": "application/json" } });
const frame = (e: ChatEvent) => `data: ${JSON.stringify(e)}\n\n`;
const cfg = (f: typeof fetch) => ({ backendUrl: "https://api.test", clientKey: "k", fetch: f });
async function collect(g: AsyncGenerator<ChatEvent>) {
  const out: ChatEvent[] = [];
  for await (const e of g) out.push(e);
  return out;
}
afterEach(() => vi.useRealTimers());

describe("SSE", () => {
  it("parses typed events split across chunks and CRLF, skips junk", async () => {
    const a: ChatEvent = { type: "text_delta", text: "Hel" };
    const b: ChatEvent = { type: "error", code: "x", message: "m", retryable: true };
    const raw = frame(a) + ": comment\n\ndata: not json\n\ndata: {\"type\":\"bogus\"}\n\n" + frame(b).replace(/\n/g, "\r\n");
    const mid = 17;
    const body = sse([raw.slice(0, mid), raw.slice(mid)]).body!;
    const events = [];
    for await (const e of readChatEvents(body)) events.push(e);
    expect(events).toEqual([a, b]);
  });

  it("discards a truncated final frame and malformed payloads", async () => {
    const ok: ChatEvent = { type: "text_delta", text: "a" };
    const body = sse([frame(ok), 'data: {"type":"text_delta"}\n\n', 'data: {"type":"error","code":"x"}\n\n', 'data: {"type":"text_delta","text":"cut"}\n']).body!;
    const events = [];
    for await (const e of readChatEvents(body)) events.push(e);
    expect(events).toEqual([ok]);
  });

  it("joins multi-line data", () => {
    expect(new SseParser().push("data: a\ndata: b\n\n")).toEqual(["a\nb"]);
  });
});

describe("cookie chat errors", () => {
  it("carries the problem code of a refused chat call", async () => {
    const f = vi.fn(async () => json({ status: 401, code: "session_expired" }, 401));
    const err = await collect(new CookieTransport(cfg(f as unknown as typeof fetch)).send({ message: "x" })).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(TransportError);
    expect((err as TransportError).status).toBe(401);
    expect((err as TransportError).code).toBe(SESSION_EXPIRED);
  });

  it("leaves the code empty when the body is not a problem", async () => {
    const f = vi.fn(async () => new Response("nope", { status: 500 }));
    const err = (await collect(new CookieTransport(cfg(f as unknown as typeof fetch)).send({ message: "x" })).catch((e: unknown) => e)) as TransportError;
    expect(err.status).toBe(500);
    expect(err.code).toBeUndefined();
  });
});

describe("probe and fallback", () => {
  it("probe success switches to cookie transport, credentialed, CSRF header on chat", async () => {
    const calls: [string, RequestInit | undefined][] = [];
    const f = vi.fn(async (u: string, i?: RequestInit) => {
      calls.push([u, i]);
      if (u.endsWith("/session/probe")) return json({ cookieReturned: calls.filter((c) => c[0].endsWith("/probe")).length > 1 });
      return sse([frame({ type: "text_delta", text: "hi" })]);
    });
    const m = new TransportManager(cfg(f as unknown as typeof fetch));
    expect(m.kind).toBe("public");
    expect(await m.open()).toBe("success");
    expect(m.kind).toBe("cookie");
    const ev = await collect(m.send({ message: "yo" }));
    expect(ev[0]).toEqual({ type: "text_delta", text: "hi" });
    const chat = calls.find((c) => c[0].endsWith("/chat/messages"))!;
    expect(chat[1]!.credentials).toBe("include");
    expect((chat[1]!.headers as Record<string, string>)["X-Biatec-Chat"]).toBe("1");
    expect(JSON.stringify(calls)).not.toMatch(/authorization|bearer|access_token/i);
  });

  it("unregistered origin stays public, warns once, never retries", async () => {
    vi.useFakeTimers();
    const warn = vi.fn();
    const f = vi.fn(async (u: string) => {
      if (u.endsWith("/session/probe")) throw new TypeError("CORS");
      if (u.endsWith("/public/origin")) return json({ registered: false });
      throw new Error(u);
    });
    const m = new TransportManager(cfg(f as unknown as typeof fetch), { warn, backoffMs: [10] });
    expect(await m.open()).toBe("failed-unregistered");
    await m.open();
    await vi.advanceTimersByTimeAsync(1000);
    expect(warn).toHaveBeenCalledTimes(1);
    expect(m.kind).toBe("public");
    expect(await m.retryOnSignIn()).toBe(false);
    expect(f.mock.calls.filter((c) => String(c[0]).endsWith("/probe"))).toHaveLength(1);
  });

  it("transient /public/origin failure is not unregistered: keeps retrying", async () => {
    vi.useFakeTimers();
    let originUp = false;
    let cookieWorks = false;
    const f = vi.fn(async (u: string) => {
      if (u.endsWith("/session/probe")) return cookieWorks ? json({ cookieReturned: true }) : new Response("", { status: 503 });
      if (u.endsWith("/public/origin")) return originUp ? json({ registered: true }) : new Response("", { status: 503 });
      throw new Error(u);
    });
    const m = new TransportManager(cfg(f as unknown as typeof fetch), { backoffMs: [100], warn: vi.fn() });
    expect(await m.open()).toBe("failed-registered");
    originUp = true;
    cookieWorks = true;
    await vi.advanceTimersByTimeAsync(150);
    expect(m.kind).toBe("cookie");
    m.dispose();
  });

  it("registered origin: provisional public, backoff retry switches and adopts conversation", async () => {
    vi.useFakeTimers();
    let cookieWorks = false;
    const bodies: Record<string, unknown> = {};
    const f = vi.fn(async (u: string, i?: RequestInit) => {
      if (u.endsWith("/session/probe")) {
        if (!cookieWorks) return new Response("", { status: 503 });
        return json({ cookieReturned: true });
      }
      if (u.endsWith("/public/origin")) return json({ registered: true });
      if (u.endsWith("/public/chat")) {
        bodies.pub = JSON.parse(String(i!.body));
        return sse([frame({ type: "text_delta", text: "a" })], { "X-Biatec-Conversation": "cap1", "X-Biatec-Continuation": "tok1" });
      }
      if (u.endsWith("/session/adopt")) {
        bodies.adopt = JSON.parse(String(i!.body));
        expect(i!.credentials).toBe("include");
        return json({});
      }
      throw new Error(u);
    });
    const onSwitch = vi.fn();
    const m = new TransportManager(cfg(f as unknown as typeof fetch), { backoffMs: [100], onSwitch });
    expect(await m.open()).toBe("failed-registered");
    expect(m.kind).toBe("public");
    await collect(m.send({ message: "hello" }));
    expect((bodies.pub as { conversationId: unknown }).conversationId).toBeNull();
    cookieWorks = true;
    await vi.advanceTimersByTimeAsync(150);
    expect(m.kind).toBe("cookie");
    expect(onSwitch).toHaveBeenCalledWith("cookie");
    expect(bodies.adopt).toEqual({ conversationId: "cap1", continuationToken: "tok1" });
  });

  it("Sign in click retries the probe immediately", async () => {
    let ok = false;
    const f = vi.fn(async (u: string) => {
      if (u.endsWith("/session/probe")) return ok ? json({ cookieReturned: true }) : new Response("", { status: 500 });
      return json({ registered: true });
    });
    const m = new TransportManager(cfg(f as unknown as typeof fetch), { backoffMs: [1e9] });
    await m.open();
    ok = true;
    expect(await m.retryOnSignIn()).toBe(true);
    expect(m.kind).toBe("cookie");
    m.dispose();
  });
});

describe("public transport continuity", () => {
  it("sends capability and the latest continuation token", async () => {
    const sent: { conversationId: string | null; continuationToken: string | null }[] = [];
    let n = 0;
    const f = vi.fn(async (u: string, i?: RequestInit) => {
      if (u.endsWith("/session/probe")) throw new TypeError("x");
      if (u.endsWith("/public/origin")) return json({ registered: false });
      sent.push(JSON.parse(String(i!.body)));
      n++;
      return sse([frame({ type: "text_delta", text: "x" })], { "X-Biatec-Conversation": "c", "X-Biatec-Continuation": "t" + n });
    });
    const m = new TransportManager(cfg(f as unknown as typeof fetch), { warn: () => {} });
    await m.open();
    await collect(m.send({ message: "1" }));
    await collect(m.send({ message: "2" }));
    expect(sent[1]).toMatchObject({ conversationId: "c", continuationToken: "t1" });
  });
});

describe("review fixes", () => {
  const pubHeaders = (n: number) => ({ "X-Biatec-Conversation": "cap", "X-Biatec-Continuation": "t" + n });

  it("failed adopt keeps the public conversation and retries later", async () => {
    vi.useFakeTimers();
    let adoptOk = false;
    const bodies: { conversationId: string | null; continuationToken: string | null }[] = [];
    const f = vi.fn(async (u: string, i?: RequestInit) => {
      if (u.endsWith("/session/probe")) return json({ cookieReturned: true });
      if (u.endsWith("/session/adopt")) return adoptOk ? json({}) : new Response("", { status: 409 });
      if (u.endsWith("/public/chat")) {
        bodies.push(JSON.parse(String(i!.body)));
        return sse([frame({ type: "text_delta", text: "a" })], pubHeaders(bodies.length));
      }
      throw new Error(u);
    });
    const m = new TransportManager(cfg(f as unknown as typeof fetch), { backoffMs: [50] });
    // Start a public conversation before the probe is allowed to switch: send first, then open.
    await collect(m.send({ message: "1" }));
    expect(await m.open()).toBe("failed-registered");
    expect(m.kind).toBe("public");
    await collect(m.send({ message: "2" }));
    expect(bodies[1]).toMatchObject({ conversationId: "cap", continuationToken: "t1" });
    adoptOk = true;
    await vi.advanceTimersByTimeAsync(100);
    expect(m.kind).toBe("cookie");
    m.dispose();
  });

  it("concurrent Sign in clicks share one probe and adopt once", async () => {
    let adopts = 0;
    const f = vi.fn(async (u: string) => {
      if (u.endsWith("/session/probe")) return json({ cookieReturned: true });
      if (u.endsWith("/session/adopt")) {
        adopts++;
        return json({});
      }
      return sse([frame({ type: "text_delta", text: "a" })], pubHeaders(1));
    });
    const m = new TransportManager(cfg(f as unknown as typeof fetch), { backoffMs: [1e9] });
    await collect(m.send({ message: "1" }));
    const r = await Promise.all([m.retryOnSignIn(), m.retryOnSignIn(), m.open()]);
    expect(r).toEqual([true, true, expect.anything()]);
    expect(adopts).toBe(1);
    m.dispose();
  });

  it("a throwing probe reschedules the background retry", async () => {
    vi.useFakeTimers();
    let probes = 0;
    const f = vi.fn(async (u: string) => {
      if (u.endsWith("/session/probe")) {
        probes++;
        return probes <= 2 ? new Response("", { status: 500 }) : json({ cookieReturned: true });
      }
      if (u.endsWith("/session/adopt")) throw new Error("boom");
      return json({ registered: true });
    });
    const m = new TransportManager(cfg(f as unknown as typeof fetch), { backoffMs: [10] });
    await m.open();
    await vi.advanceTimersByTimeAsync(500);
    expect(m.kind).toBe("cookie");
    m.dispose();
  });

  it("lost reply retries with the same token as the first attempt", async () => {
    const tokens: (string | null)[] = [];
    let n = 0;
    const f = vi.fn(async (u: string, i?: RequestInit) => {
      if (u.endsWith("/session/probe")) throw new TypeError("x");
      if (u.endsWith("/public/origin")) return json({ registered: false });
      n++;
      tokens.push(JSON.parse(String(i!.body)).continuationToken);
      if (n === 3) throw new TypeError("network lost");
      return sse([frame({ type: "text_delta", text: "x" })], pubHeaders(n));
    });
    const m = new TransportManager(cfg(f as unknown as typeof fetch), { warn: () => {} });
    await m.open();
    await collect(m.send({ message: "1" }));
    await collect(m.send({ message: "2" }));
    await collect(m.send({ message: "3" }));
    expect(tokens).toEqual([null, "t1", "t2", "t2"]);
  });

  it("a stream that fails before its first event is retried with the same token", async () => {
    const tokens: (string | null)[] = [];
    const f = vi.fn(async (u: string, i?: RequestInit) => {
      if (u.endsWith("/session/probe")) throw new TypeError("x");
      if (u.endsWith("/public/origin")) return json({ registered: false });
      tokens.push(JSON.parse(String(i!.body)).continuationToken);
      if (tokens.length === 2) {
        const body = new ReadableStream<Uint8Array>({ start: (c) => c.error(new TypeError("reset")) });
        return new Response(body, { status: 200, headers: pubHeaders(9) });
      }
      return sse([frame({ type: "text_delta", text: "x" })], pubHeaders(tokens.length));
    });
    const m = new TransportManager(cfg(f as unknown as typeof fetch), { warn: () => {} });
    await m.open();
    await collect(m.send({ message: "1" }));
    const ev = await collect(m.send({ message: "2" }));
    expect(ev).toHaveLength(1);
    expect(tokens).toEqual([null, "t1", "t1"]);
  });

  it("idle-closed conversation is replaced transparently with a one-line note", async () => {
    const bodies: { conversationId: string | null }[] = [];
    const notice = vi.fn();
    const f = vi.fn(async (u: string, i?: RequestInit) => {
      if (u.endsWith("/session/probe")) throw new TypeError("x");
      if (u.endsWith("/public/origin")) return json({ registered: false });
      bodies.push(JSON.parse(String(i!.body)));
      if (bodies.length === 2) return json({ code: "conversation_idle_closed" }, 410);
      return sse([frame({ type: "text_delta", text: "x" })], pubHeaders(bodies.length));
    });
    const m = new TransportManager({ ...cfg(f as unknown as typeof fetch), onNotice: notice }, { warn: () => {} });
    await m.open();
    await collect(m.send({ message: "1" }));
    const ev = await collect(m.send({ message: "2" }));
    expect(ev).toHaveLength(1);
    expect(notice).toHaveBeenCalledTimes(1);
    expect(bodies[2]).toMatchObject({ conversationId: null, continuationToken: null });
  });

  it("keepalive pings the cookie endpoint with credentials and the CSRF header", async () => {
    const calls: [string, RequestInit | undefined][] = [];
    const f = vi.fn(async (u: string, i?: RequestInit) => {
      calls.push([u, i]);
      return json({ cookieReturned: true });
    });
    const m = new TransportManager(cfg(f as unknown as typeof fetch));
    await m.open();
    m.keepalive.hold("preview");
    await vi.waitFor(() => undefined);
    m.keepalive.stop();
    // Drive the ping directly through the transport the manager owns.
    const ok = await (m as unknown as { cookie: { keepalive(): Promise<boolean> } }).cookie.keepalive();
    expect(ok).toBe(true);
    const ka = calls.find((c) => c[0].endsWith("/session/keepalive"))!;
    expect(ka[1]!.credentials).toBe("include");
    expect((ka[1]!.headers as Record<string, string>)["X-Biatec-Chat"]).toBe("1");
    m.dispose();
  });
});

describe("keepalive", () => {
  it("pings while held and stops when released", async () => {
    vi.useFakeTimers();
    const ping = vi.fn(async () => true);
    const k = new Keepalive(ping, 1000);
    k.hold("preview");
    k.hold("consent");
    await vi.advanceTimersByTimeAsync(2500);
    expect(ping).toHaveBeenCalledTimes(2);
    k.release("preview");
    expect(k.running).toBe(true);
    k.release("consent");
    expect(k.running).toBe(false);
    await vi.advanceTimersByTimeAsync(5000);
    expect(ping).toHaveBeenCalledTimes(2);
  });
});

describe("evidence download link", () => {
  it("points at the backend export endpoint", async () => {
    const { evidenceDownloadUrl } = await import("../src/transport");
    expect(evidenceDownloadUrl("https://chat.biatec.io/")).toBe("https://chat.biatec.io/session/evidence");
    expect(evidenceDownloadUrl("https://chat.biatec.io")).toBe("https://chat.biatec.io/session/evidence");
  });
});
