import type { ActionPreview, ActionPreviewStatus, ActionStep, ConsentState, ConsentStatus } from "@biatec/chat-protocol";
import type { StatusKey } from "../../ui";

/** Confirm is disabled when less than this many seconds of validity remain (spec: Action preview). */
export const CONFIRM_MIN_SECONDS = 600;
/** The widget warns once the session has less than this left (20 minutes). */
export const SESSION_WARN_SECONDS = 20 * 60;

const PREVIEW_STATUS: Record<ActionPreviewStatus, StatusKey> = {
  AwaitingConfirmation: "awaitingConfirmation",
  AwaitingScope: "awaitingScope",
  AwaitingApproval: "awaitingApproval",
  Signing: "signing",
  Submitted: "submitted",
  Completed: "completed",
  Cancelled: "cancelled",
  Expired: "expired",
  Failed: "failed",
};

/** docs/design/README.md "Preview status mapping": a consent result takes over the card's status while it applies. */
export function statusKey(status: ActionPreviewStatus, consent?: ConsentState | null): StatusKey {
  if (consent === "Denied") return "rejected";
  if (consent === "TimedOut") return "timedOut";
  if (consent === "Pending" && (status === "AwaitingApproval" || status === "AwaitingConfirmation")) return "awaitingApproval";
  return PREVIEW_STATUS[status];
}

export function consentFor(preview: ActionPreview, consent?: ConsentStatus | null): ConsentStatus | null {
  return consent && consent.previewId === preview.previewId ? consent : null;
}

/** Seconds left of the smaller of the group's validity and the session's absolute lifetime (never negative). */
export function remainingSeconds(expiresAt: string, sessionExpiresAt: string | null | undefined, nowMs: number): number {
  const own = Date.parse(expiresAt);
  if (Number.isNaN(own)) return 0; // fail closed: unknown validity never enables Confirm
  let end = own;
  if (sessionExpiresAt) {
    const session = Date.parse(sessionExpiresAt);
    if (!Number.isNaN(session)) end = Math.min(end, session);
  }
  return Math.max(0, Math.floor((end - nowMs) / 1000));
}

export function sessionSecondsLeft(sessionExpiresAt: string | null | undefined, nowMs: number): number | null {
  if (!sessionExpiresAt) return null;
  const end = Date.parse(sessionExpiresAt);
  return Number.isNaN(end) ? null : Math.max(0, Math.floor((end - nowMs) / 1000));
}

export function formatClock(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const pad = (n: number) => String(n).padStart(2, "0");
  return h > 0 ? `${h}:${pad(m)}:${pad(s % 60)}` : `${pad(m)}:${pad(s % 60)}`;
}

export function networkLabel(p: Pick<ActionPreview, "network" | "genesisId">): "testnet" | "mainnet" {
  return /test|sandnet|betanet|regtest|signet/i.test(`${p.network} ${p.genesisId}`) ? "testnet" : "mainnet";
}

/** Only web links are rendered as links: never javascript: or data: URLs. */
export function safeUrl(url: string | null | undefined): string | null {
  return url && /^https?:\/\//i.test(url) ? url : null;
}

export function amountText(a: { amount: string; symbol: string }): string {
  return `${a.amount} ${a.symbol}`;
}

/** First asset of the first step that has one, for the Confirm label. */
export function primaryAmount(p: ActionPreview): string | null {
  const a = p.steps.find((s) => s.assets && s.assets.length > 0)?.assets?.[0];
  return a ? amountText(a) : null;
}

/** "Swap 100 USDC for ALGO" gives "swap". */
export function actionWord(p: ActionPreview): string {
  return (p.intent.trim().split(/\s+/)[0] ?? "").toLowerCase();
}

/**
 * What a 10 percent fall in the value of the assets being spent would cost.
 * Convention: assets[0] of a step is the asset it spends. Returns null unless every step that lists assets has a
 * parsable fiat value on that asset, all in one currency (a partial total would understate the risk).
 */
export function downside(p: ActionPreview): { amount: string; currency: string } | null {
  let total = 0;
  let currency: string | null = null;
  for (const step of p.steps) {
    const first = step.assets?.[0];
    if (!first) continue;
    const v = first.fiatValue ? Number.parseFloat(first.fiatValue) : Number.NaN;
    if (Number.isNaN(v) || !first.fiatCurrency) return null;
    if (currency && first.fiatCurrency !== currency) return null;
    currency = first.fiatCurrency;
    total += v;
  }
  return currency && total > 0 ? { amount: (total * 0.1).toFixed(2), currency } : null;
}

export function stepStatusKey(step: ActionStep): StatusKey {
  return PREVIEW_STATUS[step.status];
}
