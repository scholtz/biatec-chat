import { createMockOidc } from "./app.ts";

const port = Number(process.env.PORT ?? 8089);
const host = process.env.HOST ?? "127.0.0.1";
// The /mock/* helpers and the predictable seeded device tokens are for tests only: anyone who can reach a mock that has them can approve
// consent as a seeded user. Start with MOCK_TEST_HELPERS=1 for the authenticator contract tests and the conformance kit.
const testHelpers = process.env.MOCK_TEST_HELPERS === "1";
// MOCK_DEVICE_PROOF=1: device calls need a signed proof of possession on top of the bearer token (BCHAT-030).
const deviceProof = process.env.MOCK_DEVICE_PROOF === "1";
const { server, issuer } = createMockOidc({ testHelpers, deviceProof });
server.listen(port, host, () => console.log(`mock-oidc (${issuer}) listening on http://${host}:${port}${testHelpers ? " with test helpers (MOCK_TEST_HELPERS=1)" : ""}`));
