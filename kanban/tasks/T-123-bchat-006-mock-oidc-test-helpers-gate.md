---
id: T-123
title: "Gate the mock OIDC test helpers and seeded device tokens behind an explicit flag (BCHAT-006)"
status: in-progress
priority: P2
epic: "Security"
blocked_by: []
area: ["packages/consent-protocol/mock-oidc/**", "docs/hackathon-demo.md", "docs/integration-third-party-consent.md", "apps/biatec_authenticator/README.md"]
parallel: yes
conflicts_with: []
issue: "#259"
branch: "task/T-123"
owner: "session-20261022-2100"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-22
updated: 2026-10-22
---

# T-123 - Gate the mock OIDC test helpers and seeded device tokens behind an explicit flag (BCHAT-006)

## Context

Security audit finding BCHAT-006 (Medium, issue #259). The standalone mock OIDC serves `GET /mock/devices/{kid}`
(returning `device_token`), `POST /mock/devices/{kid}/activate`, the pause helper and the push outbox with no
environment gate, its seeded devices have predictable tokens (`device-token-alice`), and the Docker image listens on
`0.0.0.0`. Anyone who reaches such a mock can approve consent as the seeded user.

## Goal

A standalone mock serves `/mock/*` and predictable seeded device tokens only when `MOCK_TEST_HELPERS=1`; the image
binds to loopback by default.

## Scope

- In: `testHelpers` option (default on for in-process use by tests, off in `server.ts` unless `MOCK_TEST_HELPERS=1`);
  random seeded device tokens when off; 404 for `/mock/*` when off; `HOST=127.0.0.1` image default; docs and a mock test.
- Out: removing `device_token` from the helper (the authenticator contract tests and e2e need it; see Hand-off); BCHAT-024.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] With helpers off, `GET /mock/devices/alice-device` is 404 and `Device device-token-alice` is rejected.
- [x] With helpers on, existing mock-oidc tests pass unchanged.
- [x] `server.ts` enables helpers only for `MOCK_TEST_HELPERS=1`; the Dockerfile defaults `HOST=127.0.0.1`; docs say how to start the mock for contract tests.
- [x] TEST-010 passes.

## Verification

`pnpm --filter mock-oidc test`; `BIATEC_ROOT="$PWD" bash audits/harness/claude-fable-5-1/*/TEST-010.sh`.

## Hand-off

- **Summary:** `testHelpers` option (default off; tests that embed the mock pass `testHelpers: true`; the standalone server enables it only for `MOCK_TEST_HELPERS=1`). Off: `/mock/*` is 404 and seeded devices get random tokens instead of `device-token-<user>`. Dockerfile defaults `HOST=127.0.0.1`. Docs updated. PR #305.
- **Files changed:** mock-oidc src/app.ts, src/server.ts, Dockerfile, test, README; docs; authenticator and conformance READMEs.
- **How it was verified:** `pnpm --filter mock-oidc test` 26 passed (new test: helpers 404, predictable token rejected); conformance suite 23 passed; TEST-010 passes. Flutter contract tests not run (no Flutter here).
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** The finding also says never return `device_token` from the helper. I kept it because the authenticator contract tests and e2e read it; with the flag off it is unreachable. Want a follow-up to rework those tests?

## Review

## Activity log

- 2026-10-22 21:00 · claude · (new) → backlog · created from GitHub issue #259 (BCHAT-006); blockers scan: none
- 2026-10-22 21:01 · scholtz · taken, and session-20261022-2100 · backlog → in-progress · claimed
