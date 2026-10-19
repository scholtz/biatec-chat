---
id: T-059
title: End-to-end demo test on testnet
status: blocked
priority: P1
epic: "Deploy & docs"
blocked_by: [T-033, T-036, T-042, T-049]
area: ["e2e/**"]
parallel: yes
conflicts_with: []
issue: "#60"
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#demonstration-script"
created: 2026-10-19
updated: 2026-10-19
---

# T-059 · End-to-end demo test on testnet

## Context

End-to-end test of the hackathon script on Algorand testnet using the always-ask fallback or the mock.

## Goal

Confidence the demo works before the freeze.

## Scope

- In: Playwright (or scripted API) run of demo steps 1-8 with two identities, documented manual phone steps when needed, evidence verification at the end.
- Out: Authenticator automation.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Script runs steps 1-8 or documents manual steps
- [ ] Evidence bundles verify
- [ ] Run log saved
- [ ] Known gaps listed

## Verification

pnpm --filter e2e test

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:00 · coordinator · review · added T-049: demo phone approvals need the consent orchestrator
