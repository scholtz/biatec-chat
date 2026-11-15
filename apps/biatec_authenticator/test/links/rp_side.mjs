// The relying party's side of the contract tests (T-247), run with the helpers of the protocol kit (packages/consent-protocol/scripts/rp-client.mjs): what a bank does against the mock.
// One call per process; the state between calls (`rp`: base, issuer, client id, an ephemeral test key generated in memory) is returned to the test and passed back, nothing is written to disk.
//   stdin  { cmd: "register", base, issuer, host }                      -> { rp, fingerprint, host }   (registers, and serves the well-known file when `prove` is true)
//   stdin  { cmd: "call", rp, method, path, body?, form? }              -> { status, json }            (a call with a fresh client assertion)
import { readFileSync } from "node:fs";
import { clientFingerprint } from "../../../../packages/consent-protocol/scripts/lib.mjs";
import { proveDomain, registerRelyingParty, rpCall } from "../../../../packages/consent-protocol/scripts/rp-client.mjs";

const input = JSON.parse(readFileSync(0, "utf8"));
if (input.cmd === "register") {
  const { rp, reply } = await registerRelyingParty(input.base, input.issuer, { clientUri: `https://${input.host}` });
  if (reply.status !== 201) throw new Error(`registration refused: ${reply.status} ${JSON.stringify(reply.json)}`);
  if (input.prove) {
    const served = await proveDomain(rp, input.host);
    if (served.status !== 200) throw new Error(`the mock did not serve the well-known file: ${served.status}`);
  }
  console.log(JSON.stringify({ rp, fingerprint: clientFingerprint(rp.clientId), host: input.host }));
} else if (input.cmd === "call") {
  const r = await rpCall(input.rp, input.method, input.path, { body: input.body, form: input.form });
  console.log(JSON.stringify({ status: r.status, json: r.json }));
} else {
  throw new Error("unknown command " + input.cmd);
}
