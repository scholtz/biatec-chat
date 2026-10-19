---
id: T-042
title: Widget action preview, consent and receipt cards
status: blocked
priority: P0
epic: "Widget"
blocked_by: [T-041]
area: ["packages/chat-widget/src/components/cards/**"]
parallel: no
conflicts_with: [T-036]
issue: "#43"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#action-preview"
created: 2026-10-19
updated: 2026-10-19
---

# T-042 · Widget action preview, consent and receipt cards

## Context

Card shows intent, network, source and destination, assets and amounts, fees, slippage, simulation, limit bucket, consent prediction; countdown; Confirm disabled under 600 s; Authorize signing button; waiting-for-phone state with binding code; receipt with explorer link and Download evidence; multi-step list.

## Goal

The trust surface in the browser.

## Scope

- In: Card components bound to chat-protocol types, countdown, confirm and cancel calls, elevation popup reuse, consent pending state, receipt card.
- Out: Authenticator.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-036: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] All preview fields render from a fixture
- [ ] Confirm disabled below 600 s remaining
- [ ] Authorize signing triggers elevate popup
- [ ] Countdown is min(group validity, session absolute lifetime); under 20 min of session left the widget warns and offers to finish or save the pending action
- [ ] Receipt shows tx id link and evidence link

## Verification

pnpm --filter chat-widget test

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:00 · coordinator · review · added session-lifetime countdown and 20-minute warning criterion
