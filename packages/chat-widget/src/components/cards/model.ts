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
  const ends = [Date.parse(expiresAt)];
  if (sessionExpiresAt) ends.push(Date.parse(sessionExpiresAt));
  const end = Math.min(...ends.filter((n) => !Number.isNaN(n)));
  return Number.isFinite(end) ? Math.max(0, Math.floor((end - nowMs) / 1000)) : 0;
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

/** What a 10 percent fall in the value of the assets being spent would cost, when every fiat value is known. */
export function downside(p: ActionPreview): { amount: string; currency: string } | null {
  let total = 0;
  let currency: string | null = null;
  for (const step of p.steps) {
    const first = step.assets?.[0];
    if (!first?.fiatValue) continue;
    const v = Number.parseFloat(first.fiatValue);
    if (Number.isNaN(v)) continue;
    if (currency && first.fiatCurrency && first.fiatCurrency !== currency) return null;
    currency = first.fiatCurrency ?? currency;
    total += v;
  }
  return currency && total > 0 ? { amount: (total * 0.1).toFixed(2), currency } : null;
}

export function stepStatusKey(step: ActionStep): StatusKey {
  return PREVIEW_STATUS[step.status];
}
