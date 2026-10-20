import type { ConversationList, ConversationSummary, ErasureResponse } from "@biatec/chat-protocol";
import { CLIENT_KEY_HEADER, CSRF_HEADER, type TransportConfig } from "../../transport/types";

/** Backend paths of T-060's export view and erasure (RetentionEndpoints). */
export const RETENTION_PATHS = {
  list: "/session/conversations",
  evidence: (conversationId: string) => `/session/conversations/${encodeURIComponent(conversationId)}/evidence`,
  erase: "/session/erase",
} as const;

/** Why a retention call failed: 401 (no live session or a guest erase), 404, 409 `ActionInFlight`, or anything else. */
export type RetentionErrorCode = "unauthorized" | "not_found" | "action_in_flight" | "failed";

export class RetentionError extends Error {
  constructor(
    readonly code: RetentionErrorCode,
    readonly status: number,
  ) {
    super(`${code} (HTTP ${status})`);
    this.name = "RetentionError";
  }
}

export interface EvidenceFile {
  /** File name from Content-Disposition, or the backend's naming scheme. */
  name: string;
  /** The signed bundle (JSON) exactly as the backend sent it. */
  text: string;
}

/** Format tag of the combined "export first" file: one object holding every per-conversation bundle unchanged. */
export const COMBINED_FORMAT = "biatec-evidence-bundles";

/**
 * One file for several conversations, so the confirm step saves a single download. Each element of `bundles` is a
 * backend bundle as parsed from its JSON and verifies on its own (the signatures cover the canonical JSON form).
 */
export function combineBundles(files: readonly EvidenceFile[], now: Date = new Date()): EvidenceFile {
  const bundles = files.map((f) => JSON.parse(f.text) as unknown);
  const stamp = now.toISOString().slice(0, 19).replace(/[-:T]/g, "");
  return {
    name: `biatec-evidence-all-${stamp}.json`,
    text: JSON.stringify({ format: COMBINED_FORMAT, version: 1, exportedAt: now.toISOString(), bundles }, null, 2),
  };
}

export interface RetentionApi {
  /** GET /session/conversations: the caller's own conversations, newest first. */
  list(): Promise<ConversationSummary[]>;
  /** GET /session/conversations/{id}/evidence: the signed evidence bundle of one of them. */
  evidence(conversationId: string): Promise<EvidenceFile>;
  /** POST /session/erase (signed-in only). Throws `action_in_flight` on 409. */
  erase(): Promise<ErasureResponse>;
}

function errorFor(status: number, body: unknown): RetentionError {
  const code = body && typeof body === "object" ? (body as Record<string, unknown>).error : null;
  if (status === 409 || code === "ActionInFlight") return new RetentionError("action_in_flight", status);
  if (status === 401) return new RetentionError("unauthorized", status);
  if (status === 404) return new RetentionError("not_found", status);
  return new RetentionError("failed", status);
}

function fileName(disposition: string | null, conversationId: string): string {
  const m = disposition?.match(/filename="?([\w.-]+)"?/);
  return m?.[1] ?? `biatec-evidence-${conversationId.replace(/-/g, "")}.json`;
}

/** Calls the backend with the HttpOnly session cookie (and, for erase, the CSRF headers); it never sees a token. */
export class RetentionClient implements RetentionApi {
  private readonly f: typeof fetch;
  constructor(private readonly cfg: TransportConfig) {
    this.f = cfg.fetch ?? ((...a) => fetch(...a));
  }

  private url(path: string): string {
    return this.cfg.backendUrl.replace(/\/+$/, "") + path;
  }

  private async call(path: string, init: RequestInit): Promise<Response> {
    let res: Response;
    try {
      res = await this.f(this.url(path), { credentials: "include", ...init });
    } catch {
      throw new RetentionError("failed", 0);
    }
    if (!res.ok) throw errorFor(res.status, await res.json().catch(() => null));
    return res;
  }

  async list(): Promise<ConversationSummary[]> {
    const res = await this.call(RETENTION_PATHS.list, { method: "GET", headers: { Accept: "application/json" } });
    const j = (await res.json().catch(() => null)) as ConversationList | null;
    if (!j || !Array.isArray(j.conversations)) throw new RetentionError("failed", res.status);
    return j.conversations;
  }

  async evidence(conversationId: string): Promise<EvidenceFile> {
    const res = await this.call(RETENTION_PATHS.evidence(conversationId), { method: "GET", headers: { Accept: "application/json" } });
    return { name: fileName(res.headers.get("Content-Disposition"), conversationId), text: await res.text() };
  }

  async erase(): Promise<ErasureResponse> {
    const res = await this.call(RETENTION_PATHS.erase, {
      method: "POST",
      headers: { "Content-Type": "application/json", [CSRF_HEADER]: "1", [CLIENT_KEY_HEADER]: this.cfg.clientKey },
      body: "{}",
    });
    const j = (await res.json().catch(() => null)) as ErasureResponse | null;
    if (!j || j.status !== "erased") throw new RetentionError("failed", res.status);
    return j;
  }
}

/** How long a saved file's object URL stays valid: the browser may still be reading it well after the click. */
export const REVOKE_AFTER_MS = 60_000;
let lastUrl: { href: string; timer: ReturnType<typeof setTimeout> } | null = null;
function releaseLast(): void {
  if (!lastUrl) return;
  clearTimeout(lastUrl.timer);
  URL.revokeObjectURL(lastUrl.href);
  lastUrl = null;
}

/**
 * Hands a downloaded file to the browser's save flow. The anchor lives in the widget root (inside the shadow root
 * when there is one) for the moment of the click. The object URL is released on the next save or after
 * REVOKE_AFTER_MS, whichever comes first.
 */
export function saveFile(file: EvidenceFile, parent: Node | null): void {
  releaseLast();
  const href = URL.createObjectURL(new Blob([file.text], { type: "application/json" }));
  lastUrl = { href, timer: setTimeout(releaseLast, REVOKE_AFTER_MS) };
  const a = document.createElement("a");
  a.href = href;
  a.download = file.name;
  a.rel = "noopener";
  a.hidden = true;
  (parent ?? document.body).appendChild(a);
  try {
    a.click();
  } finally {
    a.remove();
  }
}
