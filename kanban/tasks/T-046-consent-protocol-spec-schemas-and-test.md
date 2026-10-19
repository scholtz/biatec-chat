---
id: T-046
title: Consent protocol spec, schemas and test vectors
status: backlog
priority: P1
epic: "Consent & authenticator"
blocked_by: []
area: ["packages/consent-protocol/spec/**", "packages/consent-protocol/schemas/**", "packages/consent-protocol/vectors/**"]
parallel: yes
conflicts_with: []
issue: "#47"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#biatec-authenticator-and-the-biatec-consent-protocol"
created: 2026-10-19
updated: 2026-10-19
---

# T-046 · Consent protocol spec, schemas and test vectors

## Context

Profile over CIBA, RAR (biatec_transaction, biatec_limits_change), RFC 9470 step-up, receipt canonical object {authorization_details, auth_req_id, client entity ID, binding code, decision, expiry, sub, binding_message}.

## Goal

The open protocol document integrators can read.

## Scope

- In: Spec markdown, JSON schemas for request objects, authorization_details types and receipts, signed receipt test vectors (JCS + ES256), both entry points described.
- Out: Mock server (T-047).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Schemas validate the vectors
- [ ] Vector signatures verify
- [ ] Both entry points specified with examples
- [ ] Protocol rules 1-11 mapped to spec sections

## Verification

pnpm --filter consent-protocol test

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → backlog · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:00 · coordinator · review · P2 → P1: on the critical path of the P1 demo test (T-059)
