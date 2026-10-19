import type { ChatEvent, HostContext } from "@biatec/chat-protocol";

export type TransportKind = "cookie" | "public";

export interface SendOptions {
  message: string;
  context?: HostContext | null;
  widgetVersion?: string;
  signal?: AbortSignal;
}

/** What the UI talks to. It never needs to know which transport is active. */
export interface ChatTransport {
  readonly kind: TransportKind;
  send(opts: SendOptions): AsyncGenerator<ChatEvent>;
}

export class TransportError extends Error {
  constructor(
    message: string,
    readonly status?: number,
    readonly code?: string,
  ) {
    super(message);
    this.name = "TransportError";
  }
}

export interface TransportConfig {
  /** Backend base URL without trailing slash. */
  backendUrl: string;
  clientKey: string;
  fetch?: typeof fetch;
}

/** Header the backend requires on every state-changing call made from script. */
export const CSRF_HEADER = "X-Biatec-Chat";
export const CLIENT_KEY_HEADER = "X-Biatec-Client-Key";

/**
 * Backend paths. /session/probe and /session/keepalive exist today. The chat, public and adoption endpoints are
 * owned by later backend tasks; these paths are the widget's assumption and live in one place so they are easy to fix.
 */
export const PATHS = {
  probe: "/session/probe",
  keepalive: "/session/keepalive",
  cookieChat: "/chat",
  publicChat: "/public/chat",
  /** Public endpoint that says whether the calling Origin is registered for the tenant: { registered: boolean }. */
  publicOrigin: "/public/origin",
  /** Cookie endpoint that adopts an anonymous public conversation into the cookie session. */
  adopt: "/session/adopt",
} as const;
