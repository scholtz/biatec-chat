import { describe, expect, it } from "vitest";
import {
  formatCountdown, formatDateTime, formatDay, formatNumber, shortId, shortMiddle, shortModel, statusTone, validateReason,
} from "../src/format";

describe("format helpers", () => {
  it("formats countdowns, never negative", () => {
    expect(formatCountdown(0)).toBe("0:00");
    expect(formatCountdown(-5000)).toBe("0:00");
    expect(formatCountdown(65_000)).toBe("1:05");
    expect(formatCountdown(3_725_000)).toBe("1:02:05");
  });
  it("formats numbers and dates defensively", () => {
    expect(formatNumber(1234567)).toBe("1,234,567");
    expect(formatNumber(null)).toBe("-");
    expect(formatDateTime(null)).toBe("-");
    expect(formatDateTime("garbage")).toBe("-");
    expect(formatDay("2026-10-05")).toBe("5 Oct");
  });
  it("shortens ids and model names", () => {
    expect(shortId("abcdefghijkl")).toBe("abcdefgh…");
    expect(shortId("abc")).toBe("abc");
    expect(shortModel("eu.anthropic.claude-sonnet-4-5-20250929-v1:0")).toBe("claude-sonnet-4-5-20250929");
  });
  it("truncates subjects in the middle", () => {
    expect(shortMiddle("abc")).toBe("abc");
    const s = "a".repeat(10) + "b".repeat(30);
    const t = shortMiddle(s, 16);
    expect(t.startsWith("aaaaaaa")).toBe(true);
    expect(t.endsWith("bbbbbbb")).toBe(true);
    expect(t).toContain("…");
  });
  it("validates the impersonation reason on the trimmed length", () => {
    expect(validateReason("   short  ")).toMatch(/at least 8/);
    expect(validateReason("long enough")).toBeNull();
    expect(validateReason("x".repeat(500))).toBeNull();
    expect(validateReason("x".repeat(501))).toMatch(/at most 500/i);
  });
  it("classifies status tones", () => {
    expect(statusTone("Completed")).toBe("good");
    expect(statusTone("Cancelled")).toBe("bad");
    expect(statusTone("Pending")).toBe("neutral");
  });
});
