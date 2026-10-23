import { vi } from "vitest";

export interface Call { url: string; init: RequestInit }

/** Replaces global fetch; `handler` returns [status, body] for a URL. Returns the recorded calls. */
export function mockFetch(handler: (url: string, init: RequestInit) => [number, unknown] | Promise<[number, unknown]>) {
  const calls: Call[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init: RequestInit = {}) => {
      calls.push({ url, init });
      const [status, body] = await handler(url, init);
      return new Response(body === undefined ? null : JSON.stringify(body), {
        status,
        headers: { "Content-Type": "application/json" },
      });
    }),
  );
  return calls;
}

export async function flush(times = 5) {
  for (let i = 0; i < times; i++) await new Promise((r) => setTimeout(r, 0));
}

export function header(call: Call, name: string): string | undefined {
  return (call.init.headers as Record<string, string>)[name];
}
