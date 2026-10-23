---
id: T-135
title: "Make the offline verifier strict about JWKS keys and sources (BCHAT-023)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["packages/evidence-verifier/src/**", "packages/evidence-verifier/test/**"]
parallel: yes
conflicts_with: []
issue: "#276"
branch: "task/T-135"
owner: "session-20261023-1136"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-135 - Make the offline verifier strict about JWKS keys and sources (BCHAT-023)

## Context

Security audit finding BCHAT-023 (Low, issue #276). The verifier trusts every key in the JWKS whatever its `use` and `alg`, lets a duplicate
`kid` silently win last, does not check the bundle signing key's lifetime against `exportedAt`, and the CLI fetches a JWKS over plain `http://`.

## Goal

A key that is not a P-256 ES256 signature key is never used; an ambiguous JWKS (duplicate `kid`) fails; the bundle signature is bound to its key's lifetime like entries; the CLI fetches only over https.

## Scope

- In: those four checks in `verify.ts` and `cli.ts`, with tests.
- Out: anchor resolution (BCHAT-022 follow-up).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] A JWKS key with `use` other than `sig` or `alg` other than `ES256` is refused when it would verify something.
- [x] A JWKS with two keys of the same `kid` fails verification.
- [x] A bundle whose `exportedAt` lies outside its signing key's lifetime fails.
- [x] The CLI refuses an `http://` JWKS URL (exit 2); file paths and `https://` still work.
- [x] Existing verifier tests stay green.

## Verification

`pnpm --filter evidence-verifier test`.

## Hand-off

- **Summary:** A JWK that declares a use other than sig or an alg other than ES256 is never used to verify; a JWKS listing a kid twice fails; the bundle signature's key must have been valid at exportedAt (like entry keys at signedAt); the CLI refuses http:// sources. Redirects are refused when fetching; a present but unparseable created_at/retired_at fails instead of meaning 'no bound'; null or id-less keys and duplicate kids fail cleanly in both JWKS and embedded-key modes.
- **Files changed:** packages/evidence-verifier/src/verify.ts, src/cli.ts, test/verify.test.ts.
- **How it was verified:** pnpm --filter evidence-verifier test (34 passed): wrong use/alg, absent use/alg, duplicate kid (JWKS and embedded), null keys, unparseable bounds, malformed exportedAt, exportedAt before created_at and after retired_at, CLI refuses http. The no-redirect guarantee is checked only as a source assertion (no https test server here).
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** The CLI now refuses http:// for the bundle as well as the JWKS, which also blocks verifying against a local dev backend over http; copy the files and pass paths. Tell me if you want an explicit loopback opt-in.

## Review

## Activity log

- 2026-10-23 17:00 · claude · (new) → backlog · created from GitHub issue #276 (BCHAT-023); blockers scan: none
- 2026-10-23 11:36 · scholtz · taken, and session-20261023-1136 · backlog → in-progress · claimed
