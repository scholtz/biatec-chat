---
id: T-102
title: "DEX playground page: fix serious axe violations"
status: in-progress
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

- [ ] axe reports no serious violations on /dex.html in dark and light (test)

## Verification

Playwright/axe or jsdom axe on the built demo /dex.html.

## Hand-off

- **Fixes:** (1) light-mode contrast: `playground/dex/dex.css` overrides Aura's light button tokens (primary emerald-700/800/900, success green-700/800/900, danger red-600/700/800 under white labels; link buttons emerald-700) and the SelectButton label (surface-600 on surface-100), each as a `light-dark()` pair whose dark half is Aura's own; (2) the "Ask Biatec AI" link: the `BiatecAiChip` moved out of the button's `#icon` slot to sit beside it, `aria-hidden`, because its SVG "AI" text was visible but not in the button's name (`label-content-name-mismatch`). `docs/integration-biatec-dex.md` step 4 updated to match (its snippet test requires it).
- **Test:** `test/dex-host-a11y.test.ts` mounts DexApp in jsdom (dark, light): axe without contrast, button visible text ⊂ accessible name (fails on the old markup), and the light-mode overrides reach 4.5:1 against Aura's palette. `@/` alias added to the vitest config and tsconfig paths.
- **Verification:** Chromium + axe-core 4.14 on the built playground dex.html and the built demo /dex.html: dark and light, 1280 and 375 px, plain, settings dialog open, and Sell side: 0 serious/critical (was 1 dark / 2 light). Remaining axe "incomplete" contrast items are translucent/backdrop-filter surfaces axe cannot measure. Widget 363 tests, typecheck, typecheck:playground, demo build and tests pass.

## Review

## Activity log

- 2026-10-20 07:59 · coordinator · (new) → backlog · proposed by a worker
- 2026-10-20 07:59 · LukassSoukup · taken · status stays backlog
- 2026-10-20 07:59 · coordinator · backlog → in-progress · dispatched to worker
