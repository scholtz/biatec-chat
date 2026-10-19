---
id: T-042
title: Widget action preview, consent and receipt cards
status: done
priority: P0
epic: "Widget"
blocked_by: [T-041, T-062]
area: ["packages/chat-widget/src/components/cards/**"]
parallel: no
conflicts_with: [T-036, T-064, T-067]
issue: "#43"
branch: "task/T-042"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#action-preview"
created: 2026-10-19
updated: 2026-10-20
---

# T-042 · Widget action preview, consent and receipt cards

## Context

Card shows intent, network, source and destination, assets and amounts, fees, slippage, simulation, limit bucket, consent prediction; countdown; Confirm disabled under 600 s; Authorize signing button; waiting-for-phone state with binding code; receipt with explorer link and Download evidence; multi-step list.

Design: build the cards directly in the **Biatec AI** design (prototype screens 6 TradeProposalCard and Confirmation card, 10 Review trade dialog in `docs/design/biatec-ai-widget/README.md`), using the tokens and primitives from T-062 and the rules in `packages/chat-widget/AGENTS.md`. Spec, User Experience 5: "The card has a dashed border until the user confirms and a solid one after, a status badge from the fixed status set …, the intent as its title, the network label, fees, slippage or worst case, the limit bucket, and whether phone approval is expected. Confirm is the only 48 px primary in the widget and names the action and amount ("Confirm swap · 100 USDC"); it is enabled only after the user has opened the review step, which answers what will happen, which permissions are involved, the exact details, the financial implications (including, when a fiat value is known, what a 10 percent fall would cost), and how to stop it." Status labels: the "Preview status mapping" table in `docs/design/README.md`.

## Goal

The trust surface in the browser.

## Scope

- In: Card components bound to chat-protocol types, countdown, confirm and cancel calls, elevation popup reuse, consent pending state, receipt card.
- In: Biatec AI look: dashed → solid border, StatusBadge per the status mapping, network label (Testnet / Mainnet), expandable details, review dialog with the five answers and an acknowledgement checkbox, 48 px shield-check Confirm stating action and amount and locked while submitting, downside row when a fiat value exists, multi-step list with completed steps showing their tx ids.
- Out: Authenticator.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-036, T-064, T-067: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] All preview fields render from a fixture
- [ ] Confirm disabled below 600 s remaining
- [ ] Authorize signing triggers elevate popup
- [ ] Countdown is min(group validity, session absolute lifetime); under 20 min of session left the widget warns and offers to finish or save the pending action
- [ ] Receipt shows tx id link and evidence link
- [ ] Border is dashed before Confirm and solid after; every ActionPreviewStatus and consent state shows the label from `docs/design/README.md`
- [ ] Confirm reads "Confirm <action> · <amount> <unit>", is 48 px, and stays disabled until the review step was opened and its checkbox ticked
- [ ] Countdown uses tabular numerals in a neutral colour (no red, no flashing)

## Verification

pnpm --filter chat-widget test

## Hand-off

## Review

- 2026-10-20 01:46 · approve · PR #131 merged into main @ 8d962de (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:00 · coordinator · review · added session-lifetime countdown and 20-minute warning criterion
- 2026-10-19 22:40 · coordinator · blocked · widget UI rework: build in the Biatec AI design (docs/design/), blocked_by T-062 for tokens and primitives
- 2026-10-20 00:55 · kanban-board · blocked → backlog · blockers done: T-041, T-062
- 2026-10-20 01:09 · LukassSoukup · backlog · parallel analysis: added T-064 and T-067: cards render in components/chat/ChatView.vue and their events go through useChat.ts (T-064's area)
- 2026-10-20 01:16 · scholtz · taken · status stays backlog
- 2026-10-20 01:16 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-20 01:46 · user · in-progress → done · PR #131 merged into main @ 8d962de (synced by kanban-sync)
