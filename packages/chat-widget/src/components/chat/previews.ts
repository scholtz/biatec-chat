import type { ActionPreview, ActionPreviewStatus, ConsentStatus } from "@biatec/chat-protocol";
import { sessionCallHeaders, type TransportConfig } from "../../transport/types";
import { isConsentStatus } from "../../transport/sse";
import { DECIMAL_AMOUNT, str } from "../../transport/guards";

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
  /** The backend's one-line prediction about phone approval (GET .../consent/prediction), shown before Confirm. */
  prediction?: string | null;
}

/** The statuses in which the user still has something pending (keepalive holds while any is true). */
export const PENDING_STATUSES: ActionPreviewStatus[] = ["AwaitingConfirmation", "AwaitingScope", "AwaitingApproval", "Signing"];

/** Receipt statuses: the action was handed to the network. */
export const RECEIPT_STATUSES: ActionPreviewStatus[] = ["Submitted", "Completed"];

/** The step statuses in which the user still has to act (confirm, authorize, approve on the phone). */
export const OPEN_STEP_STATUSES: ActionPreviewStatus[] = ["AwaitingConfirmation", "AwaitingScope", "AwaitingApproval"];

/** The group hash the user is deciding on: the first step still waiting for them, else the first step. */
export function decisionHash(p: ActionPreview): string {
  return (p.steps.find((s) => OPEN_STEP_STATUSES.includes(s.status)) ?? p.steps[0])?.groupHash ?? "";
}

/**
 * What the action spends, in EUR, as a decimal string, when every step states the asset it spends (assets[0]) with a fiat
 * value in EUR. Otherwise null (a step with no asset, or a value in another currency, is not guessed at), and the backend
 * then answers cautiously.
 */
export function previewValueEur(p: ActionPreview): string | null {
  // Exact: every value is a decimal string with at most 18 fractional digits, summed as integers of 10^-18.
  const SCALE = 18;
  let total = 0n;
  let any = false;
  for (const s of p.steps) {
    // Only the steps still to be approved count: signed ones are done, cancelled, expired and failed ones will not be signed.
    if (!OPEN_STEP_STATUSES.includes(s.status)) continue;
    const a = s.assets?.[0];
    // A step that states nothing it spends has an unknown value, not a zero one.
    if (!a) return null;
    if (a.fiatCurrency !== "EUR" || !a.fiatValue || !DECIMAL_AMOUNT.test(a.fiatValue)) return null;
    const [whole = "0", fraction = ""] = a.fiatValue.split(".");
    total += BigInt(whole) * 10n ** BigInt(SCALE) + BigInt(fraction.padEnd(SCALE, "0"));
    any = true;
  }
  if (!any) return null;
  // Rounded up to the cent, so a small non-zero amount is never sent as zero.
  const cent = 10n ** BigInt(SCALE - 2);
  const cents = (total + cent - 1n) / cent;
  return `${cents / 100n}.${String(cents % 100n).padStart(2, "0")}`;
}

/** Same shape as the backend's ConfirmResponse; any other JSON body (409, 410, 404) carries `error` and `message`. */
export interface ConfirmResult {
  /** landed, submitted, consent_pending, scope_required, sign_in_required, sign_in_unavailable, failed, agent_paused, ... */
  status: string;
  message: string;
  txIds: string[];
  consent: ConsentStatus | null;
  reauthorizeScope: string | null;
  /** A machine-readable reason (no_active_device, code_failed, ...) when the backend sends one; the message is the fallback. */
  errorCode?: string | null;
  /** The raw body, for the agent refusal check. */
  raw: unknown;
}

/**
 * The widget's view of a ConfirmResponse body, from the confirm call and from the consent stream's `confirm_result`.
 * A 401 without our body means the session is gone; other bodies without `status` carry an `error` code.
 */
export function toConfirmResult(j: Record<string, unknown> | null, httpStatus: number): ConfirmResult {
  const status = str(j?.status) || (httpStatus === 401 ? "sign_in_required" : str(j?.error) ? `error:${str(j?.error)}` : "failed");
  return {
    status,
    message: str(j?.message),
    txIds: Array.isArray(j?.txIds) ? (j.txIds as unknown[]).filter((x): x is string => typeof x === "string") : [],
    consent: isConsentStatus(j?.consent) ? (j!.consent as ConsentStatus) : null,
    reauthorizeScope: str(j?.reauthorizeScope) || null,
    errorCode: str(j?.errorCode) || null,
    raw: j,
  };
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
      headers: { "Content-Type": "application/json", ...sessionCallHeaders(this.cfg) },
      body: JSON.stringify({ groupHash }),
    });
    const j = (await res.json().catch(() => null)) as Record<string, unknown> | null;
    return toConfirmResult(j, res.status);
  }
}
