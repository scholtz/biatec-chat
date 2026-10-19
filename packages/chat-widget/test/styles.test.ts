import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const read = (p: string) =>
  readFileSync(resolve(process.cwd(), p), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");

function selectors(css: string): string[] {
  return [...css.matchAll(/(?:^|[};])\s*([^{}@;]+)\{/g)]
    .map((m) => m[1]!.trim())
    .filter((s) => !/^(from|to|\d+%)$/.test(s));
}

describe("styles", () => {
  for (const file of ["src/styles.css", "src/styles/tokens.css", "src/styles/ui.css"]) {
    it(`${file}: every selector is scoped to the widget root or :host`, () => {
      const list = selectors(read(file));
      expect(list.length).toBeGreaterThan(1);
      for (const group of list) for (const s of group.split(",")) expect(s.trim()).toMatch(/^(\.biatec-chat|:host)/);
    });
  }

  it("tokens: Biatec AI names, no font import, no old --bc-* colour tokens", () => {
    const css = read("src/styles/tokens.css");
    for (const name of ["--color-bg-primary", "--color-brand-primary", "--color-brand-on", "--space-4", "--radius-md", "--motion-fast", "--bc-z: 1000"])
      expect(css).toContain(name);
    expect(css).not.toMatch(/@import|fonts\.googleapis/);
    for (const f of ["src/styles.css", "src/styles/tokens.css", "src/styles/ui.css"]) expect(read(f)).not.toMatch(/--bc-color|--bc-radius|--bc-shadow|--bc-font/);
  });

  it("tokens: dark base on :host with a light override", () => {
    const css = read("src/styles/tokens.css");
    expect(css).toMatch(/:host,[\s\S]*?color-scheme: dark/);
    expect(css).toMatch(/:host\(\[data-theme="light"\]\)/);
  });

  it("components use tokens: no hex colour and no px value a token carries in ui.css", () => {
    const css = read("src/styles/ui.css");
    expect(css).not.toMatch(/#[0-9a-fA-F]{3,8}\b/);
    const tokenPx = new Set(["4", "8", "12", "16", "20", "24", "32", "40", "44", "48", "64", "6"]);
    for (const line of css.split("\n")) {
      if (/font-size|min\(/.test(line)) continue; // type sizes are not tokens yet; min() holds a dialog width cap
      for (const m of line.matchAll(/(?<![\d.])(\d+)px/g)) expect(tokenPx.has(m[1]!), `literal ${m[0]} in: ${line.trim()}`).toBe(false);
    }
  });

  it("legacy styles.css has no hex colour either", () => {
    expect(read("src/styles.css")).not.toMatch(/#[0-9a-fA-F]{3,8}\b/);
  });
});
