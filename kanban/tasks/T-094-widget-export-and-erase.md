---
id: T-094
title: "Widget: export view and erase button for the user's own conversations"
status: done
priority: P2
epic: "Widget"
blocked_by: [T-060]
area: ["packages/chat-protocol/**", "packages/chat-widget/src/components/export/**", "packages/chat-widget/test/**"]
parallel: no
conflicts_with: [T-095]
issue: "#187"
branch: "task/T-094"
owner: "worker-T-094"
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

Parallel work possible: **no**. Don't run it at the same time as T-095: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [x] The export view lists the user's conversations and downloads a bundle that verifies (test with a fixture)
- [x] Erase asks for confirmation, offers export first, then switches to the new conversation id (test)
- [x] 409 shows a clear "finish or cancel the action first" message (test)

## Verification

`pnpm -C packages/chat-widget typecheck`, `test`, `build`; `pnpm -C packages/chat-protocol build`.

## Hand-off

- **Summary:** chat-protocol regenerated for T-060's contracts. The Activity sheet's "Your conversations" lists the user's own conversations (backend `current` flag), per-row bundle download, and the 7-year receipts note; signed-in users get "Erase my data" with a confirm step that offers "Download all your evidence first" (one combined file of independently verifiable bundles; failures counted across the export and listed). After erase, `core.resetConversation()` (shared with sign-out) clears the transcript, activity log, previews, consent and holds and re-reads the session; 409 → "Finish or cancel the action first". `RetentionClient` uses the cookie and CSRF/client-key headers only. en/sk `export.json`.
- **Branch:** `task/T-094` @ `e348cf5` (merged with main), PR #193 (`Closes #187`).
- **Files outside the area:** `BiatecChatRoot.vue`, `api/core.ts`, `chat/useChat.ts` (`resetConversation`), `activity/model.ts` (`reset()`), `i18n/index.ts`, `styles.css`.
- **Verification:** widget typecheck, build; tests 297 passed + 1 pre-existing failure on main (`chat-conversation.test.ts` i18n check, fixed by T-095); export tests 18/18 incl. bundle and combined-file verification with the evidence-verifier fixture; Contracts 2/2. Review (medium): all realistic findings fixed.
- **Known limitations:** duplicated CSRF/client-key header code between RetentionClient and AgentClient.
- **Unblocks:** T-095 (conflict) can start once this leaves in-progress.

## Review

- 2026-10-20 05:41 · approve · PR #193 merged into main @ 731d342 (recorded by kanban-sync)

## Activity log

- 2026-10-20 07:40 · coordinator · (new) → blocked · proposed by T-060
- 2026-10-20 04:59 · kanban-board · blocked → backlog · blockers done: T-060
- 2026-10-20 05:08 · LukassSoukup · taken · status stays backlog
- 2026-10-20 05:08 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 05:31 · coordinator · in-progress · conflicts_with + T-095 (blockers scan: shared files)
- 2026-10-20 05:41 · coordinator · in-progress → review · branch task/T-094 @ e348cf5, PR #193
- 2026-10-20 05:41 · user · review → done · PR #193 merged into main @ 731d342 (synced by kanban-sync)
