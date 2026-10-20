---
id: T-102
title: "DEX playground page: fix serious axe violations"
status: backlog
priority: P3
epic: "Widget"
blocked_by: []
area: ["packages/chat-widget/playground/dex/**"]
parallel: yes
conflicts_with: []
issue: "#211"
branch: ""
owner: ""
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

- [ ] axe reports no serious violations on /dex.html in dark and light (test)

## Verification

Playwright/axe or jsdom axe on the built demo /dex.html.

## Hand-off

## Review

## Activity log

- 2026-10-20 07:59 · coordinator · (new) → backlog · proposed by a worker
- 2026-10-20 07:59 · LukassSoukup · taken · status stays backlog
