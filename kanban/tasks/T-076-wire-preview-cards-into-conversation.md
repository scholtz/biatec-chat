---
id: T-076
title: Wire preview, consent and receipt cards into the conversation
status: backlog
priority: P1
epic: "Widget UI rework"
blocked_by: [T-064, T-075]
area: ["packages/chat-widget/src/components/chat/**", "packages/chat-widget/src/components/cards/**"]
parallel: no
conflicts_with: [T-067, T-074, T-085]
issue: "#143"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#user-experience"
created: 2026-10-20
updated: 2026-10-20
---

# T-076 · Wire preview, consent and receipt cards into the conversation

## Context

T-042 (PR #131) built `ActionPreviewCard`, `ReviewDialog` and `ReceiptCard` in `components/cards/`, but left them unwired:
`useChat.ts` still drops `preview_card` and `consent_status` events ("rendered by T-042"), and nothing calls confirm or
cancel. The cards emit `confirm`, `cancel`, `authorize(scope)`, `finish` and `save`. So today a user can't see or confirm
a preview in the widget, and the demo flow (analyze → preview → review → confirm → receipt) stops at the model's text.

T-066 (PR #141) added the hooks this wiring must call: `core.agent.reportRefusal(body)` for an `agent_paused` /
`agent_analyze_only` confirm response (shows the message with Resume / Change mode), and `core.activity.recordConfirm(previewId)`
when a confirm is accepted. Preview card events already feed the Activity timeline through the stream tap in `api/core.ts`.

## Goal

Preview cards appear in the conversation and the user can review, confirm, cancel, authorize signing and see the receipt.

## Scope

- In: keep the latest `preview_card` per `previewId` and the latest `consent_status` on the assistant message, render
  `ActionPreviewCard` (and `ReceiptCard` once completed) in `ChatView`.
- In: confirm call `POST /chat/previews/{id}/confirm { groupHash }` (locks Confirm while in flight), handling every `ConfirmResponse.status`:
  `landed`/`submitted` (update the card, receipt), `consent_pending` (consent state), `scope_required` (elevate popup for
  `reauthorizeScope`, then retry), `sign_in_required`, `sign_in_unavailable`, `failed`, and `agent_paused` / `agent_analyze_only`
  through `core.agent.reportRefusal`.
- In: cancel call `POST /chat/previews/{id}/cancel { groupHash }` (T-075); `authorize` maps to the existing `startElevate` popup.
- In: keepalive while a preview, consent or signing is pending (`transport/keepalive.ts`).
- Out: data cards (T-067); Slovak strings (T-043).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-067, T-074: they change the same files (the cards folder
and the card rendering in `ChatView`), and running them together causes merge conflicts or broken behaviour. T-064, which
also edits `components/chat/**`, is a blocker.

## Acceptance criteria

- [ ] A `preview_card` event renders the card in the assistant message; a later event for the same preview updates it in place
- [ ] Confirm posts the group hash shown; landed/submitted show the receipt; consent_pending shows the phone state
- [ ] scope_required opens the elevate popup and retries once it completes
- [ ] agent_paused / agent_analyze_only show the T-066 refusal with Resume / Change mode, and Activity shows the confirmation after a successful confirm
- [ ] Cancel posts the decision and the card shows Cancelled

## Verification

pnpm --filter @biatec/chat-widget test (fixture streams and a mocked fetch); manual playground run against the local backend.

## Hand-off

## Review

## Activity log

- 2026-10-20 02:10 · session-20261020-0154 · (new) → blocked · follow-up from T-066: T-042's cards were never wired into the conversation
- 2026-10-20 03:21 · kanban-board · blocked → backlog · blockers done: T-064, T-075
