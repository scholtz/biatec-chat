import type { ConsentStatus } from "@biatec/chat-protocol";
import { isConsentStatus, readNamedEvents } from "../../transport/sse";
import { CLIENT_KEY_HEADER, CSRF_HEADER, TransportError, type TransportConfig } from "../../transport/types";
import { DECIMAL_AMOUNT, isObj, str } from "./guards";
import { toConfirmResult, type ConfirmResult } from "./previews";

/** What the consent stream says, in order: a status per poll, then (once the consent has ended) the confirm result. */
export type ConsentStreamEvent =
  | { kind: "status"; consent: ConsentStatus }
  | { kind: "result"; result: ConfirmResult }
  | { kind: "error"; message: string };

/**
 * One answer of POST /chat/previews/{id}/consent. `ok: false` carries the HTTP status: 401 (the session is gone), 404 or
 * 409 (the preview or its consent is no longer there) are final; anything else is worth another try.
 */
export type ConsentPoll = { ok: true; consent: ConsentStatus; result: ConfirmResult | null } | { ok: false; status: number; message: string };

export interface ConsentApi {
  /** POST .../consent: one poll. Rejects only on a network error. */
  poll(previewId: string, signal?: AbortSignal): Promise<ConsentPoll>;
  /** POST .../consent/stream: server-sent events until the consent ends. Throws TransportError for a non-2xx answer. */
  stream(previewId: string, signal?: AbortSignal): AsyncGenerator<ConsentStreamEvent>;
  /** GET .../consent/prediction: the card's line about phone approval, or null when there is none. Never rejects. */
  prediction(previewId: string, signal?: AbortSignal, valueEur?: string | null): Promise<string | null>;
}

export const CONSENT_PATHS = {
  poll: (id: string) => `/chat/previews/${encodeURIComponent(id)}/consent`,
  stream: (id: string) => `/chat/previews/${encodeURIComponent(id)}/consent/stream`,
  prediction: (id: string) => `/chat/previews/${encodeURIComponent(id)}/consent/prediction`,
} as const;

/** Longest prediction text the card shows; a real one is a sentence. */
const MAX_PREDICTION_CHARS = 300;

/** Calls the backend with the HttpOnly session cookie and the CSRF headers; it never sees a token. */
export class ConsentClient implements ConsentApi {
  private readonly f: typeof fetch;
  constructor(private readonly cfg: TransportConfig) {
    this.f = cfg.fetch ?? ((...a) => fetch(...a));
  }

  private headers(): Record<string, string> {
    return { "Content-Type": "application/json", [CSRF_HEADER]: "1", [CLIENT_KEY_HEADER]: this.cfg.clientKey };
  }

  async poll(previewId: string, signal?: AbortSignal): Promise<ConsentPoll> {
    const res = await this.f(this.cfg.backendUrl + CONSENT_PATHS.poll(previewId), { method: "POST", credentials: "include", headers: this.headers(), signal });
    const j = (await res.json().catch(() => null)) as unknown;
    if (!res.ok || !isObj(j) || !isConsentStatus(j.consent)) return { ok: false, status: res.status, message: isObj(j) ? str(j.message) : "" };
    return { ok: true, consent: j.consent as ConsentStatus, result: isObj(j.result) ? toConfirmResult(j.result, res.status) : null };
  }

  async *stream(previewId: string, signal?: AbortSignal): AsyncGenerator<ConsentStreamEvent> {
    const res = await this.f(this.cfg.backendUrl + CONSENT_PATHS.stream(previewId), {
      method: "POST",
      credentials: "include",
      headers: { ...this.headers(), Accept: "text/event-stream" },
      signal,
    });
    if (!res.ok || !res.body) throw new TransportError(`consent stream failed: ${res.status}`, res.status);
    for await (const frame of readNamedEvents(res.body)) {
      let data: unknown;
      try {
        data = JSON.parse(frame.data);
      } catch {
        continue;
      }
      if (!isObj(data)) continue;
      if (frame.event === "consent_status" && isConsentStatus(data.consent)) yield { kind: "status", consent: data.consent as ConsentStatus };
      else if (frame.event === "confirm_result") yield { kind: "result", result: toConfirmResult(data, res.status) };
      else if (frame.event === "error") yield { kind: "error", message: str(data.message) };
    }
  }

  async prediction(previewId: string, signal?: AbortSignal, valueEur?: string | null): Promise<string | null> {
    try {
      const query = valueEur && /^\d{1,15}(?:\.\d{1,18})?$/.test(valueEur) ? `?valueEur=${valueEur}` : "";
      const res = await this.f(this.cfg.backendUrl + CONSENT_PATHS.prediction(previewId) + query, {
        method: "GET",
        credentials: "include",
        headers: { [CSRF_HEADER]: "1", [CLIENT_KEY_HEADER]: this.cfg.clientKey },
        signal,
      });
      if (!res.ok) return null;
      const j = (await res.json().catch(() => null)) as unknown;
      const text = isObj(j) ? str(j.text).trim() : "";
      return text ? text.slice(0, MAX_PREDICTION_CHARS) : null;
    } catch {
      return null;
    }
  }
}
