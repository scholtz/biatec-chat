import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const spec = readFileSync(join(root, "spec", "biatec-consent-protocol.md"), "utf8");

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
