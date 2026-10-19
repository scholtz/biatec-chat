---
id: T-044
title: Required OIDC changes handoff document
status: backlog
priority: P1
epic: "Consent & authenticator"
blocked_by: []
area: ["docs/required-changes-biatec-oidc.md"]
branch: ""
owner: ""
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
