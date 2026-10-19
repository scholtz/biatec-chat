import type { ChatEvent } from "@biatec/chat-protocol";
import { readChatEvents } from "./sse";
import { CLIENT_KEY_HEADER, CSRF_HEADER, PATHS, TransportError, type ChatTransport, type SendOptions, type TransportConfig } from "./types";

/** Cookie transport: the session cookie is HttpOnly, so JS only sets `credentials: include` and the CSRF header. */
export class CookieTransport implements ChatTransport {
  readonly kind = "cookie" as const;
  private readonly f: typeof fetch;
  constructor(private readonly cfg: TransportConfig) {
    this.f = cfg.fetch ?? ((...a) => fetch(...a));
  }

  private headers(): Record<string, string> {
    return { "Content-Type": "application/json", [CSRF_HEADER]: "1", [CLIENT_KEY_HEADER]: this.cfg.clientKey };
  }

  /** The cookie identifies the conversation, so the body carries no conversation id. */
  async *send(opts: SendOptions): AsyncGenerator<ChatEvent> {
    const res = await this.f(this.cfg.backendUrl + PATHS.cookieChat, {
      method: "POST",
      credentials: "include",
      headers: { ...this.headers(), Accept: "text/event-stream" },
      body: JSON.stringify({ message: opts.message, context: opts.context ?? null, widgetVersion: opts.widgetVersion ?? null }),
      signal: opts.signal,
    });
    if (!res.ok || !res.body) throw new TransportError(`chat failed: ${res.status}`, res.status);
    yield* readChatEvents(res.body);
  }

  /** Extends the idle timeout only; call while a preview, consent or signing call is pending. */
  async keepalive(): Promise<boolean> {
    try {
      const res = await this.f(this.cfg.backendUrl + PATHS.keepalive, { method: "POST", credentials: "include", headers: this.headers() });
      return res.ok;
    } catch {
      return false;
    }
  }

  /** Hand the anonymous conversation over to the cookie session. The backend checks the live continuation token. */
  async adopt(conversationId: string, continuationToken: string): Promise<boolean> {
    try {
      const res = await this.f(this.cfg.backendUrl + PATHS.adopt, {
        method: "POST",
        credentials: "include",
        headers: this.headers(),
        body: JSON.stringify({ conversationId, continuationToken }),
      });
      return res.ok;
    } catch {
      return false;
    }
  }
}
