---
id: T-074
title: Widget safe card for safe.list and safe.create
status: backlog
priority: P2
epic: "Safe"
blocked_by: [T-032, T-042]
area: ["packages/chat-widget/src/components/cards/SafeCard.vue", "backend/Biatec.Chat.Api/Contracts/**", "packages/chat-protocol/**"]
parallel: no
conflicts_with: [T-064, T-067, T-076]
issue: "#138"
branch: ""
owner: ""
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#safe-capabilities"
created: 2026-10-20
updated: 2026-10-20
---

# T-074 · Widget safe card for safe.list and safe.create

## Context

Proposed by the T-032 worker. T-032 returns the safe card data (safes, the user's member addresses, signer groups with
membership epoch, pending proposals with "N of M approvals", payload version, expiry round, approvedByYou; for
safe.create the app id and safe address) only as tool-result JSON to the model. The spec asks to "show the proposal
queue, approval progress, membership epoch and expiry rounds in a card".

## Goal

safe.list and safe.create results show as a safe card in the widget, from structured data, not model text.

## Scope

- In: a chat event carrying the safe card data (OpenAPI contract and regenerated `packages/chat-protocol`), emitted
  from the T-032 executors' results; `SafeCard.vue` in the card set T-042 builds (Biatec AI look, status badges,
  progress, expiry); rendering it in the message list; English (and Slovak) strings in their own i18n section.
- Out: proposal actions (T-033), the evidence export (T-036).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-064, T-067, T-076: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] safe.list with a fixture shows each safe, its groups with epoch and each pending proposal's approvals, payload version and expiry
- [ ] safe.create shows app id and safe address once deployed
- [ ] The card is built from the event, never from model text (test)

## Verification

`pnpm -C packages/chat-widget test`, backend contract tests, `dotnet test --filter SafeCreate`.

## Hand-off

## Review

## Activity log

- 2026-10-20 02:30 · coordinator · (new) → blocked · proposed by T-032; blocked by T-042 (card set and message-list rendering); conflicts with T-064 and T-067 (ChatView.vue/useChat.ts, contracts)
- 2026-10-20 01:57 · kanban-board · blocked → backlog · blockers done: T-032, T-042
- 2026-10-20 02:26 · LukassSoukup · taken · status stays backlog
