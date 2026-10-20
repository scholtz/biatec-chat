import type { ActionPreview, ActionPreviewStatus, ConsentStatus } from "@biatec/chat-protocol";
import { CLIENT_KEY_HEADER, CSRF_HEADER, type TransportConfig } from "../../transport/types";

/** A preview card in the conversation: the latest `preview_card` for its id plus the widget's own state. */
export interface PreviewEntry {
  preview: ActionPreview;
  consent: ConsentStatus | null;
  /** True while the confirm call is in flight; locks Confirm. */
  submitting: boolean;
  /** Plain-text note shown under the card (a confirm or cancel problem). Never HTML. */
  note: string;
  /** Set when the note needs the user to sign in again. */
  needsSignIn: boolean;
  /** Scope the backend asked to elevate (scope_required). */
  scope: string | null;
}

/** The statuses in which the user still has something pending (keepalive holds while any is true). */
export const PENDING_STATUSES: ActionPreviewStatus[] = ["AwaitingConfirmation", "AwaitingScope", "AwaitingApproval", "Signing"];

/** Receipt statuses: the action was handed to the network. */
export const RECEIPT_STATUSES: ActionPreviewStatus[] = ["Submitted", "Completed"];

/** The group hash the user is deciding on: the first step still waiting for them, else the first step. */
export function decisionHash(p: ActionPreview): string {
  const open: ActionPreviewStatus[] = ["AwaitingConfirmation", "AwaitingScope", "AwaitingApproval"];
  return (p.steps.find((s) => open.includes(s.status)) ?? p.steps[0])?.groupHash ?? "";
}

/** Same shape as the backend's ConfirmResponse; any other JSON body (409, 410, 404) carries `error` and `message`. */
export interface ConfirmResult {
  /** landed, submitted, consent_pending, scope_required, sign_in_required, sign_in_unavailable, failed, agent_paused, ... */
  status: string;
  message: string;
  txIds: string[];
  consent: ConsentStatus | null;
  reauthorizeScope: string | null;
  /** The raw body, for the agent refusal check. */
  raw: unknown;
}

export interface PreviewApi {
  /** POST /chat/previews/{id}/confirm { groupHash }. Rejects only on a network error. */
  confirm(previewId: string, groupHash: string): Promise<ConfirmResult>;
}

export const PREVIEW_PATHS = {
  confirm: (previewId: string) => `/chat/previews/${encodeURIComponent(previewId)}/confirm`,
} as const;

/** Calls the backend with the HttpOnly session cookie and the CSRF headers; it never sees a token. */
export class PreviewClient implements PreviewApi {
  private readonly f: typeof fetch;
  constructor(private readonly cfg: TransportConfig) {
    this.f = cfg.fetch ?? ((...a) => fetch(...a));
  }

  async confirm(previewId: string, groupHash: string): Promise<ConfirmResult> {
    const res = await this.f(this.cfg.backendUrl + PREVIEW_PATHS.confirm(previewId), {
      method: "POST",
      credentials: "include",
      headers: { "Content-Type": "application/json", [CSRF_HEADER]: "1", [CLIENT_KEY_HEADER]: this.cfg.clientKey },
      body: JSON.stringify({ groupHash }),
    });
    const j = (await res.json().catch(() => null)) as Record<string, unknown> | null;
    const str = (v: unknown) => (typeof v === "string" ? v : "");
    // A 401 without our body means the session is gone; other bodies without `status` carry an `error` code.
    const status = str(j?.status) || (res.status === 401 ? "sign_in_required" : str(j?.error) ? `error:${str(j?.error)}` : "failed");
    return {
      status,
      message: str(j?.message),
      txIds: Array.isArray(j?.txIds) ? (j.txIds as unknown[]).filter((x): x is string => typeof x === "string") : [],
      consent: j?.consent && typeof j.consent === "object" ? (j.consent as ConsentStatus) : null,
      reauthorizeScope: str(j?.reauthorizeScope) || null,
      raw: j,
    };
  }
}
