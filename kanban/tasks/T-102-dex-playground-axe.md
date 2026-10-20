---
id: T-102
title: "DEX playground page: fix serious axe violations"
status: review
priority: P3
epic: "Widget"
blocked_by: []
area: ["packages/chat-widget/playground/dex/**"]
parallel: yes
conflicts_with: []
issue: "#211"
branch: "task/T-102"
owner: "worker-T-102"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-102 · DEX playground page: fix serious axe violations

## Context

Found by the T-100 worker: the reused T-068 DEX playground page, now public at the stage demo's /dex.html, has 2 serious axe violations in its PrimeVue host chrome (light-mode contrast, a .p-button-link whose label doesn't match its content name). The widget itself has none.

## Goal

/dex.html has no serious axe violations in dark or light.

## Scope

- In: fix the host page's PrimeVue chrome (contrast tokens, button labels).
- Out: the widget.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [x] axe reports no serious violations on /dex.html in dark and light (test)

## Verification

Playwright/axe or jsdom axe on the built demo /dex.html.

## Hand-off

- **Summary:** light-mode button/toggle contrast fixed with `light-dark()` shades (dark unchanged); the Biatec AI chip moved beside the link button so its name equals its label; integration-biatec-dex.md updated; new jsdom a11y test.
- **Branch:** `task/T-102`, PR #218 (`Closes #211`).
- **Verification:** Chromium + axe on playground and demo /dex.html (dark/light, 1280/375): 0 serious/critical; widget tests 364/364; demo build + tests.
- **Note:** the real BiatecDEX needs the same chip placement and light shades (the hand-off doc says so).
- **Unblocks:** no task is blocked by T-102.

## Review

## Activity log

- 2026-10-20 07:59 · coordinator · (new) → backlog · proposed by a worker
- 2026-10-20 07:59 · LukassSoukup · taken · status stays backlog
- 2026-10-20 07:59 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 08:16 · coordinator · in-progress → review · PR #218
