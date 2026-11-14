// Writes one report file per run: report/run-<time>-<host>.json with the server URL, the commit of this repository and
// pass, fail and skip per case. Everything that could carry a token, a secret or a binding code is redacted, and the
// supplied secrets are never written. The folder is git-ignored; CONFORMANCE_REPORT_DIR overrides it.
import { execFileSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { Reporter } from "vitest/node";
import { offersHelpers, redact, serverUrl } from "./needs.ts";

const here = dirname(fileURLToPath(import.meta.url));

/** Everything the run depends on (this package, the mock, the schemas, the scripts and the vectors); unrelated dirty files elsewhere do not mark the report. `here` is src/, so the paths climb. */
const dependsOn = ["..", "../../mock-oidc", "../../schemas", "../../scripts", "../../vectors", "../../spec"];
const dirtyPaths = () => {
  const out = git("status", "--porcelain", "--", ...dependsOn);
  return out === "unknown" || !out ? [] : out.split(/\r?\n/).map((l) => l.replace(/^\s*\S+\s+/, "").trim()).filter((p) => p && !/(^|\/)report\//.test(p));
};

const git = (...args: string[]) => {
  try { return execFileSync("git", args, { cwd: here, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim(); } catch { return "unknown"; }
};

/** The server URL without credentials or a query string, which may carry them. */
export const publicUrl = (raw: string) => {
  try { const u = new URL(raw); u.username = ""; u.password = ""; u.search = ""; u.hash = ""; return u.toString().replace(/\/+$/, ""); } catch { return "[unparseable url]"; }
};

export default class ConformanceReport implements Reporter {
  onTestRunEnd(testModules: ReadonlyArray<any>, unhandledErrors: ReadonlyArray<unknown> = [], reason: string = "passed") {
    const cases: { name: string; file: string; status: "passed" | "failed" | "skipped" | "not_run"; reason?: string; error?: string }[] = [];
    for (const mod of testModules) {
      for (const t of mod.children.allTests() as Iterable<any>) {
        const r = t.result();
        // "skipped" is a case that declared it could not run here; "not_run" is one that never ran (aborted or cancelled run).
        const status = r.state === "passed" ? "passed" : r.state === "failed" ? "failed" : r.state === "skipped" ? "skipped" : "not_run";
        const c: (typeof cases)[number] = { name: redact(t.fullName), file: String(mod.relativeModuleId ?? mod.moduleId).split(/[\/]/).pop()!, status };
        if (status === "skipped") c.reason = redact(r.note ?? "skipped without a reason");
        if (status === "not_run") c.reason = "the run ended before this case started";
        if (status === "failed") c.error = redact(String(r.errors?.[0]?.message ?? "failed").slice(0, 1500));
        cases.push(c);
      }
    }
    const count = (s: string) => cases.filter((c) => c.status === s).length;
    const url = serverUrl();
    const dirty = dirtyPaths();
    const modulesFailed = testModules.some((m: any) => (m.errors?.() ?? []).length > 0); // a setup or collection error in a file
    const runState = reason === "interrupted" ? "cancelled" : unhandledErrors.length || modulesFailed || cases.some((c) => c.status === "not_run") ? "aborted" : "completed";
    const report = {
      generatedAt: new Date().toISOString(),
      server: url ? publicUrl(url) : "in-process mock",
      mockHelpers: offersHelpers(),
      commit: git("rev-parse", "HEAD"),
      dirtyPaths: dirty,
      runState,
      totals: { passed: count("passed"), failed: count("failed"), skipped: count("skipped"), not_run: count("not_run") },
      note: "Skipped and not_run cases did not run and prove nothing. Tokens, secrets, codes and request ids are redacted.",
      cases,
    };
    const dir = process.env.CONFORMANCE_REPORT_DIR || join(here, "..", "report");
    mkdirSync(dir, { recursive: true });
    let host = "mock";
    if (url) { try { host = new URL(url).host.replace(/[^A-Za-z0-9.-]/g, "_") || "server"; } catch { host = "server"; } }
    const file = join(dir, `run-${report.generatedAt.replace(/[:.]/g, "-")}-${host}.json`);
    writeFileSync(file, JSON.stringify(report, null, 2) + "\n");
    console.log(`\nconformance report: ${file}  (${runState}: ${report.totals.passed} passed, ${report.totals.failed} failed, ${report.totals.skipped} skipped, ${report.totals.not_run} not run)`);
    for (const c of cases.filter((x) => x.status === "skipped")) console.log(`  SKIPPED ${c.name}: ${c.reason}`);
  }
}
