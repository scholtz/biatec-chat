// Verifies a `biatec_link` statement the app signed with `verifyLinkPinned` of the protocol kit (packages/consent-protocol/scripts/lib.mjs): the signature under the device named by `signed.kid`, an approve only
// under an approval key, the link id, the fingerprint, the domain, the nonce, the code and the time. stdin: { approval: { signed, signature }, devices, expect, now }. Prints "true" or "false".
import { readFileSync } from "node:fs";
import { verifyLinkPinned } from "../../../../packages/consent-protocol/scripts/lib.mjs";

const { approval, devices, expect, now } = JSON.parse(readFileSync(0, "utf8"));
const ok = verifyLinkPinned({ ...approval, alg: "ES256" }, devices, expect, { now, consumed: new Set() });
console.log(String(ok));
