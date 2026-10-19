---
id: T-036
title: Evidence export bundle and offline verifier
status: blocked
priority: P1
epic: "Evidence"
blocked_by: [T-035]
area: ["backend/Biatec.Chat.Api/Evidence/Export/**", "packages/evidence-verifier/**"]
parallel: no
conflicts_with: [T-032, T-042, T-043, T-066]
issue: "#37"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#session-recording-and-evidence"
created: 2026-10-19
updated: 2026-10-20
---

# T-036 · Evidence export bundle and offline verifier

## Context

User exports a signed JSON bundle; verifier recomputes payload hashes when present, checks chain and signatures against published JWKS.

## Goal

Demo step 8 and success criterion 6.

## Scope

- In: Export endpoint for the session's conversation, bundle format, TypeScript verifier package with CLI, widget download link uses the endpoint.
- Out: Anchor proofs (T-037).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-032, T-042, T-043, T-066: they change the same files, and running them together causes merge conflicts or broken behaviour.

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
- 2026-10-19 22:40 · coordinator · blocked · conflicts_with T-066 added: the evidence download link is wired into the widget activity aside
- 2026-10-20 01:09 · LukassSoukup · blocked · parallel analysis: dropped T-037, T-038, T-039, T-040, T-041 (done)
