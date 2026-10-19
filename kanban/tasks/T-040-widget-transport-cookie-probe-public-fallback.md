---
id: T-040
title: Widget transport: cookie probe, public fallback, SSE
status: in-progress
priority: P0
epic: "Widget"
blocked_by: [T-005, T-039]
area: ["packages/chat-widget/src/transport/**"]
parallel: no
conflicts_with: [T-036]
issue: "#41"
branch: "task/T-040"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#embedding-modes"
created: 2026-10-19
updated: 2026-10-19
---

# T-040 · Widget transport: cookie probe, public fallback, SSE

## Context

Probe on first panel open (not page load); failure means public transport; ask public endpoint if origin is registered; retry probe with backoff and on Sign in; SSE streaming; CSRF header; keepalive while preview/consent/signing pending.

## Goal

A client layer the UI uses without caring which transport is active.

## Scope

- In: Transport module with cookie and public implementations, probe logic, adoption call on switch, SSE parser using chat-protocol types, keepalive timer.
- Out: UI.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-036: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] Unit tests cover probe success, failure, unregistered origin
- [ ] Switch to cookie transport adopts the conversation
- [ ] SSE events typed
- [ ] No tokens or OAuth params handled in JS

## Verification

pnpm --filter chat-widget test

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 20:06 · kanban-board · blocked → backlog · blockers done: T-005, T-039
- 2026-10-19 20:38 · scholtz · taken · status stays backlog
- 2026-10-19 20:38 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
