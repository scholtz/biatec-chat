import type { ChatEvent } from "@biatec/chat-protocol";

export const CHAT_EVENT_TYPES = [
  "text_delta",
  "tool_status",
  "preview_card",
  "consent_status",
  "sign_in_card",
  "error",
] as const;

/** Narrow an unknown JSON value to a ChatEvent by its discriminator. Unknown event types are dropped by callers. */
export function isChatEvent(v: unknown): v is ChatEvent {
  return (
    typeof v === "object" &&
    v !== null &&
    typeof (v as { type?: unknown }).type === "string" &&
    (CHAT_EVENT_TYPES as readonly string[]).includes((v as { type: string }).type)
  );
}

/** Incremental server-sent-events parser. Feed text chunks; it returns the data payloads completed so far. */
export class SseParser {
  private buffer = "";
  private data: string[] = [];

  push(chunk: string): string[] {
    this.buffer += chunk;
    const out: string[] = [];
    for (;;) {
      const m = /\r\n|\n|\r/.exec(this.buffer);
      if (!m) break;
      // A lone trailing CR may be the first half of CRLF: wait for more input.
      if (m[0] === "\r" && m.index + 1 === this.buffer.length) break;
      const line = this.buffer.slice(0, m.index);
      this.buffer = this.buffer.slice(m.index + m[0].length);
      if (line === "") {
        if (this.data.length) out.push(this.data.join("\n"));
        this.data = [];
      } else if (line.startsWith("data:")) {
        this.data.push(line.slice(5).replace(/^ /, ""));
      }
      // event:, id:, retry: and comment lines carry nothing we need: the JSON payload holds its own `type`.
    }
    return out;
  }
}

/** Read a fetch response body as typed chat events. Malformed or unknown payloads are skipped. */
export async function* readChatEvents(body: ReadableStream<Uint8Array>): AsyncGenerator<ChatEvent> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  const parser = new SseParser();
  try {
    for (;;) {
      const { value, done } = await reader.read();
      const payloads = parser.push(done ? decoder.decode() + "\n\n" : decoder.decode(value, { stream: true }));
      for (const p of payloads) {
        let parsed: unknown;
        try {
          parsed = JSON.parse(p);
        } catch {
          continue;
        }
        if (isChatEvent(parsed)) yield parsed;
      }
      if (done) return;
    }
  } finally {
    reader.releaseLock();
  }
}
