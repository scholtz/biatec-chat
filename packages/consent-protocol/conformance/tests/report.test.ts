// The kit against a server that is not the mock and lacks its helpers: the run completes, every case that needs a helper (or an
// input nobody supplied) is listed as skipped with its reason, and no token, secret or code reaches the report.
// A tiny stub plays the server. It answers 404 on /mock/*, and every other call with a 401 that echoes the caller's credentials and
// carries a binding code, an auth request id and an access token, so that any failure message built from the answer would leak them.
import { describe, it, expect, beforeAll, afterAll } from "vitest";
import { spawn } from "node:child_process";
import { createServer, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import { mkdtempSync, readdirSync, readFileSync, rmSync } from "node:fs";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { missing, redact } from "../src/needs.ts";
import { publicUrl } from "../src/report.ts";

const here = dirname(fileURLToPath(import.meta.url));
const markers = {
  clientSecret: "MARK-client-secret-7f3a9c1e",
  userToken: "MARK-user-token-b81d44e0",
  deviceToken: "MARK-device-token-52c9aa17",
  bearerNoDevice: "MARK-bearer-nodevice-0e6f31d2",
  jwkD: "MARK-jwk-private-d-93ab07cc",
  bindingCode: "424242",
  authReqId: "ciba_MARKauthreq91",
  accessToken: "MARK-access-token-e5d27b09",
};

describe("the kit against a server without the mock's helpers", () => {
  let stub: Server;
  let url: string;
  let dir: string;
  let run: { error?: Error; status: number | null };
  let reportText: string;
  let report: any;

  beforeAll(async () => {
    stub = createServer((req, res) => {
      req.resume();
      if (req.url?.startsWith("/mock/")) { res.writeHead(404).end(); return; }
      res.writeHead(401, { "Content-Type": "application/json", "WWW-Authenticate": 'Bearer error="insufficient_user_authentication"' });
      res.end(JSON.stringify({
        error: `rejected ${req.headers.authorization ?? ""}`,
        error_description: `binding_code ${markers.bindingCode}`,
        binding_code: markers.bindingCode,
        auth_req_id: markers.authReqId,
        access_token: markers.accessToken,
      }));
    });
    await new Promise<void>((r) => stub.listen(0, "127.0.0.1", r));
    url = `http://127.0.0.1:${(stub.address() as AddressInfo).port}`;
    dir = mkdtempSync(join(tmpdir(), "conformance-report-"));
    const vitest = join(dirname(createRequire(import.meta.url).resolve("vitest/package.json")), "vitest.mjs");
    // Asynchronous: the stub lives in this process and must keep answering while the nested run talks to it.
    run = await new Promise((resolve) => {
    const child = spawn(process.execPath, [vitest, "run", "--config", join(here, "..", "vitest.config.ts")], {
      cwd: join(here, ".."),
      stdio: "ignore",
      env: {
        ...process.env,
        CONFORMANCE_NESTED: "1",
        CONFORMANCE_OIDC_URL: url,
        CONFORMANCE_REPORT_DIR: dir,
        CONFORMANCE_MOCK_HELPERS: "",
        CONFORMANCE_CLIENT_SECRET: markers.clientSecret,
        CONFORMANCE_USER_TOKEN: markers.userToken,
        CONFORMANCE_DEVICE_TOKEN: markers.deviceToken,
        CONFORMANCE_BEARER_NO_DEVICE: markers.bearerNoDevice,
        CONFORMANCE_DEVICE_PRIVATE_JWK: JSON.stringify({ kty: "EC", crv: "P-256", x: "x".repeat(43), y: "y".repeat(43), d: markers.jwkD }),
      },
    });
    const timer = setTimeout(() => child.kill(), 120_000);
    child.on("error", (error) => resolve({ error, status: null }));
    child.on("exit", (status) => { clearTimeout(timer); resolve({ status }); });
    });
    const files = readdirSync(dir);
    const file = files.find((f) => f.startsWith("run-") && f.endsWith(".json"));
    reportText = file ? readFileSync(join(dir, file), "utf8") : "";
    report = reportText ? JSON.parse(reportText) : null;
  }, 150_000);

  afterAll(async () => {
    await new Promise<void>((r) => stub.close(() => r()));
    rmSync(dir, { recursive: true, force: true });
  });

  it("completes and writes exactly one report file for the run", () => {
    expect(run.error).toBeUndefined();
    expect(run.status === 0 || run.status === 1, `exit ${run.status}`).toBe(true);
    expect(readdirSync(dir).filter((f) => f.startsWith("run-"))).toHaveLength(1);
    // The vitest JSON and JUnit files carry raw failure messages, so they are not written against another server.
    expect(readdirSync(dir).filter((f) => !f.startsWith("run-"))).toEqual([]);
  });

  it("names the server, the commit of this repository and a result per case", () => {
    expect(report.server).toBe(url);
    expect(report.commit).toMatch(/^([0-9a-f]{40}( \(uncommitted changes\))?|unknown)$/);
    expect(report.cases.length).toBeGreaterThan(20);
    for (const c of report.cases) expect(["passed", "failed", "skipped"]).toContain(c.status);
    expect(report.totals.passed + report.totals.failed + report.totals.skipped).toBe(report.cases.length);
  });

  it("lists the cases that need a mock helper as skipped, each with its reason, never as passed", () => {
    const skipped = report.cases.filter((c: any) => c.status === "skipped");
    expect(skipped.length).toBeGreaterThan(10);
    for (const c of skipped) expect(c.reason, c.name).toMatch(/^needs /);
    expect(skipped.filter((c: any) => /mockHelpers/.test(c.reason)).length).toBeGreaterThan(10);
    for (const name of ["step-up entry > the receipt conforms", "direct entry > the receipt conforms", "a second use of an elevated token", "too_many_pending_requests"]) {
      const c = report.cases.find((x: any) => x.name.includes(name));
      expect(c, name).toBeDefined();
      expect(c.status, name).toBe("skipped");
    }
    // Cases that need only what was supplied do run (and fail against the stub, which answers 401 to everything).
    expect(report.cases.find((c: any) => c.name.includes("an inconsistent grp")).status).toBe("failed");
    // The shared canonicalization vectors need nothing from the server.
    expect(report.cases.find((c: any) => c.name.includes("RFC 8785")).status).toBe("passed");
  });

  it("holds no token, secret or code", () => {
    for (const [name, value] of Object.entries(markers)) expect(reportText, name).not.toContain(value);
    expect(reportText).not.toContain(JSON.stringify(markers.jwkD));
    // The failures are there (and vitest abbreviates the answers in them); the redaction itself is asserted below.
    expect(report.totals.failed).toBeGreaterThan(0);
  });
});

describe("what the report may say", () => {
  it("reports a case's missing inputs by name", () => {
    const env = { CONFORMANCE_OIDC_URL: "https://stage.example" };
    expect(missing(["mockHelpers", "userToken"], env)).toMatch(/mockHelpers.*userToken.*CONFORMANCE_USER_TOKEN/);
    expect(missing(["userToken"], { ...env, CONFORMANCE_USER_TOKEN: "x" })).toBeNull();
    expect(missing(["mockHelpers"], {})).toBeNull(); // in-process mock
  });

  it("redacts supplied secrets and token-shaped values", () => {
    const env = { CONFORMANCE_USER_TOKEN: "super-secret-value" };
    const text = redact(`HTTP 401 Bearer abcdef123456 {"binding_code":"123456","note":"super-secret-value","auth_req_id":"ciba_x1"} eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln`, env);
    for (const leak of ["super-secret-value", "abcdef123456", "123456", "ciba_x1", "eyJhbGci"]) expect(text).not.toContain(leak);
  });

  it("drops credentials, query and fragment from the server URL", () => {
    expect(publicUrl("https://user:pw@stage.example/path/?token=abc#frag")).toBe("https://stage.example/path");
  });
});
