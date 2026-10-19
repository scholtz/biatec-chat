---
id: T-044
title: Required OIDC changes handoff document
status: in-progress
priority: P1
epic: "Consent & authenticator"
blocked_by: []
area: ["docs/required-changes-biatec-oidc.md"]
parallel: yes
conflicts_with: []
issue: "#45"
branch: "task/T-044"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#required-changes-to-biatec-oidc"
created: 2026-10-19
updated: 2026-10-19
---

# T-044 · Required OIDC changes handoff document

## Context

We work only in this repo; every Biatec OIDC addition must be specified for its owners, with the fallback this repo uses.

## Goal

One precise handoff spec plus the fallback matrix agents implement against.

## Scope

- In: Document each addition from the table: API shape, error formats (step-up 401 body, CIBA token response with consent_receipt), and which fallback this repo implements now; include the confidential client registration request for the Biatec Chat tenant.
- Out: Implementing them.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Every row of the Required Changes table is covered
- [ ] Each has request/response examples
- [ ] Fallback per row is stated
- [ ] Linked from AGENTS.md

## Verification

Review the document

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → backlog · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 19:05 · scholtz · taken · status stays backlog
- 2026-10-19 19:05 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
