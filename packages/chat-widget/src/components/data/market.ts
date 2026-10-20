import type { DataCardEvent } from "@biatec/chat-protocol";
import { formatAmount } from "./holdings";

/** A market card is the `kind: "market"` variant of the data_card event (T-069). */
export type MarketCardData = DataCardEvent;

export type Direction = "up" | "down" | "flat";

export interface MarketModel {
  pair: string;
  /** Price for display, every digit kept: grouped integer part, at least two decimals. */
  price: string;
  direction: Direction;
  /** "+7.50%", "-2.31%" or "0.00%": the sign is always part of the text. */
  change: string;
  high: string;
  low: string;
  volume: string;
  /** Source name for the kicker. */
  source: string;
  /** SVG path data for the sparkline line and its closed area; null with fewer than two points. */
  line: string | null;
  area: string | null;
}

const PLAIN = /^-?\d+(?:\.\d+)?$/;

/** A decimal string for display without converting to a number: grouped thousands, at least two decimals. */
export function formatDecimal(value: string | null | undefined): string {
  if (typeof value !== "string" || !PLAIN.test(value)) return "-";
  const neg = value.startsWith("-");
  const body = neg ? value.slice(1) : value;
  const [int, frac = ""] = body.split(".");
  const shown = formatAmount(`${int}.${frac.padEnd(2, "0")}`);
  return neg ? `-${shown}` : shown;
}

/** "7.5" to up "+7.50%", "-2.314" to down "-2.314%", "0" to flat "0.00%". Never rounds a digit away. */
export function formatChange(value: string | null | undefined): { text: string; direction: Direction } {
  if (typeof value !== "string" || !PLAIN.test(value)) return { text: "-", direction: "flat" };
  const neg = value.startsWith("-");
  const abs = formatDecimal(neg ? value.slice(1) : value);
  const zero = /^[0.]*$/.test(neg ? value.slice(1) : value);
  if (zero) return { text: "0.00%", direction: "flat" };
  return neg ? { text: `-${abs}%`, direction: "down" } : { text: `+${abs}%`, direction: "up" };
}

export const SPARK_W = 300;
export const SPARK_H = 96;
const PAD = 4;

/** Sparkline geometry from decimal strings. Numbers are used only to place points, never shown. */
export function sparkPaths(series: readonly string[] | null | undefined): { line: string; area: string } | null {
  const pts = (series ?? []).filter((s) => PLAIN.test(s)).map(Number);
  if (pts.length < 2) return null;
  const min = Math.min(...pts);
  const max = Math.max(...pts);
  const span = max - min || 1;
  const x = (i: number) => (i / (pts.length - 1)) * SPARK_W;
  const y = (v: number) => PAD + (1 - (v - min) / span) * (SPARK_H - 2 * PAD);
  const coords = pts.map((v, i) => `${x(i).toFixed(1)} ${(max === min ? SPARK_H / 2 : y(v)).toFixed(1)}`);
  const line = `M${coords.join(" L")}`;
  return { line, area: `${line} L${SPARK_W} ${SPARK_H} L0 ${SPARK_H} Z` };
}

export function buildMarket(card: MarketCardData): MarketModel {
  const change = formatChange(card.change24hPct);
  const spark = sparkPaths(card.series);
  return {
    pair: card.pair ?? "",
    price: formatDecimal(card.price),
    direction: change.direction,
    change: change.text,
    high: formatDecimal(card.high24h),
    low: formatDecimal(card.low24h),
    volume: formatDecimal(card.volume24h),
    source: card.source ?? "Biatec DEX",
    line: spark?.line ?? null,
    area: spark?.area ?? null,
  };
}
