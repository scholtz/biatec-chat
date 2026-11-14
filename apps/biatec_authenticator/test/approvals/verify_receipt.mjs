// Verifies a receipt the app signed with `verifyApproval` of the protocol kit (packages/consent-protocol/scripts/lib.mjs). stdin: { receipt, request, code }. Prints "true" or "false".
import { readFileSync } from "node:fs";
import { verifyApproval } from "../../../../packages/consent-protocol/scripts/lib.mjs";

const jwks = JSON.parse(readFileSync(new URL("../../../../packages/consent-protocol/vectors/jwks.json", import.meta.url), "utf8"));
const { receipt, request, code } = JSON.parse(readFileSync(0, "utf8"));
const ok = verifyApproval(receipt, jwks, request, code, new Set(), { now: 1792425700, issuer: "https://oidc.biatec.test", deviceActive: true });
console.log(String(ok));
