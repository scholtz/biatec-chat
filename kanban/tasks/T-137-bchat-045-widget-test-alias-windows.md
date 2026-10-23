---
id: T-137
title: "Repair the playground alias so the widget suite passes on Windows too (BCHAT-045)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["packages/chat-widget/vite.config.ts"]
parallel: yes
conflicts_with: []
issue: "#298"
branch: "task/T-137"
owner: "session-20261023-1156"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-137 - Repair the playground alias so the widget suite passes on Windows too (BCHAT-045)

## Context

Security audit finding BCHAT-045 (Low, issue #298): the widget test suite has five failures in the DEX playground tests
(`Failed to resolve import "@/i18n" from "playground/dex/DexApp.vue"`). A failing suite masks regressions. The alias in `vite.config.ts` is a
string key `"@/"` mapped to `fileURLToPath(new URL("./playground/dex/host/", ...))`, which has backslashes on Windows; CI (Linux) passes.

## Goal

The widget suite passes on Windows and Linux.

## Scope

- In: the test alias in `packages/chat-widget/vite.config.ts` (a regex alias with a forward-slash target).
- Out: the playground build config (`playground/vite.config.ts`).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes this file.

## Acceptance criteria

- [ ] `pnpm --filter @biatec/chat-widget test` shows no failures on Windows.
- [ ] CI's widget job still passes on Linux.

## Verification

`pnpm --filter @biatec/chat-widget test`; the PR's CI run.

## Hand-off

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

- 2026-10-23 19:00 · claude · (new) → backlog · created from GitHub issue #298 (BCHAT-045); blockers scan: none
- 2026-10-23 11:56 · scholtz · taken, and session-20261023-1156 · backlog → in-progress · claimed
