const nf = new Intl.NumberFormat("en");
const cf = new Intl.NumberFormat("en", { notation: "compact", maximumFractionDigits: 1 });

export function formatNumber(n: number | null | undefined): string {
  return n == null || !Number.isFinite(n) ? "-" : nf.format(n);
}

export function formatCompact(n: number | null | undefined): string {
  return n == null || !Number.isFinite(n) ? "-" : cf.format(n);
}

export function formatEur(n: number | null | undefined): string {
  if (n == null || !Number.isFinite(n)) return "-";
  return new Intl.NumberFormat("en", { style: "currency", currency: "EUR" }).format(n);
}

/** Local date and time, e.g. "23 Oct 2026, 09:05". "-" for missing or unparsable input. */
export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return "-";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "-";
  return d.toLocaleString("en-GB", { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" });
}

export function formatTime(iso: string | null | undefined): string {
  if (!iso) return "-";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "-";
  return d.toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

/** "5 Oct" from "2026-10-05" (a plain date, no timezone shifting). */
export function formatDay(date: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(date);
  if (!m) return date;
  const d = new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])));
  return d.toLocaleDateString("en-GB", { day: "numeric", month: "short", timeZone: "UTC" });
}

/** Remaining time as m:ss or h:mm:ss; never negative. */
export function formatCountdown(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const ss = String(s).padStart(2, "0");
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${ss}` : `${m}:${ss}`;
}

export function shortId(id: string, n = 8): string {
  return id.length <= n ? id : `${id.slice(0, n)}…`;
}

/** Truncates in the middle ("8f2a91…c0d4") so both ends of a subject stay visible; the full value goes in a title. */
export function shortMiddle(s: string, max = 16): string {
  if (s.length <= max) return s;
  const keep = Math.floor((max - 1) / 2);
  return `${s.slice(0, keep)}…${s.slice(s.length - keep)}`;
}

export function shortModel(modelId: string): string {
  return modelId.replace(/^eu\.anthropic\./, "").replace(/-v\d+:\d+$/, "");
}

export const REASON_MIN = 8;
export const REASON_MAX = 500;

/** Returns an error message, or null when the (trimmed) reason is acceptable. */
export function validateReason(reason: string): string | null {
  const len = reason.trim().length;
  if (len < REASON_MIN) return `Enter at least ${REASON_MIN} characters (${len} so far).`;
  if (len > REASON_MAX) return `At most ${REASON_MAX} characters (${len} entered).`;
  return null;
}

export function msUntil(iso: string, now: number): number {
  const t = new Date(iso).getTime();
  return Number.isNaN(t) ? 0 : t - now;
}

const GOOD = new Set(["completed", "confirmed", "signed", "submitted", "succeeded", "executed"]);
const BAD = new Set(["failed", "rejected", "cancelled", "canceled", "expired", "refused", "declined"]);
export type Tone = "good" | "bad" | "neutral";
export function statusTone(status: string): Tone {
  const s = status.toLowerCase();
  return GOOD.has(s) ? "good" : BAD.has(s) ? "bad" : "neutral";
}
export function statusGlyph(tone: Tone): string {
  return tone === "good" ? "✓" : tone === "bad" ? "✕" : "•";
}
