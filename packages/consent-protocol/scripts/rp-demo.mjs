#!/usr/bin/env node
// A relying party end to end (protocol 1.2, spec chapter 14): what a bank does to use Biatec Authenticator as its confirmation app.
//
//   node packages/consent-protocol/scripts/rp-demo.mjs --server http://127.0.0.1:8089
//
// 1. registers itself (RFC 7591, `private_key_jwt`; the key pair is generated in memory and never written anywhere),
// 2. starts a link and prints the link code of the QR (`biatec-authenticator://link?iss=...&id=...&s=...`) and the six digit code,
// 3. waits until the user linked it on the phone and verifies that statement with the device keys it learned (pinning them),
// 4. sends a payment request, polls it, fetches the receipt and verifies it with the PINNED KEY ONLY (`verifyReceiptPinned`: no JWKS, no attestation),
// 5. acts on the signed decision (here: prints what it would execute).
//
// The phone is a scripted test device when the server is a mock started with MOCK_TEST_HELPERS=1 (it uses the test-only device keys of vectors/keys.json);
// with `--manual` (or any other server) the demo shows the QR payload and the code and waits for a real phone to approve. Nothing secret is printed:
// the key never leaves memory, no assertion or token is shown. The link secret is in the QR payload by definition: it is single use and expires in 5 minutes.
//
//   --server <url>     the Biatec OIDC (default $RP_DEMO_SERVER, $OIDC_URL or http://127.0.0.1:8089)
//   --client-uri <url> the https URL of the organisation (default a random rp-demo-<n>.example host; its well-known file is served to a mock through the helper)
//   --user <name>      the scripted phone's user in the mock (default alice)
//   --manual           do not script the phone: wait for a real one (up to --timeout seconds, default 300)
import { pathToFileURL } from "node:url";
import { randomBytes } from "node:crypto";
import { clientFingerprint, thumbprint, verifyLinkPinned, verifyReceiptPinned } from "./lib.mjs";
import { approveLink, approveRequest, generateRpKey, httpJson, parseLinkUri, proveDomain, registerRelyingParty, rpCall, testDevice } from "../mock-oidc/src/test-device.ts";

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/**
 * Runs the whole story against `server`. `log(line)` receives every step. Returns `{ ok, decision, subject, authReqId }`; throws on a protocol failure.
 * Options: `clientUri`, `user`, `manual`, `timeoutSeconds`, `pollMs` (override of the announced interval, for tests of a mock with a fast clock), `payment`.
 */
export async function runDemo({ server, log = console.log, clientUri, user = "alice", manual = false, timeoutSeconds = 300, pollMs, payment } = {}) {
  const step = (n, text) => log(`\n[${n}] ${text}`);
  const base = String(server).replace(/\/+$/, "");
  const deadline = Date.now() + timeoutSeconds * 1000;
  const wait = (seconds) => sleep(pollMs ?? seconds * 1000);

  step(1, `Discovery: ${base}`);
  const cfg = await httpJson(base, "GET", "/.well-known/openid-configuration");
  if (cfg.status !== 200 || !cfg.json?.issuer) throw new Error("no OpenID configuration at " + base);
  const issuer = cfg.json.issuer;
  log(`    issuer ${issuer}`);

  // The phone is scripted only against a mock that has the test helpers.
  const scripted = !manual && (await httpJson(base, "GET", `/mock/devices/${user}-device`)).status === 200;
  const dev = scripted ? await testDevice(base, user) : undefined;
  log(`    the phone: ${scripted ? `scripted test device of ${user} (the mock's test helpers)` : "a real phone (waiting for you)"}`);

  step(2, "Registration (private_key_jwt, key pair generated in memory, no client secret)");
  const host = clientUri ? new URL(clientUri).hostname : `rp-demo-${randomBytes(3).toString("hex")}.example`;
  const { rp, reply } = await registerRelyingParty(base, issuer, { clientUri: clientUri ?? `https://${host}` });
  if (reply.status !== 201) throw new Error(`registration refused: HTTP ${reply.status} ${JSON.stringify(reply.json)}`);
  log(`    client_id ${rp.clientId}   fingerprint ${clientFingerprint(rp.clientId)}   client_uri https://${host}`);
  if (scripted) {
    await proveDomain(rp, host);
    log(`    domain proof: served https://${host}/.well-known/biatec-client.json to the mock { "client_ids": ["${rp.clientId}"] }`);
  } else {
    log(`    domain proof: publish https://${host}/.well-known/biatec-client.json with { "client_ids": ["${rp.clientId}"] } or the phone shows "Unproven application"`);
  }

  step(3, "Linking: the party shows a QR code inside its own authenticated session");
  const created = await rpCall(rp, "POST", "/consent/links", { body: { user_label: "Demo User (account ending 4421)" } });
  if (created.status !== 201) throw new Error(`link refused: HTTP ${created.status} ${JSON.stringify(created.json)}`);
  log(`    QR payload   ${created.json.link_uri}`);
  log(`    code on the screen next to the QR: ${created.json.binding_code}   (valid ${created.json.expires_in} s)`);
  let linkRequest;
  if (scripted) {
    const r = await approveLink(base, dev, created.json.link_uri, { code: created.json.binding_code });
    if (r.posted?.status !== 200) throw new Error("the scripted phone could not link: " + JSON.stringify(r.posted?.json ?? r.linkRequest.json));
    linkRequest = r.linkRequest.json;
    log(`    phone: scanned the code, showed ${linkRequest.client.domain_proven ? linkRequest.client.domain : "Unproven application " + linkRequest.client.fingerprint}, the user typed the code and approved with the approval key`);
  }
  let status;
  let interval = created.json.interval;
  for (;;) {
    const s = await rpCall(rp, "GET", `/consent/links/${created.json.link_id}`);
    if (s.json?.error === "slow_down") interval = s.json.interval;
    else if (s.status !== 200) throw new Error(`link status: HTTP ${s.status} ${JSON.stringify(s.json)}`);
    else if (s.json.status !== "pending") { status = s.json; break; }
    if (Date.now() > deadline) throw new Error("timed out waiting for the user to link on the phone");
    log("    waiting for the phone ...");
    await wait(interval);
  }
  if (status.status !== "linked") throw new Error(`the link ended as ${status.status}`);
  // What the user signed, checked with the device keys of this answer only: the signature under the key named by signed.kid.
  const expectLink = { link_id: created.json.link_id, client_fingerprint: clientFingerprint(rp.clientId), domain: scripted ? host : status.approval.signed.domain, nonce: status.approval.signed.nonce, binding_code: created.json.binding_code };
  if (!verifyLinkPinned(status.approval, status.devices, expectLink)) throw new Error("the link statement does not verify");
  const pins = status.devices;
  log(`    linked. subject ${status.subject}; verified the user's signed statement; pinned ${pins.length} device keys:`);
  for (const p of pins) log(`      ${p.key_use.padEnd(8)} ${p.kid}  thumbprint ${thumbprint(p.jwk)}`);

  step(4, "Payment request (biatec_payment) to the linked user");
  const details = payment ?? { type: "biatec_payment", instructed_amount: { amount: "12.50", currency: "EUR" }, creditor_name: "Muster Energie GmbH", creditor_account: { iban: "DE89370400440532013000" }, remittance: "Invoice 2026-0042", execution_date: "2026-10-20" };
  const origin = `https://netbanking.${host}`;
  const opened = await rpCall(rp, "POST", "/connect/ciba", { body: { scope: "openid", login_hint: status.subject, authorization_details: [details], origin, requested_expiry: 300 } });
  if (opened.status !== 200) throw new Error(`request refused: HTTP ${opened.status} ${JSON.stringify(opened.json)}`);
  log(`    auth_req_id ${opened.json.auth_req_id}   expires in ${opened.json.expires_in} s`);
  log(`    code to show to the user inside the bank's own session: ${opened.json.binding_code}`);
  log(`    asking: ${details.instructed_amount.amount} ${details.instructed_amount.currency} to ${details.creditor_name}, ${details.creditor_account.iban}`);
  if (scripted) {
    const a = await approveRequest(base, dev, opened.json.auth_req_id, { code: opened.json.binding_code });
    if (a.posted?.status !== 200) throw new Error("the scripted phone could not approve: " + JSON.stringify(a.posted?.json ?? a.fetched.json));
    log("    phone: the card showed the amount, the payee and the IBAN as written by the bank; the user typed the code and approved with the approval key");
  }

  step(5, "Polling the token endpoint (CIBA), then the receipt");
  let receipt;
  let tokenInterval = opened.json.interval;
  for (;;) {
    const t = await rpCall(rp, "POST", "/token", { form: { grant_type: "urn:openid:params:grant-type:ciba", auth_req_id: opened.json.auth_req_id } });
    if (t.status === 200) { receipt = t.json.consent_receipt; log(`    token redeemed (scope ${t.json.scope}: useless anywhere, only the receipt matters)`); break; }
    const e = t.json?.error;
    if (e === "authorization_pending") { tokenInterval = t.json.interval ?? tokenInterval; log("    pending ..."); }
    else if (e === "slow_down") tokenInterval = t.json.interval ?? tokenInterval + 5;
    else if (e === "access_denied") {
      const r = await rpCall(rp, "GET", `/consent/receipts/${opened.json.auth_req_id}`);
      if (r.status === 200) { receipt = r.json; log(`    ended: access_denied (${t.json.reason ?? "the user denied"}); fetched the receipt`); break; }
      throw new Error("denied without a receipt");
    } else throw new Error(`token endpoint: HTTP ${t.status} ${JSON.stringify(t.json)}`);
    if (Date.now() > deadline) throw new Error("timed out waiting for the user to decide on the phone");
    await wait(tokenInterval);
  }

  step(6, "Verifying the receipt with the pinned device key only (no JWKS, no attestation, no call to Biatec)");
  const same = await rpCall(rp, "GET", `/consent/receipts/${opened.json.auth_req_id}`);
  if (same.status === 200 && JSON.stringify(same.json) !== JSON.stringify(receipt)) throw new Error("the receipt retrieval differs from the token response");
  const ok = verifyReceiptPinned(receipt, pins, details, {
    origin, auth_req_id: opened.json.auth_req_id, sub: status.subject, clientFingerprint: clientFingerprint(rp.clientId),
    ...(receipt.signed?.decision === "approve" ? { bindingCode: opened.json.binding_code } : {}),
  });
  log(`    verifyReceiptPinned: ${ok}  (signature under a pinned ${receipt.signed?.decision === "approve" ? "approval" : ""} key, details = what was sent, details_hash recomputed, decision ${receipt.signed?.decision})`);
  if (!ok) throw new Error("the receipt does not verify against the pinned keys: nothing is executed");
  const decision = receipt.signed.decision;
  if (decision === "approve") log(`    approved by the user: the bank would now execute the payment and store this receipt as the user's signed consent`);
  else log(`    the result is ${decision}: nothing is executed`);
  log("\nDone.");
  return { ok: true, decision, subject: status.subject, authReqId: opened.json.auth_req_id };
}

function parseArgs(argv, env) {
  const o = { server: env.RP_DEMO_SERVER || env.OIDC_URL || "http://127.0.0.1:8089", manual: false, user: "alice", timeoutSeconds: 300 };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--manual") o.manual = true;
    else if (["--server", "--client-uri", "--user", "--timeout"].includes(a)) {
      const v = argv[++i];
      if (v === undefined) throw new Error(`${a} needs a value`);
      if (a === "--server") o.server = v;
      else if (a === "--client-uri") o.clientUri = v;
      else if (a === "--user") o.user = v;
      else o.timeoutSeconds = Number(v);
    } else if (a === "--help" || a === "-h") o.help = true;
    else throw new Error("unknown argument " + a);
  }
  return o;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const o = parseArgs(process.argv.slice(2), process.env);
    if (o.help) console.log("usage: node rp-demo.mjs [--server <url>] [--client-uri <https url>] [--user <name>] [--manual] [--timeout <seconds>]");
    else {
      const r = await runDemo(o);
      process.exit(r.decision === "approve" ? 0 : 3);
    }
  } catch (e) {
    console.error("rp-demo: " + (e instanceof Error ? e.message : e));
    process.exit(1);
  }
}
