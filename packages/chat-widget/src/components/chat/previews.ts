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
 * What the step the user is deciding on (the first one still to be approved, as decisionHash picks it) spends, in EUR, as a
 * decimal string, when that step states the asset it spends (assets[0]) with a fiat value in EUR. Otherwise null (no asset,
 * or a value in another currency, is not guessed at), and the backend then answers cautiously.
 */
export function previewValueEur(p: ActionPreview): string | null {
  // Confirm acts on one step at a time: the first one still to be approved. Signed, cancelled, expired and failed steps are not it.
  const step = p.steps.find((x) => OPEN_STEP_STATUSES.includes(x.status));
  // assets[0] is the asset the step spends (the convention of the card's downside line). A step that states nothing it spends
  // has an unknown value, not a zero one; so has one valued in another currency.
  const spent = step?.assets?.[0];
  if (!spent || spent.fiatCurrency !== "EUR" || !spent.fiatValue || !DECIMAL_AMOUNT.test(spent.fiatValue)) return null;
  // A value of zero for something that is spent is a price that could not be had, not a free action: unknown.
  if (/^0+(?:\.0+)?$/.test(spent.fiatValue)) return null;
  // Exact: at most 18 fractional digits, held as an integer of 10^-18, then rounded up to the cent so a small non-zero
  // amount is never sent as zero.
  const SCALE = 18;
  const [whole = "0", fraction = ""] = spent.fiatValue.split(".");
  const value = BigInt(whole) * 10n ** BigInt(SCALE) + BigInt(fraction.padEnd(SCALE, "0"));
  const cent = 10n ** BigInt(SCALE - 2);
  const cents = (value + cent - 1n) / cent;
  if (cents >= 10n ** 17n) return null; // rounding up must not leave the 15 whole digits the query accepts
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
