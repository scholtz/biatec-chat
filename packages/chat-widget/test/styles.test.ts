import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

describe("styles", () => {
  it("scopes every selector under .biatec-chat", () => {
    const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
    const selectors = [...css.matchAll(/(?:^|})\s*([^{}@]+)\{/g)].map((m) => m[1]!.trim());
    expect(selectors.length).toBeGreaterThan(5);
    for (const group of selectors) for (const s of group.split(",")) expect(s.trim()).toMatch(/^\.biatec-chat/);
  });
});
