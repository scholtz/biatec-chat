---
id: T-074
title: Widget safe card for safe.list and safe.create
status: done
priority: P2
epic: "Safe"
blocked_by: [T-032, T-042]
area: ["packages/chat-widget/src/components/cards/SafeCard.vue", "backend/Biatec.Chat.Api/Contracts/**", "packages/chat-protocol/**"]
parallel: no
conflicts_with: [T-064, T-067, T-076, T-083]
issue: "#138"
branch: "task/T-074"
owner: "worker-T-074"
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

- [x] safe.list with a fixture shows each safe, its groups with epoch and each pending proposal's approvals, payload version and expiry
- [x] safe.create shows app id and safe address once deployed
- [x] The card is built from the event, never from model text (test)

## Verification

`pnpm -C packages/chat-widget test`, backend contract tests, `dotnet test --filter SafeCreate`.

## Hand-off

- **Summary:** new `safe_card` chat event (`SafeCard` contract: safes, signer groups with epoch, pending proposals with status, approvals/threshold, approvedByYou, payload version, expiry, unreadable safes, proposals-not-loaded, safe.create state), built by the T-032 executors from their outcomes, never from model text; `SafeCard.vue` in the card set; shape-checked in `transport/sse.ts`, stored by `useChat`, rendered in `ChatView`; screen-reader summary for card-only replies.
- **Branch:** `task/T-074` @ `aef95e6` (merged with main), PR #160 (`Closes #138`).
- **Files outside the area:** `Contracts/ChatEvents.cs`, `Safe/Create/{SafeCreateExecutors,SafeListService,SafeCreateService}.cs`, `ChatView.vue`, `useChat.ts`, `transport/sse.ts`, `i18n/en/safe.json` + `i18n/index.ts`, `docs/public-chat-contract.md`; `openapi.json` and chat-protocol regenerated (also older uncommitted drift: AdoptRequest, PublicChatRequest).
- **Verification:** widget tests 200/200, typecheck, build; chat-protocol build; backend 956 passed, 1 skipped, 0 failed.
- **Review (medium):** fixed safes past the 10-safe probe limit shown as "no pending proposals", thinking indicator ending early, loose frame shape check, no card while setup is in flight, missing contract doc entry, card-only replies not announced. Known limitations: duplicated FromList/Render projection; badges not via StatusBadge; no Slovak strings (no sk catalog yet, T-043).
- **Open questions:** `pnpm --filter chat-protocol generate` needs `ASPNETCORE_ENVIRONMENT=Development` (pre-existing).
- **Unblocks:** no task is blocked by T-074; T-067 (conflict) can start once this leaves in-progress.

## Review

- 2026-10-20 03:00 · approve · PR #160 merged into main @ c04925d (recorded by kanban-sync)

## Activity log

- 2026-10-20 02:30 · coordinator · (new) → blocked · proposed by T-032; blocked by T-042 (card set and message-list rendering); conflicts with T-064 and T-067 (ChatView.vue/useChat.ts, contracts)
- 2026-10-20 01:57 · kanban-board · blocked → backlog · blockers done: T-032, T-042
- 2026-10-20 02:26 · LukassSoukup · taken · status stays backlog
- 2026-10-20 02:26 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 03:00 · coordinator · in-progress → review · branch task/T-074 @ aef95e6, PR #160
- 2026-10-20 03:00 · user · review → done · PR #160 merged into main @ c04925d (synced by kanban-sync)
