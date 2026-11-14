import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const spec = readFileSync(join(root, "spec", "biatec-consent-protocol.md"), "utf8").replaceAll("\r\n", "\n");

const ajv = new Ajv2020({ allErrors: true, strict: true });
for (const f of readdirSync(join(root, "schemas"))) ajv.addSchema(JSON.parse(readFileSync(join(root, "schemas", f), "utf8")));
const validator = (n) => ajv.getSchema(`https://schemas.biatec.io/consent/v1/${n}.schema.json`);

const slug = (h) =>
  h.toLowerCase().replace(/[^a-z0-9 _-]/g, "").trim().replace(/ /g, "-");
const headings = [...spec.matchAll(/^#{1,6} (.+)$/gm)].map((m) => slug(m[1]));

test("spec JSON examples tagged with a schema validate", () => {
  const blocks = [...spec.matchAll(/```json schema=([a-z-]+)\n([\s\S]*?)```/g)];
  const names = blocks.map((b) => b[1]);
  for (const n of ["stepup-challenge", "backchannel-authentication-request", "backchannel-authentication-response", "consent-request"])
    assert.ok(names.includes(n), "example for " + n);
  for (const [, name, body] of blocks) {
    const v = validator(name);
    assert.ok(v, name);
    assert.ok(v(JSON.parse(body)), name + " " + JSON.stringify(v.errors));
  }
});

test("spec consent-request example equals the first vector", () => {
  const [, , body] = spec.match(/```json schema=(consent-request)\n([\s\S]*?)```/);
  const vec = JSON.parse(readFileSync(join(root, "vectors", "receipts.json"), "utf8")).vectors[0];
  assert.deepEqual(JSON.parse(body), vec.request);
});

test("spec canonical worked example equals the first vector", () => {
  const vec = JSON.parse(readFileSync(join(root, "vectors", "receipts.json"), "utf8")).vectors[0];
  assert.ok(spec.includes(vec.canonical));
});

test("both entry points are specified", () => {
  assert.ok(headings.includes("4-step-up-entry"));
  assert.ok(headings.includes("5-direct-entry"));
});

test("protocol rules 1-11 are each mapped to existing spec sections", () => {
  const table = spec.split("## 11. Protocol rules and where they are specified")[1].split("### 11.1")[0];
  for (let n = 1; n <= 11; n++) {
    const row = table.split("\n").find((l) => l.startsWith(`| ${n} |`));
    assert.ok(row, "rule " + n);
    const anchors = [...row.matchAll(/\]\(#([^)]+)\)/g)].map((m) => m[1]);
    assert.ok(anchors.length > 0, "rule " + n + " has a link");
    for (const a of anchors) assert.ok(headings.includes(a), `rule ${n}: anchor #${a} must exist`);
  }
});

test("every schema is referenced by the spec by file name or example tag", () => {
  for (const f of readdirSync(join(root, "schemas"))) {
    const name = f.replace(".schema.json", "");
    assert.ok(spec.includes(f) || spec.includes("schema=" + name), f);
  }
});

test("the spec has a Changes from 1.0 section and names the 1.1 vectors", () => {
  assert.ok(headings.includes("13-changes-from-10"));
  for (const f of ["receipts.json", "receipts-1.0.json", "device-signed.json", "keys.json"]) assert.ok(spec.includes("vectors/" + f), f);
});

test("worked examples of the device-signed statements equal the vectors", () => {
  const dev = JSON.parse(readFileSync(join(root, "vectors", "device-signed.json"), "utf8"));
  const block = (name) => JSON.parse(spec.match(new RegExp("```json schema=" + name + "\\n([\\s\\S]*?)```"))[1]);
  assert.deepEqual(block("settings-change-signed"), dev.settings_changes[0].envelope.signed);
  assert.deepEqual(block("device-approval-signed"), dev.device_approvals[0].envelope.signed);
  const p = dev.pairing_code;
  for (const part of [p.input, p.sha256_hex, String(p.first_four_bytes_uint32), p.code]) assert.ok(spec.includes(part), part);
});

test("no operator-vouched client identity and no third-party push relay outside the 1.0 branches and Changes from 1.0", () => {
  const banned = /verified|entity_id|display_name|logo_uri|firebase|fcm/i;
  const body = spec.split("## 13. Changes from 1.0")[0];
  assert.equal(banned.test(body), false, "spec text before section 13: " + (body.match(banned) ?? [""])[0]);
  for (const f of readdirSync(join(root, "schemas"))) {
    const s = JSON.parse(readFileSync(join(root, "schemas", f), "utf8"));
    // The 1.0 branches are the only place the 1.0 members may stay.
    for (const k of ["v1_0", "clientV1_0", "legacyClientNoText_v1_0"]) if (s.$defs) delete s.$defs[k];
    const text = JSON.stringify(s);
    assert.equal(banned.test(text), false, f + ": " + (text.match(banned) ?? [""])[0]);
  }
});
