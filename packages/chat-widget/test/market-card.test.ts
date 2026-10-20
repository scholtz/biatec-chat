import { describe, it, expect } from "vitest";
import { mount } from "@vue/test-utils";
import type { DataCardEvent } from "@biatec/chat-protocol";
import { MarketCard, formatChange, formatDecimal, sparkPaths } from "../src/components/data";
import { isChatEvent } from "../src/transport/sse";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

function market(over: Partial<DataCardEvent> = {}): DataCardEvent {
  return {
    type: "data_card",
    kind: "market",
    network: "mainnet",
    addresses: [],
    assets: [],
    pair: "ALGO/USDC",
    price: "0.2150",
    change24hPct: "7.5",
    high24h: "0.26",
    low24h: "0.19",
    volume24h: "1000000000000005000.75",
    series: ["0.21", "0.22", "0.2", "0.215"],
    source: "Biatec DEX",
    asOf: "2026-10-20T11:00:00Z",
    ...over,
  };
}

describe("MarketCard", () => {
  it("renders the up fixture with arrow, sign and the word, plus kicker, stats and stamp", () => {
    const w = mount(MarketCard, { props: { card: market() } });
    expect(w.get("[data-test=market-kicker]").text()).toBe("Market snapshot · Biatec DEX");
    expect(w.get("[data-test=market-pair]").text()).toBe("ALGO/USDC");
    expect(w.get("[data-test=market-price]").text()).toBe("0.215");
    expect(w.get("[data-test=market-arrow]").text()).toBe("▲");
    expect(w.get("[data-test=market-pct]").text()).toBe("+7.50%");
    expect(w.get("[data-test=market-word]").text()).toBe("Up · 24 h");
    expect(w.get("[data-test=market-change]").attributes("data-direction")).toBe("up");
    expect(w.get("[data-test=market-high]").text()).toBe("0.26");
    expect(w.get("[data-test=market-low]").text()).toBe("0.19");
    // Digits above 2^53 survive: the volume is a string all the way.
    expect(w.get("[data-test=market-volume]").text()).toBe("1,000,000,000,000,005,000.75");
    expect(w.get("[data-test=market-stamp]").text()).toBe("Snapshot 11:00 UTC · not live");
    expect(w.findAll("dl > div")).toHaveLength(3);
  });

  it("renders the down fixture with arrow, minus sign and the word", () => {
    const w = mount(MarketCard, { props: { card: market({ change24hPct: "-2.31", price: "1234.5" }) } });
    expect(w.get("[data-test=market-arrow]").text()).toBe("▼");
    expect(w.get("[data-test=market-pct]").text()).toBe("-2.31%");
    expect(w.get("[data-test=market-word]").text()).toBe("Down · 24 h");
    expect(w.get("[data-test=market-change]").attributes("data-direction")).toBe("down");
    expect(w.get("[data-test=market-price]").text()).toBe("1,234.50");
  });

  it("renders no change as flat, with a word and no sign", () => {
    const w = mount(MarketCard, { props: { card: market({ change24hPct: "0" }) } });
    expect(w.get("[data-test=market-pct]").text()).toBe("0.00%");
    expect(w.get("[data-test=market-word]").text()).toBe("Unchanged · 24 h");
    expect(w.get("[data-test=market-change]").attributes("data-direction")).toBe("flat");
  });

  it("draws a 96 px sparkline in the text colour with a neutral area, never green or red", () => {
    const w = mount(MarketCard, { props: { card: market() } });
    const svg = w.get("[data-test=market-spark]");
    expect(svg.attributes("role")).toBe("img");
    expect(svg.findAll("path")).toHaveLength(2);
    const css = readFileSync(resolve(process.cwd(), "src/components/data/market.css"), "utf8");
    expect(css).toMatch(/\.bc-market-spark\s*\{[^}]*height:\s*96px/);
    expect(css).toMatch(/\.bc-market-spark-line\s*\{[^}]*stroke:\s*var\(--color-text-primary\)/);
    expect(css).toMatch(/\.bc-market-spark-area\s*\{[^}]*fill:\s*var\(--color-bg-interactive\)/);
    expect(css).toMatch(/\.bc-market-price\s*\{[^}]*font-size:\s*26px[^}]*font-weight:\s*600/);
  });

  it("omits the sparkline with fewer than two points", () => {
    const w = mount(MarketCard, { props: { card: market({ series: ["0.2"] }) } });
    expect(w.find("[data-test=market-spark]").exists()).toBe(false);
  });

  it("uses tabular numerals on every number", () => {
    const w = mount(MarketCard, { props: { card: market() } });
    for (const sel of ["market-price", "market-change", "market-high", "market-low", "market-volume", "market-stamp"])
      expect(w.get(`[data-test=${sel}]`).classes()).toContain("bc-num");
  });

  it("never animates the price: no animation or transition in the styles or the component", () => {
    const css = readFileSync(resolve(process.cwd(), "src/components/data/market.css"), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
    expect(css).not.toMatch(/animation|transition|@keyframes|transform/i);
    const sfc = readFileSync(resolve(process.cwd(), "src/components/data/MarketCard.vue"), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
    expect(sfc).not.toMatch(/<transition|animate|setInterval|setTimeout|requestAnimationFrame/i);
  });

  it("shows no timestamp line when asOf is not a date", () => {
    const w = mount(MarketCard, { props: { card: market({ asOf: "garbage" }) } });
    expect(w.find("[data-test=market-stamp]").exists()).toBe(false);
  });

  it("renders text from the event as text, not markup", () => {
    const w = mount(MarketCard, { props: { card: market({ pair: "<img src=x onerror=alert(1)>/ALGO" }) } });
    expect(w.find("img").exists()).toBe(false);
    expect(w.get("[data-test=market-pair]").text()).toContain("<img");
  });
});

describe("market helpers", () => {
  it("formats decimals without converting to a number", () => {
    expect(formatDecimal("12345678901234567890.1")).toBe("12,345,678,901,234,567,890.10");
    expect(formatDecimal("not a number")).toBe("-");
    expect(formatChange("-0.5")).toEqual({ text: "-0.50%", direction: "down" });
    expect(formatChange("12.345")).toEqual({ text: "+12.345%", direction: "up" });
    expect(formatChange("-0")).toEqual({ text: "0.00%", direction: "flat" });
  });

  it("builds sparkline paths only from plain decimals", () => {
    expect(sparkPaths(["1", "2"])?.line).toBe("M0.0 92.0 L300.0 4.0");
    expect(sparkPaths(["1", "x"])).toBeNull();
    expect(sparkPaths(["5", "5"])?.line).toBe("M0.0 48.0 L300.0 48.0");
  });
});

describe("market data_card validation", () => {
  it("accepts a well-formed market card and rejects numbers where strings are required", () => {
    expect(isChatEvent(market())).toBe(true);
    expect(isChatEvent({ ...market(), price: 0.215 })).toBe(false);
    expect(isChatEvent({ ...market(), series: [0.2, 0.3] })).toBe(false);
    expect(isChatEvent({ ...market(), change24hPct: undefined })).toBe(false);
    expect(isChatEvent({ ...market(), kind: "other" })).toBe(false);
  });
});
