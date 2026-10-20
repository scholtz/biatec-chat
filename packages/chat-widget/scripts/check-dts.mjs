// The published declarations must be self-contained: every module they reference is relative or "vue" (the only
// peer dependency). Used by emit-types.mjs after it writes dist/, and by test/standalone.test.ts on fixtures.
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";

const ALLOWED = new Set(["vue"]);

// `… from "x"` (import / export … from), `import("x")`, a bare `import "x"`, `/// <reference types="x" />`.
const SPECIFIERS =
  /\bfrom\s+["']([^"']+)["']|\bimport\(\s*["']([^"']+)["']\s*\)|^\s*import\s+["']([^"']+)["']|^\s*\/\/\/\s*<reference\s+types\s*=\s*["']([^"']+)["']/gm;

/** Non-relative module specifiers other than "vue" in one declaration file's text. */
export function foreignSpecifiers(text) {
  const out = [];
  for (const m of text.matchAll(SPECIFIERS)) {
    const spec = m[1] ?? m[2] ?? m[3] ?? m[4];
    if (!spec.startsWith(".") && !ALLOWED.has(spec)) out.push(spec);
  }
  return out;
}

/** `file: specifier` for every foreign reference in the .d.ts files under `dir`. */
export function foreignImports(dir) {
  const bad = [];
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, e.name);
    if (e.isDirectory()) bad.push(...foreignImports(p));
    else if (p.endsWith(".d.ts")) for (const s of foreignSpecifiers(readFileSync(p, "utf8"))) bad.push(`${p}: ${s}`);
  }
  return bad;
}
