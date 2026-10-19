---
id: T-073
title: Document the widget host API in the integration docs
status: backlog
priority: P3
epic: "Deploy & docs"
blocked_by: [T-063]
area: ["docs/integration-widget.md"]
parallel: yes
conflicts_with: []
issue: "#134"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#embedding-modes"
created: 2026-10-20
updated: 2026-10-20
---

# T-073 · Document the widget host API in the integration docs

## Context

Proposed by the T-063 worker. T-063 shipped the `mode` option (floating / inline / workspace), `BiatecChatInline`
and `useBiatecChat()` (open, close, ask → sent / busy / empty / unavailable, setPageContext, setTheme, setLocale).
`docs/integration-widget.md` doesn't describe them yet. (`docs/integration-biatec-dex.md` is T-068's hand-off doc and
gets updated there.)

## Goal

A host developer can embed the widget in every mode and drive it from the page using only the docs.

## Scope

- In: `docs/integration-widget.md` section on modes, `BiatecChatInline` and `useBiatecChat()` with examples and the
  known limitations from T-063's hand-off.
- Out: code changes; the DEX playground and `docs/integration-biatec-dex.md` (T-068).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [ ] Every public export of the host API is documented with a working example
- [ ] The known limitations from T-063's hand-off are listed

## Verification

Read the docs against `packages/chat-widget/src/api/types.ts` and `src/index.ts`; the examples type-check in the playground.

## Hand-off

## Review

## Activity log

- 2026-10-20 01:40 · coordinator · (new) → blocked · proposed by T-063; blockers scan: T-068 owns docs/integration-biatec-dex.md, so that doc stays out of this task's area
- 2026-10-20 01:32 · kanban-board · blocked → backlog · blockers done: T-063
