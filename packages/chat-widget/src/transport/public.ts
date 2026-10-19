import type { ChatEvent } from "@biatec/chat-protocol";
import { readChatEvents } from "./sse";
import { CLIENT_KEY_HEADER, PATHS, TransportError, type ChatTransport, type SendOptions, type TransportConfig } from "./types";

export const CAPABILITY_HEADER = "X-Biatec-Conversation";
export const CONTINUATION_HEADER = "X-Biatec-Continuation";
/** Error code of a public conversation the server closed on idle (spec: "a distinct error code"). */
export const IDLE_CLOSED_CODE = "conversation_idle_closed";
export const IDLE_CLOSED_NOTICE = "Your earlier conversation was closed after a period of inactivity, so this is a new one.";

/**
 * Public (cookie-less) transport. The anonymous capability and the single-use continuation token live in memory
 * only. They are never persisted, shown or put in URLs.
 */
export class PublicTransport implements ChatTransport {
  readonly kind = "public" as const;
  private readonly f: typeof fetch;
  private capability: string | null = null;
  private continuation: string | null = null;
  private previousContinuation: string | null = null;

  constructor(private readonly cfg: TransportConfig) {
    this.f = cfg.fetch ?? ((...a) => fetch(...a));
  }

  /** Current anonymous state, for adoption when switching to the cookie transport. */
  get state(): { conversationId: string; continuationToken: string } | null {
    return this.capability && this.continuation ? { conversationId: this.capability, continuationToken: this.continuation } : null;
  }

  private post(opts: SendOptions, token: string | null): Promise<Response> {
    return this.f(this.cfg.backendUrl + PATHS.publicChat, {
      method: "POST",
      credentials: "omit",
      headers: { "Content-Type": "application/json", Accept: "text/event-stream", [CLIENT_KEY_HEADER]: this.cfg.clientKey },
      body: JSON.stringify({
        message: opts.message,
        conversationId: this.capability,
        continuationToken: token,
        context: opts.context ?? null,
        widgetVersion: opts.widgetVersion ?? null,
      }),
      signal: opts.signal,
    });
  }

  private async fail(res: Response): Promise<never> {
    let code: string | undefined;
    try {
      code = ((await res.json()) as { code?: string }).code;
    } catch {
      /* no body */
    }
    throw new TransportError(`public chat failed: ${res.status}`, res.status, code);
  }

  async *send(opts: SendOptions, isRetry = false): AsyncGenerator<ChatEvent> {
    const sentToken = this.continuation;
    let res: Response;
    let lostReplyRetried = false;
    try {
      res = await this.post(opts, sentToken);
    } catch (e) {
      if (opts.signal?.aborted || !sentToken) throw e;
      // Reply lost: retry once with the SAME token (the server may have consumed it and replays the stored reply).
      lostReplyRetried = true;
      res = await this.post(opts, sentToken);
    }
    if (!res.ok || !res.body) {
      let code: string | undefined;
      try {
        code = ((await res.clone().json()) as { code?: string }).code;
      } catch {
        /* no body */
      }
      if (code === IDLE_CLOSED_CODE && !isRetry) {
        // The earlier conversation ended on idle: start a new one transparently and tell the user in one line.
        this.reset();
        this.cfg.onNotice?.(IDLE_CLOSED_NOTICE);
        yield* this.send(opts, true);
        return;
      }
      if (code === IDLE_CLOSED_CODE) this.reset();
      return await this.fail(res);
    }
    this.adoptHeaders(res, sentToken);
    // A stream that dies before its first event is a lost reply too: retry once with the same token.
    let first = true;
    try {
      for await (const e of readChatEvents(res.body)) {
        first = false;
        yield e;
      }
    } catch (e) {
      if (!first || lostReplyRetried || opts.signal?.aborted || !sentToken) throw e;
      const again = await this.post(opts, sentToken);
      if (!again.ok || !again.body) return await this.fail(again);
      this.adoptHeaders(again, sentToken);
      yield* readChatEvents(again.body);
    }
  }

  private adoptHeaders(res: Response, sentToken: string | null): void {
    const cap = res.headers.get(CAPABILITY_HEADER);
    const next = res.headers.get(CONTINUATION_HEADER);
    if (cap) this.capability = cap;
    if (next) {
      this.previousContinuation = sentToken;
      this.continuation = next;
    }
  }

  /** Forget the conversation (adopted, or closed by the server on idle); the next message starts a new one. */
  reset(): void {
    this.capability = this.continuation = this.previousContinuation = null;
  }

  /** Ask the public endpoint whether the calling Origin is registered for this tenant. */
  async isOriginRegistered(): Promise<boolean> {
    try {
      const res = await this.f(this.cfg.backendUrl + PATHS.publicOrigin, {
        credentials: "omit",
        headers: { [CLIENT_KEY_HEADER]: this.cfg.clientKey },
      });
      if (!res.ok) return false;
      return ((await res.json()) as { registered?: boolean }).registered === true;
    } catch {
      return false;
    }
  }
}
