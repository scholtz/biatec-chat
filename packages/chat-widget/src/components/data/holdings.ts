import type { DataCardEvent } from "@biatec/chat-protocol";

/** A holdings card is the `kind: "holdings"` variant of the data_card event. */
export type HoldingsCardData = DataCardEvent;
type Asset = HoldingsCardData["assets"][number];

/** One asset is "concentrated" when its share is strictly above this many percent. */
export const CONCENTRATION_PERCENT = 40;

/** Fiat values are scaled to this many decimals so sums and shares are exact (BigInt, never floating point). */
const SCALE = 12;

/** "123.45" to 123450000000000n; null for anything that is not a plain non-negative decimal. */
export function parseScaled(value: string | null | undefined): bigint | null {
  if (typeof value !== "string") return null;
  const m = /^(\d+)(?:\.(\d+))?$/.exec(value.trim());
  if (!m) return null;
  const frac = (m[2] ?? "").slice(0, SCALE).padEnd(SCALE, "0");
  return BigInt(m[1]!) * 10n ** BigInt(SCALE) + BigInt(frac);
}

export interface HoldingsRow {
  assetId: string;
  unitName: string;
  /** Amount for display: grouped integer part, trailing zeros trimmed to at least two decimals. Every digit kept. */
  amount: string;
  /** Share in tenths of a percent (0..1000), present only when every asset has a fiat value. */
  tenths: number | null;
  /** Rank among the rows by value (0 is the largest), for the swatch tone. */
  rank: number;
}

export interface HoldingsModel {
  rows: HoldingsRow[];
  /** Total fiat value, e.g. "1,234.50 EUR"; null unless every asset has a fiat value and a currency is known. */
  total: string | null;
  /** True when shares exist (fiat values for all assets and a positive total). */
  hasShares: boolean;
  /** The largest asset's unit name when it is above the concentration threshold. */
  concentrated: { unitName: string; tenths: number } | null;
}

/** Groups thousands in a decimal string without converting to a number. */
export function formatAmount(amount: string): string {
  const m = /^(\d+)(?:\.(\d*))?$/.exec(amount);
  if (!m) return amount;
  const int = m[1]!.replace(/\B(?=(\d{3})+(?!\d))/g, ",");
  const frac = (m[2] ?? "").replace(/0+$/, "").padEnd(Math.min(2, (m[2] ?? "").length), "0");
  return frac ? `${int}.${frac}` : int;
}

function formatFiat(scaled: bigint, currency: string): string {
  const cents = (scaled + 5n * 10n ** BigInt(SCALE - 3)) / 10n ** BigInt(SCALE - 2); // round half up to 2 decimals
  const s = cents.toString().padStart(3, "0");
  return `${formatAmount(`${s.slice(0, -2)}.${s.slice(-2)}`)} ${currency}`;
}

/** Share of each value in tenths of a percent that sum to exactly 1000 (largest remainder). */
export function allocateTenths(values: bigint[]): number[] {
  const total = values.reduce((a, b) => a + b, 0n);
  if (total <= 0n) return values.map(() => 0);
  const floors = values.map((v) => (v * 1000n) / total);
  const rema = values.map((v, i) => ({ i, r: (v * 1000n) % total }));
  let left = 1000 - Number(floors.reduce((a, b) => a + b, 0n));
  rema.sort((a, b) => (a.r === b.r ? a.i - b.i : a.r > b.r ? -1 : 1));
  const out = floors.map(Number);
  for (const { i } of rema) {
    if (left-- <= 0) break;
    out[i]! += 1;
  }
  return out;
}

export function buildHoldings(card: HoldingsCardData): HoldingsModel {
  const values = card.assets.map((a: Asset) => parseScaled(a.fiatValue));
  const allFiat = card.assets.length > 0 && values.every((v): v is bigint => v !== null);
  const sum = allFiat ? (values as bigint[]).reduce((a, b) => a + b, 0n) : 0n;
  const hasShares = allFiat && sum > 0n;
  const tenths = hasShares ? allocateTenths(values as bigint[]) : null;

  // Rank by value (stable), so the largest asset gets the brand swatch.
  const order = card.assets.map((_, i) => i);
  if (allFiat) order.sort((a, b) => ((values[b] as bigint) === (values[a] as bigint) ? a - b : (values[b] as bigint) > (values[a] as bigint) ? 1 : -1));
  const rankOf = new Map(order.map((idx, rank) => [idx, rank]));

  const rows = card.assets.map((a: Asset, i: number) => ({
    assetId: a.assetId,
    unitName: a.unitName,
    amount: formatAmount(a.amount),
    tenths: tenths ? tenths[i]! : null,
    rank: rankOf.get(i) ?? i,
  }));

  let concentrated: HoldingsModel["concentrated"] = null;
  if (hasShares && card.assets.length > 1) {
    const top = order[0]!;
    // Exact: value * 100 > total * threshold.
    if ((values[top] as bigint) * 100n > sum * BigInt(CONCENTRATION_PERCENT))
      concentrated = { unitName: card.assets[top]!.unitName, tenths: tenths![top]! };
  }

  return {
    rows,
    total: hasShares && card.fiatCurrency ? formatFiat(sum, card.fiatCurrency) : null,
    hasShares,
    concentrated,
  };
}

/** 625 to "62.5". */
export const percentText = (tenths: number): string => `${Math.floor(tenths / 10)}.${tenths % 10}`;

/** "09:30" in UTC from an ISO timestamp; null when it is not a valid date. */
export function utcTime(iso: string): string | null {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  return `${String(d.getUTCHours()).padStart(2, "0")}:${String(d.getUTCMinutes()).padStart(2, "0")}`;
}
