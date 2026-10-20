---
id: T-104
title: "Floating panel must not cover the host navbar on short viewports"
status: done
priority: P3
epic: "Widget"
blocked_by: []
area: ["packages/chat-widget/src/components/ChatPanel.vue", "packages/chat-widget/src/components/shell/**"]
parallel: yes
conflicts_with: []
issue: "#213"
branch: "task/T-104"
owner: "worker-T-104"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-104 · Floating panel must not cover the host navbar on short viewports

## Context

Found by the T-099 worker on the Docusaurus scratch site: on an 800 px high window the open floating panel (700 px, raised by --bc-offset-bottom) covers the right end of the host navbar.

## Goal

The open floating panel never covers the host's top navbar.

## Scope

- In: a --bc-offset-top (or max-height: calc(100vh - offsets)) host variable, documented in docs/integration-widget.md.
- Out: other layout changes.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [x] With --bc-offset-top set, the panel stays below the host navbar at 800 px height (test)

## Verification

`pnpm -C packages/chat-widget test`.

## Hand-off

- **Summary:** `--bc-offset-top` host CSS variable bounds the floating panel's height below a host navbar; defaults unchanged; documented in integration-widget.md and the Docusaurus hand-off.
- **Branch:** `task/T-104`, PR #215 (`Closes #213`).
- **Verification:** widget tests 357/357 incl. a geometry test; typecheck; demo build.
- **Decisions:** CSS variable only (like the other offsets); the proposed "typecheck without a prior build" is not needed (CI builds the widget before typecheck).
- **Unblocks:** no task is blocked by T-104.

## Review

- 2026-10-20 08:05 · approve · PR #215 merged into main @ 8bae226 (recorded by kanban-sync)

## Activity log

- 2026-10-20 07:59 · coordinator · (new) → backlog · proposed by a worker
- 2026-10-20 07:59 · LukassSoukup · taken · status stays backlog
- 2026-10-20 07:59 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 08:04 · coordinator · in-progress → review · PR #215
- 2026-10-20 08:05 · user · review → done · PR #215 merged into main @ 8bae226 (synced by kanban-sync)
