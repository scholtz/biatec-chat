// Writes one report file per run: report/run-<time>-<host>.json with the server URL, the commit of this repository and
// pass, fail and skip per case. Everything that could carry a token, a secret or a binding code is redacted, and the
// supplied secrets are never written. The folder is git-ignored; CONFORMANCE_REPORT_DIR overrides it.
import { execFileSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { Reporter } from "vitest/node";
import { redact, seeded, serverUrl } from "./needs.ts";

const here = dirname(fileURLToPath(import.meta.url));

const git = (...args: string[]) => {
  try { return execFileSync("git", args, { cwd: here, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim(); } catch { return "unknown"; }
};

/** The server URL without credentials or a query string, which may carry them. */
export const publicUrl = (raw: string) => {
  try { const u = new URL(raw); u.username = ""; u.password = ""; u.search = ""; u.hash = ""; return u.toString().replace(/\/+$/, ""); } catch { return "[unparseable url]"; }
};

export default class ConformanceReport implements Reporter {
  onTestRunEnd(testModules: ReadonlyArray<any>) {
    const cases: { name: string; file: string; status: "passed" | "failed" | "skipped"; reason?: string; error?: string }[] = [];
    for (const mod of testModules) {
      for (const t of mod.children.allTests() as Iterable<any>) {
        const r = t.result();
        const status = r.state === "passed" ? "passed" : r.state === "failed" ? "failed" : "skipped";
        const c: (typeof cases)[number] = { name: redact(t.fullName), file: String(mod.relativeModuleId ?? mod.moduleId).split(/[\/]/).pop()!, status };
        if (status === "skipped") c.reason = redact(r.note ?? "skipped without a reason");
        if (status === "failed") c.error = redact(String(r.errors?.[0]?.message ?? "failed").slice(0, 1500));
        cases.push(c);
      }
    }
    const count = (s: string) => cases.filter((c) => c.status === s).length;
    const url = serverUrl();
    const dirty = git("status", "--porcelain").length > 0;
    const report = {
      generatedAt: new Date().toISOString(),
      server: url ? publicUrl(url) : "in-process mock",
      mockHelpers: seeded(),
      commit: git("rev-parse", "HEAD") + (dirty ? " (uncommitted changes)" : ""),
      totals: { passed: count("passed"), failed: count("failed"), skipped: count("skipped") },
      note: "Skipped cases did not run and prove nothing. Tokens, secrets, codes and request ids are redacted.",
      cases,
    };
    const dir = process.env.CONFORMANCE_REPORT_DIR || join(here, "..", "report");
    mkdirSync(dir, { recursive: true });
    const host = url ? new URL(url).host.replace(/[^A-Za-z0-9.-]/g, "_") : "mock";
    const file = join(dir, `run-${report.generatedAt.replace(/[:.]/g, "-")}-${host}.json`);
    writeFileSync(file, JSON.stringify(report, null, 2) + "\n");
    console.log(`\nconformance report: ${file}  (${report.totals.passed} passed, ${report.totals.failed} failed, ${report.totals.skipped} skipped)`);
    for (const c of cases.filter((x) => x.status === "skipped")) console.log(`  SKIPPED ${c.name}: ${c.reason}`);
  }
}
