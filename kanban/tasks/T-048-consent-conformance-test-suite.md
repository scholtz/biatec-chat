---
id: T-048
title: Consent conformance test suite
status: done
priority: P2
epic: "Consent & authenticator"
blocked_by: [T-047]
area: ["packages/consent-protocol/conformance/**"]
parallel: yes
conflicts_with: []
issue: "#49"
branch: "task/T-048"
owner: "session-20261019-agent"
taken_by: "scholtz"
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

- 2026-10-19 20:47 · approve · PR #89 merged into main @ 9cb23a8 (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 20:38 · kanban-board · blocked → backlog · blockers done: T-047
- 2026-10-19 20:38 · scholtz · taken · status stays backlog
- 2026-10-19 20:38 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-19 20:47 · user · in-progress → done · PR #89 merged into main @ 9cb23a8 (synced by kanban-sync)
