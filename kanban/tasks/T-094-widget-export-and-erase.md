---
id: T-094
title: "Widget: export view and erase button for the user's own conversations"
status: backlog
priority: P2
epic: "Widget"
blocked_by: [T-060]
area: ["packages/chat-protocol/**", "packages/chat-widget/src/components/export/**", "packages/chat-widget/test/**"]
parallel: yes
conflicts_with: []
issue: "#187"
branch: ""
owner: ""
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#session-recording-and-evidence"
created: 2026-10-20
updated: 2026-10-20
---

# T-094 · Widget: export view and erase button for the user's own conversations

## Context

Spec, Session Recording And Evidence: "Users can view and export their own sessions from the widget." T-060 added
`GET /session/conversations` (`ListOwnConversations`), `GET /session/conversations/{id}/evidence` and
`POST /session/erase` (`EraseOwnData`, returns the new `conversationId`; 409 `ActionInFlight` while an action is in
flight). They are not yet in `packages/chat-protocol/openapi.json`, and the widget has no conversation list or erase
button. After an erase the widget must switch to the returned `conversationId` (the old one answers 404).

## Goal

A signed-in user can list, export (download the evidence bundle) and erase their own conversations from the widget.

## Scope

- In: regenerate chat-protocol (`ASPNETCORE_ENVIRONMENT=Development pnpm --filter chat-protocol generate`); an export
  view (list, per-conversation download, a clear note that receipts and transaction references are kept under the
  financial record-keeping carve-out); an erase button with a confirm step that offers export first; switch to the
  returned `conversationId`; handle 409 (finish or cancel the action first). Strings in their own i18n section (en, sk).
- Out: backend changes.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [ ] The export view lists the user's conversations and downloads a bundle that verifies (test with a fixture)
- [ ] Erase asks for confirmation, offers export first, then switches to the new conversation id (test)
- [ ] 409 shows a clear "finish or cancel the action first" message (test)

## Verification

`pnpm -C packages/chat-widget typecheck`, `test`, `build`; `pnpm -C packages/chat-protocol build`.

## Hand-off

## Review

## Activity log

- 2026-10-20 07:40 · coordinator · (new) → blocked · proposed by T-060
- 2026-10-20 04:59 · kanban-board · blocked → backlog · blockers done: T-060
- 2026-10-20 05:08 · LukassSoukup · taken · status stays backlog
