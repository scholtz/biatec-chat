import { describe, it, expect } from "vitest";
import { mount } from "@vue/test-utils";
import type { ChatEvent, DataCardEvent } from "@biatec/chat-protocol";
import { HoldingsCard, allocateTenths, buildHoldings, parseScaled } from "../src/components/data";
import { isChatEvent } from "../src/transport/sse";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

function card(over: Partial<DataCardEvent> = {}, fiat: (string | undefined)[] = ["600.00", "300.00", "100.00"]): DataCardEvent {
  const names = [
    ["0", "ALGO", "1,500"],
    ["31566704", "USDC", "300"],
    ["99", "GOLD", "0.5"],
  ] as const;
  return {
    type: "data_card",
    kind: "holdings",
    network: "testnet",
    addresses: ["AAAA"],
    assets: names.map(([assetId, unitName, amount], i) => ({
      assetId,
      unitName,
      amount: amount.replace(",", ""),
      decimals: 6,
      ...(fiat[i] !== undefined ? { fiatValue: fiat[i] } : {}),
    })),
    fiatCurrency: "EUR",
    asOf: "2026-10-20T09:05:00Z",
    ...over,
  };
}

describe("HoldingsCard", () => {
  it("renders total, bar, one row per asset, shares that sum to 100 percent and the as-of line", () => {
    const w = mount(HoldingsCard, { props: { card: card() } });
    expect(w.get("[data-test=holdings-total]").text()).toContain("1,000.00 EUR");
    expect(w.findAll("[data-test=holdings-seg]")).toHaveLength(3);
    const rows = w.findAll("[data-test=holdings-row]");
    expect(rows).toHaveLength(3);
    expect(rows.map((r) => r.get("[data-test=holdings-unit]").text())).toEqual(["ALGO", "USDC", "GOLD"]);
    const shares = rows.map((r) => r.get("[data-test=holdings-share]").text());
    expect(shares).toEqual(["60.0%", "30.0%", "10.0%"]);
    expect(rows.map((r) => parseFloat(r.get("[data-test=holdings-share]").text())).reduce((a, b) => a + b, 0)).toBeCloseTo(100, 5);
    expect(w.get("[data-test=holdings-asof]").text()).toBe("Balances as of 09:05 UTC · Testnet");
  });

  it("uses tabular numerals on every amount, share and the timestamp", () => {
    const w = mount(HoldingsCard, { props: { card: card() } });
    for (const sel of ["holdings-amount", "holdings-share", "holdings-total", "holdings-asof"])
      for (const el of w.findAll(`[data-test=${sel}]`)) expect(el.classes()).toContain("bc-num");
    const css = readFileSync(resolve(process.cwd(), "src/styles/cards.css"), "utf8");
    expect(css).toMatch(/\.bc-num\s*\{[^}]*tabular-nums/);
  });

  it("makes the largest asset the brand swatch and never draws a red or green gain/loss scheme", () => {
    const w = mount(HoldingsCard, { props: { card: card() } });
    expect(w.findAll("[data-test=holdings-seg]").map((s) => s.attributes("data-rank"))).toEqual(["0", "1", "2"]);
    expect(w.findAll(".bc-holdings-swatch").map((s) => s.attributes("data-rank"))).toEqual(["0", "1", "2"]);
    const css = readFileSync(resolve(process.cwd(), "src/components/data/data.css"), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
    expect(css).not.toMatch(/red|danger|success|#[0-9a-f]{3,8}\b/i);
  });

  it("shows the concentration note only when one asset is strictly above 40 percent", () => {
    // Exactly 40 percent: no note.
    const at = mount(HoldingsCard, { props: { card: card({}, ["400", "350", "250"]) } });
    expect(at.find("[data-test=holdings-concentration]").exists()).toBe(false);
    // Just above.
    const above = mount(HoldingsCard, { props: { card: card({}, ["401", "349", "250"]) } });
    expect(above.get("[data-test=holdings-concentration]").text()).toContain("ALGO is 40.1%");
    // Clear dominance.
    const dom = mount(HoldingsCard, { props: { card: card() } });
    expect(dom.get("[data-test=holdings-concentration]").text()).toContain("60.0%");
  });

  it("shows amounts only, with no total, bar, shares or concentration note, without fiat values", () => {
    const w = mount(HoldingsCard, { props: { card: card({ fiatCurrency: undefined }, [undefined, undefined, undefined]) } });
    expect(w.find("[data-test=holdings-total]").exists()).toBe(false);
    expect(w.find("[data-test=holdings-bar]").exists()).toBe(false);
    expect(w.find(".bc-holdings-swatch").exists()).toBe(false);
    expect(w.find("[data-test=holdings-share]").exists()).toBe(false);
    expect(w.find("[data-test=holdings-concentration]").exists()).toBe(false);
    expect(w.findAll("[data-test=holdings-amount]").map((a) => a.text())).toEqual(["1,500", "300", "0.5"]);
  });

  it("treats partial fiat coverage as no fiat at all, so shares are never computed from a subset", () => {
    const m = buildHoldings(card({}, ["600", undefined, "100"]));
    expect(m.hasShares).toBe(false);
    expect(m.total).toBeNull();
    expect(m.rows.every((r) => r.tenths === null)).toBe(true);
  });

  it("keeps amounts above 2^53 as exact strings", () => {
    const c = card({ assets: [{ assetId: "0", unitName: "ALGO", amount: "9007199254740993.123456", decimals: 6 }] });
    const w = mount(HoldingsCard, { props: { card: c } });
    expect(w.get("[data-test=holdings-amount]").text()).toBe("9,007,199,254,740,993.123456");
  });

  it("renders unit names as text, never HTML", () => {
    const c = card({ assets: [{ assetId: "7", unitName: "<img src=x onerror=alert(1)>", amount: "1", decimals: 0 }] });
    const w = mount(HoldingsCard, { props: { card: c } });
    expect(w.find("img").exists()).toBe(false);
    expect(w.get("[data-test=holdings-unit]").text()).toBe("<img src=x onerror=alert(1)>");
  });
});

describe("holdings math", () => {
  it("allocateTenths always sums to exactly 1000", () => {
    for (const v of [[1n, 1n, 1n], [333n, 333n, 334n], [1n, 999999n], [5n]]) expect(allocateTenths(v).reduce((a, b) => a + b, 0)).toBe(1000);
  });

  it("parseScaled accepts plain decimals only", () => {
    expect(parseScaled("1.5")).toBe(1_500_000_000_000n);
    expect(parseScaled("-1")).toBeNull();
    expect(parseScaled("1e3")).toBeNull();
    expect(parseScaled(undefined)).toBeNull();
  });
});

describe("data_card event validation", () => {
  it("accepts a well-formed holdings card and drops malformed ones", () => {
    expect(isChatEvent(card())).toBe(true);
    const asNumber = JSON.parse(JSON.stringify(card()));
    asNumber.assets[0].amount = 12345; // a number would lose digits above 2^53
    expect(isChatEvent(asNumber)).toBe(false);
    expect(isChatEvent({ ...card(), kind: "market" })).toBe(false);
    expect(isChatEvent({ ...card(), assets: "nope" })).toBe(false);
  });

  it("is part of the ChatEvent union", () => {
    const e: ChatEvent = card();
    expect(e.type).toBe("data_card");
  });
});
