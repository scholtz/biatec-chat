---
id: T-036
title: Evidence export bundle and offline verifier
status: blocked
priority: P1
epic: "Evidence"
blocked_by: [T-035]
area: ["backend/Biatec.Chat.Api/Evidence/Export/**", "packages/evidence-verifier/**"]
issue: "#37"
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#session-recording-and-evidence"
created: 2026-10-19
updated: 2026-10-19
---

# T-036 · Evidence export bundle and offline verifier

## Context

User exports a signed JSON bundle; verifier recomputes payload hashes when present, checks chain and signatures against published JWKS.

## Goal

Demo step 8 and success criterion 6.

## Scope

- In: Export endpoint for the session's conversation, bundle format, TypeScript verifier package with CLI, widget download link uses the endpoint.
- Out: Anchor proofs (T-037).

## Acceptance criteria

- [ ] Exported bundle verifies with the CLI
- [ ] Tampered entry fails verification
- [ ] Bundle with keys not in JWKS fails
- [ ] Deleted payloads still verify

## Verification

pnpm --filter evidence-verifier test; node cli verify bundle.json

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
