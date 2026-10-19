import type { ChatEvent } from "@biatec/chat-protocol";

export const CHAT_EVENT_TYPES = [
  "text_delta",
  "tool_status",
  "preview_card",
  "consent_status",
  "sign_in_card",
  "error",
] as const;

const isStr = (v: unknown): v is string => typeof v === "string";
const isObj = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null;

/** Narrow an unknown JSON value to a ChatEvent: the discriminator and the required fields of that event type. */
export function isChatEvent(v: unknown): v is ChatEvent {
  if (!isObj(v) || !isStr(v.type)) return false;
  switch (v.type) {
    case "text_delta":
      return isStr(v.text);
    case "tool_status":
      return isStr(v.toolCallId) && isStr(v.label) && isStr(v.state);
    case "preview_card":
      return isObj(v.preview);
    case "consent_status":
      return isObj(v.consent) && isStr(v.consent.previewId) && isStr(v.consent.state);
    case "sign_in_card":
      return isStr(v.signInUrl) && isStr(v.reason);
    case "error":
      return isStr(v.code) && isStr(v.message) && typeof v.retryable === "boolean";
    default:
      return false;
  }
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

/** Read a fetch response body as typed chat events. Malformed or unknown payloads are skipped, and an incomplete final frame is discarded (SSE spec). */
export async function* readChatEvents(body: ReadableStream<Uint8Array>): AsyncGenerator<ChatEvent> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  const parser = new SseParser();
  try {
    for (;;) {
      const { value, done } = await reader.read();
      const payloads = done ? [] : parser.push(decoder.decode(value, { stream: true }));
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
