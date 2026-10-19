---
id: T-048
title: Consent conformance test suite
status: blocked
priority: P2
epic: "Consent & authenticator"
blocked_by: [T-047]
area: ["packages/consent-protocol/conformance/**"]
parallel: yes
conflicts_with: []
issue: "#49"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#permissionless-third-party-integration"
created: 2026-10-19
updated: 2026-10-19
---

# T-048 · Consent conformance test suite

## Context

Suite exercises direct and step-up entries and asserts receipts share schema, canonical form and signing rules.

## Goal

Third parties can self-test integrations.

## Scope

- In: Runnable suite (vitest) parameterized by base URL, default against the mock, report output.
- Out: Real OIDC certification.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Suite passes against the mock
- [ ] Both entries covered
- [ ] Fails on a deliberately broken receipt
- [ ] README for integrators

## Verification

pnpm --filter consent-protocol conformance

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
