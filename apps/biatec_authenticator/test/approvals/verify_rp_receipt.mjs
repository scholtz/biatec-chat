// Verifies a protocol 1.2 receipt the app signed with `verifyReceiptPinned` of the protocol kit (packages/consent-protocol/scripts/lib.mjs): the signature under the pinned keys only (no JWKS, no attestation),
// the details equal to what the relying party sent, `details_hash` recomputed from the signed details. stdin: { receipt, pinned, expectedDetails, opts }. Prints "true" or "false".
import { readFileSync } from "node:fs";
import { verifyReceiptPinned } from "../../../../packages/consent-protocol/scripts/lib.mjs";

const { receipt, pinned, expectedDetails, opts } = JSON.parse(readFileSync(0, "utf8"));
// The receipt is checked after it expired in the test's clock: an archived receipt (spec 8.5).
const ok = verifyReceiptPinned(receipt, pinned, expectedDetails, { ...opts, consumed: new Set(), checkExpiry: false });
console.log(String(ok));
