// scripts/rp-demo.mjs runs to the end against an in-process mock with the scripted test device, and verifies the receipt with the pinned key only (T-246).
import { test } from "node:test";
import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { AddressInfo } from "node:net";
import { createMockOidc } from "../src/app.ts";
// @ts-expect-error a plain JavaScript module
import { runDemo } from "../../scripts/rp-demo.mjs";

const demoPath = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "scripts", "rp-demo.mjs");

async function withMock<T>(opts: Parameters<typeof createMockOidc>[0], fn: (base: string) => Promise<T>) {
  const mock = createMockOidc(opts);
  await new Promise<void>((r) => mock.server.listen(0, "127.0.0.1", r));
  try {
    return await fn(`http://127.0.0.1:${(mock.server.address() as AddressInfo).port}`);
  } finally {
    mock.server.close();
  }
}

test("rp-demo: registers, links, asks for a payment and verifies the receipt with the pinned key only; nothing secret is printed", async () => {
  await withMock({ testHelpers: true }, async (base) => {
    const lines: string[] = [];
    const r = await runDemo({ server: base, log: (l: string) => lines.push(l) });
    assert.deepEqual([r.ok, r.decision], [true, "approve"]);
    const out = lines.join("\n");
    assert.match(out, /biatec-authenticator:\/\/link\?iss=.+&id=lnk_[0-9a-f]+&s=[A-Za-z0-9_-]{43}/);
    assert.match(out, /verifyReceiptPinned: true/);
    assert.match(out, /pinned 2 device keys/);
    assert.equal(/eyJ|"d":|Bearer |Client-Assertion|client_assertion|device-token/.test(out), false, "no JWT, private member or token in the output");
  });
});

test("rp-demo: the command line runs to the end against a mock (exit code 0)", async () => {
  await withMock({ testHelpers: true }, async (base) => {
    const out = await new Promise<string>((resolve, reject) => {
      execFile(process.execPath, [demoPath, "--server", base], { timeout: 30_000 }, (e, stdout, stderr) => (e ? reject(new Error(String(e) + stderr + stdout)) : resolve(stdout)));
    });
    assert.match(out, /Done\./);
    assert.match(out, /verifyReceiptPinned: true/);
  });
});

test("rp-demo: a mock without test helpers cannot script the phone, so the demo times out instead of faking an approval", async () => {
  await withMock({}, async (base) => {
    const lines: string[] = [];
    await assert.rejects(runDemo({ server: base, log: (l: string) => lines.push(l), timeoutSeconds: 1, pollMs: 50 }), /timed out waiting for the user to link/);
    assert.match(lines.join("\n"), /a real phone \(waiting for you\)/);
    assert.match(lines.join("\n"), /Unproven application|publish https:/);
  });
});

