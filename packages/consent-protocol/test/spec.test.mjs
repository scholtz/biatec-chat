import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import { createHash } from "node:crypto";
import { jcs } from "../scripts/lib.mjs";

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

test("no operator-vouched client identity and no third-party push relay outside the 1.0 branches and the Changes sections", () => {
  const banned = /verified|entity_id|display_name|logo_uri|firebase|fcm/i;
  // Everything except the two "Changes from" sections, which name what was dropped.
  const body = spec.split("## 13. Changes from 1.0")[0] + spec.split("## 14. Relying parties")[1].split("## 16. Changes from 1.1")[0];
  assert.equal(banned.test(body), false, "spec text outside the Changes sections: " + (body.match(banned) ?? [""])[0]);
  for (const f of readdirSync(join(root, "schemas"))) {
    const s = JSON.parse(readFileSync(join(root, "schemas", f), "utf8"));
    // The 1.0 branches are the only place the 1.0 members may stay.
    for (const k of ["v1_0", "clientV1_0", "legacyClientNoText_v1_0"]) if (s.$defs) delete s.$defs[k];
    const text = JSON.stringify(s);
    assert.equal(banned.test(text), false, f + ": " + (text.match(banned) ?? [""])[0]);
  }
});

// ---- protocol 1.2
const load = (...p) => JSON.parse(readFileSync(join(root, ...p), "utf8"));
const rp = load("vectors", "receipts-1.2.json");
const linkFile = load("vectors", "link.json");
const block = (name, nth = 0) => JSON.parse([...spec.matchAll(new RegExp("```json schema=" + name + "\\n([\\s\\S]*?)```", "g"))][nth][1]);

test("the spec has Relying parties, Text rules and Changes from 1.1, and names the 1.2 vectors", () => {
  for (const h of ["14-relying-parties", "15-text-rules", "16-changes-from-11", "33-biatec_payment", "36-details_hash", "84-verification-of-relying-party-receipts-attestation-mode", "85-verification-with-a-pinned-device-key-no-attestation"]) assert.ok(headings.includes(h) || headings.some((x) => x.startsWith(h.slice(0, 3))), h);
  assert.ok(headings.includes("14-relying-parties") && headings.includes("15-text-rules") && headings.includes("16-changes-from-11"));
  for (const f of ["receipts-1.2.json", "link.json", "text-rules.json"]) assert.ok(spec.includes("vectors/" + f), f);
  assert.ok(spec.startsWith("# Biatec Consent Protocol, version 1.2"));
  // the trust model is stated, the four statements and the registration facts
  const ch = spec.split("## 14. Relying parties")[1].split("## 15. Text rules")[0];
  for (const s of ["vouches for no relying party", "only after that user linked it", "exactly the `authorization_details` the relying party sent", "The receipt is signed by the device", "private_key_jwt", "urn:openid:params:grant-type:ciba", "client_uri", "/.well-known/biatec-client.json", "biatec-authenticator://link?iss=<issuer>&id=<link_id>&s=<link_secret>", "refuses a link code whose `iss`"]) assert.ok(ch.includes(s), s);
  assert.ok(headings.some((h) => h.startsWith("146-a-new-device")), "what a new device means for a pinned key");
});

test("the envelope version is stated: 1.2 for the new types and 1.1 for biatec_transaction", () => {
  assert.ok(spec.includes('"1.2" for a relying-party receipt') || spec.includes('`"1.2"` for a relying-party receipt'));
  assert.match(spec, /envelope `version` is `"1.1"` for them and `"1.2"` for the three new types/);
});

test("worked examples of the relying-party types and the link equal the vectors", () => {
  const byName = (n) => rp.vectors.find((v) => v.name === n);
  assert.deepEqual(block("authorization-details-payment"), byName("payment-approve").request.authorization_details[0]);
  assert.deepEqual(block("authorization-details-login"), byName("login-approve").request.authorization_details[0]);
  assert.deepEqual(block("authorization-details-action"), byName("action-approve").request.authorization_details[0]);
  assert.deepEqual(block("consent-request", 1), byName("payment-approve").request);
  const link = linkFile.vectors.find((v) => v.name === "link-approve-domain-proven");
  assert.deepEqual(block("link-created"), link.link_created);
  assert.deepEqual(block("link-request"), link.link_request);
  assert.deepEqual(block("link-signed"), link.envelope.signed);
  assert.ok(spec.includes(link.canonical));
  assert.deepEqual(block("backchannel-authentication-request", 1), { scope: "openid", login_hint: rp.subject, authorization_details: [byName("payment-approve").sent_details], origin: byName("payment-approve").server_origin, requested_expiry: 300 });
});

test("the details_hash worked example: canonical bytes, SHA-256 and base64url match the vector", () => {
  const v = rp.vectors.find((x) => x.name === "payment-approve");
  const canonical = jcs(v.request.authorization_details[0]);
  assert.ok(spec.includes(canonical));
  assert.ok(spec.includes(jcs(v.sent_details)));
  assert.ok(spec.includes(createHash("sha256").update(canonical).digest("hex")));
  assert.ok(spec.includes(v.details_hash));
  assert.equal(createHash("sha256").update(canonical).digest("base64url"), v.details_hash);
  assert.ok(spec.includes(v.canonical), "the canonical receipt bytes");
  assert.ok(spec.includes(rp.iban_alteration.altered_details_hash));
  assert.ok(spec.includes(rp.iban_alteration.altered_iban));
});

test("the text rules section states every rule of the task and the core and profiles", () => {
  const t = spec.split("## 15. Text rules")[1].split("## 16.")[0];
  for (const s of ["NFC", "code points", "control characters", "U+202A to U+202E", "U+2066 to U+2069", "U+200E", "U+200F", "U+061C", "U+200B to U+200D", "U+2060", "U+FEFF", "line breaks", "refused, never stripped", "isDisplaySafe", "isRelyingPartyText", "vectors/text-rules.json", "core"]) assert.ok(t.includes(s), s);
});

test("every limit of the three relying-party types is in the spec", () => {
  for (const s of ["1 to 70", "1 to 34", "1 to 20", "1 to 140", "1 to 80", "1 to 60", "1 to 8", "1 to 30", "mod-97", "decimal string with at most two decimals"]) assert.ok(spec.includes(s), s);
});
