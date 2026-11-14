import { createMockOidc } from "./app.ts";

const port = Number(process.env.PORT ?? 8089);
const host = process.env.HOST ?? "127.0.0.1";
// The /mock/* helpers and the predictable seeded device tokens are for tests only: anyone who can reach a mock that has them can approve
// consent as a seeded user. Start with MOCK_TEST_HELPERS=1 for the authenticator contract tests and the conformance kit.
const testHelpers = process.env.MOCK_TEST_HELPERS === "1";
// MOCK_DEVICE_PROOF=1: device calls need a signed proof of possession on top of the bearer token (BCHAT-030).
const deviceProof = process.env.MOCK_DEVICE_PROOF === "1";
// MOCK_MIN_APP_BUILD=<n>: the lowest authenticator build number accepted (header X-Biatec-Min-App-Build; default 1).
const minAppBuild = Number(process.env.MOCK_MIN_APP_BUILD ?? 1);
if (!Number.isInteger(minAppBuild) || minAppBuild < 0 || minAppBuild > 1_000_000) {
  throw new Error("MOCK_MIN_APP_BUILD must be a whole number from 0 to 1000000");
}
// MOCK_JSON_GROUPS=1: the unsigned group is base64(JSON) descriptors instead of Algorand transactions (only the e2e demo harness needs it).
const jsonGroups = process.env.MOCK_JSON_GROUPS === "1";
// MOCK_MAX_PENDING=<n>: pending requests per user, client and origin (default 3, rule 10). MOCK_MAX_PER_HOUR=<n>: new requests per user and client per hour (default 60).
const maxPendingPerClientOrigin = process.env.MOCK_MAX_PENDING ? Number(process.env.MOCK_MAX_PENDING) : undefined;
const maxRequestsPerHour = process.env.MOCK_MAX_PER_HOUR ? Number(process.env.MOCK_MAX_PER_HOUR) : undefined;
for (const n of [maxPendingPerClientOrigin, maxRequestsPerHour]) if (n !== undefined && (!Number.isInteger(n) || n < 1)) throw new Error("MOCK_MAX_PENDING and MOCK_MAX_PER_HOUR must be whole numbers of at least 1");
const { server, issuer } = createMockOidc({ testHelpers, deviceProof, minAppBuild, jsonGroups, maxPendingPerClientOrigin, maxRequestsPerHour });
server.listen(port, host, () => console.log(`mock-oidc (${issuer}) listening on http://${host}:${port}${testHelpers ? " with test helpers (MOCK_TEST_HELPERS=1)" : ""}`));
