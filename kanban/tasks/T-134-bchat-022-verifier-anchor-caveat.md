---
id: T-134
title: "Say what the offline verifier did not check: anchoring (BCHAT-022)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["packages/evidence-verifier/src/**", "packages/evidence-verifier/test/**"]
parallel: yes
conflicts_with: []
issue: "#275"
branch: "task/T-134"
owner: "session-20261023-1127"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-134 - Say what the offline verifier did not check: anchoring (BCHAT-022)

## Context

Security audit finding BCHAT-022 (Low, issue #275). The offline verifier prints "OK: ... verified" for a bundle whose entries carry no
anchor, and it does not resolve `anchorRef` values against the chain at all. A reader can take "verified" to mean that the log existed at a
point in time, when it only proves the hash chain and the signatures: the operator, who holds the signing key, could have rewritten the log.

## Goal

The verifier reports how many entries are anchored and how many are not, always states that anchors were not resolved, and the CLI's OK line says so.

## Scope

- In: `anchoredEntries` / `unanchoredEntries` in the result; a warning that anchors are not resolved (stronger wording when nothing is anchored); the CLI OK line; tests.
- Out: resolving anchors against Algorand from the verifier (a feature, not a wording fix).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files (BCHAT-023 follows it).

## Acceptance criteria

- [x] `verifyBundle` returns `anchoredEntries` and `unanchoredEntries` that add up to `entries`.
- [x] A valid bundle always carries a warning that anchors were not resolved; with no anchored entry it also says the log is not yet anchored.
- [x] The CLI OK line ends with a caveat that no anchor was checked; `--json` includes the counts.
- [x] Existing verifier tests stay green.

## Verification

`pnpm --filter evidence-verifier test`.

## Hand-off

- **Summary:** The offline verifier now returns anchoredEntries and unanchoredEntries and, for a valid bundle, always warns that anchors are not resolved (stronger wording when nothing is anchored). The CLI OK line ends with the counts and 'anchors were NOT checked'. PR #318.
- **Files changed:** packages/evidence-verifier/src/verify.ts, src/cli.ts, test/verify.test.ts.
- **How it was verified:** pnpm --filter evidence-verifier test (25 passed): counts add up on the backend fixture; self-signed bundles with no anchor and with 2 of 3 anchored give the two wordings; an invalid bundle gets no caveat; the CLI prints the caveat; blank anchorRef anchors nothing; a machine-readable anchorsResolved=false.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** The verifier still does not resolve anchorRef values against Algorand (threat model row 20 says it should, once anchoring runs); this task only makes the output honest about it. Want a task for the resolver?

## Review

## Activity log

- 2026-10-23 16:00 · claude · (new) → backlog · created from GitHub issue #275 (BCHAT-022); blockers scan: none
- 2026-10-23 11:27 · scholtz · taken, and session-20261023-1127 · backlog → in-progress · claimed
