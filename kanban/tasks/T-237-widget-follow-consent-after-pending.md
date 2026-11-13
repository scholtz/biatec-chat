---
id: T-237
title: "Widget: follow the phone approval after consent_pending (consent stream, result, resume after reload)"
status: done
priority: P0
epic: "Consent & authenticator"
blocked_by: []
area: ["packages/chat-widget/src/components/chat/**", "packages/chat-widget/src/transport/**", "packages/chat-widget/src/components/cards/**", "packages/chat-widget/src/i18n/**", "packages/chat-widget/test/**"]
parallel: yes
conflicts_with: []
issue: "#397"
branch: "task/T-237"
owner: "session-20261113-1344"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#biatec-authenticator-and-the-biatec-consent-protocol"
created: 2026-11-12
updated: 2026-11-13
---

# T-237 - Widget: follow the phone approval after consent_pending

## Context

Architecture record: ludovit-scholtz/AlgorandGoogleDriveAccount#23 (phase 1, step 6).

When `POST /chat/previews/{id}/confirm` answers `202 consent_pending`, the widget shows the binding code and "Waiting for approval on your phone" (`useChat.ts` around lines 324 to 327, `ActionPreviewCard.vue`), and then nothing ever happens. The backend only polls Biatec OIDC when one of its consent endpoints is called (`backend/Biatec.Chat.Api/Consent/ConsentEndpoints.cs`):

- `POST /chat/previews/{id}/consent/stream`: SSE, one `consent_status` event per poll and a final `confirm_result` event;
- `POST /chat/previews/{id}/consent`: one poll, `{ consent, result? }`;
- `GET /chat/previews/{id}/consent/prediction`: the card text.

Nothing in `packages/chat-widget/src` calls any of them, and `confirm_result` is not handled. The chat stream never carries `consent_status`. So an approval on the phone is never picked up and nothing is signed. The routes are in `packages/chat-protocol/openapi.json` (lines 430 to 478).

## Goal

After `consent_pending` the widget follows the consent to its end and shows the result, without the user doing anything.

## Scope

- In:
  - On `consent_pending`, open the consent stream for that preview. Apply each `consent_status` to the card (`entry.consent`), and on `confirm_result` apply the same handling the direct confirm response gets (submitted, rejected, failed), reusing that code path, not a copy.
  - If the stream fails or ends without a result while the consent is still pending and unexpired: fall back to the single-poll endpoint with backoff (2 s, 4 s, 8 s, capped at 15 s, plus jitter), and retry the stream once per minute. Stop at `expiresAt` plus 10 seconds and show "timed out".
  - Exactly one follower per preview. Stop it on dispose, on sign-out, on conversation reset, and when the card's Cancel is pressed (the existing cancel path).
  - After a page reload or a session restore, a card that is `AwaitingApproval` with an unexpired consent re-attaches (one single poll first, then the stream).
  - Pause polling while the tab is hidden for more than 30 seconds and poll once when it becomes visible again (the stream is kept; only the fallback polling pauses).
  - Before confirm, call the prediction endpoint when the card is shown and render its text (it already has strings for "may need approval on your phone"); a failure shows nothing.
  - States on the card and in the activity feed for: approved, denied on the phone, wrong code three times, timed out, and "this action needs a phone approval and no phone is set up" (`no_active_device`). English and Slovak.
  - The binding code is shown only while pending, is never written to `localStorage`, logs or analytics, and is removed from memory when the consent ends.
- Out: backend changes (T-238, T-240).

## Parallel work

Parallel work possible: **yes**.

## Acceptance criteria

- [x] With a fake transport: `consent_pending` → the stream is opened once → `consent_status` Pending keeps the code and countdown → `confirm_result` submitted shows the submitted state.
- [x] Denied, timed out and code-failed results each show their own text and remove the code.
- [x] A stream error falls back to polling with the stated backoff, and no more than one request is in flight per preview.
- [x] Dispose, sign-out, reset and Cancel stop the follower (no request after them).
- [x] A restored `AwaitingApproval` card re-attaches; an expired one shows timed out without a request.
- [x] The binding code never appears in `localStorage`, `sessionStorage` or console output (asserted in a test).
- [x] `npx vitest run` and `npx vue-tsc --noEmit -p tsconfig.json` pass in `packages/chat-widget`.

## Implementation notes

- The SSE validator `src/transport/sse.ts` already knows `consent_status`; add `confirm_result`.
- The consent stream is a `POST` that answers `text/event-stream`: use the same fetch-based reader as the chat stream, not `EventSource`.
- Fake timers for every timing test; no real waits.

## Verification

`cd packages/chat-widget && npx vitest run && npx vue-tsc --noEmit -p tsconfig.json`

## Hand-off

**Branch:** `task/T-237` @ 4b7afb1. **PR:** see the task's GitHub issue (#397).

**What changed** (all in `packages/chat-widget`):
- `src/components/chat/consent.ts` (new): `ConsentClient` for `POST .../consent` (one poll), `POST .../consent/stream` (fetch-based SSE reader with named events) and `GET .../consent/prediction`. 401, 404 and 409 are final, any other failure is retried.
- `src/transport/sse.ts`: `SseParser.pushFrames` keeps the `event:` name; `readNamedEvents`. `push()` is unchanged for the chat stream.
- `useChat.ts`: one follower per preview. Stream first; on failure or early end, single polls at 2, 4, 8, then 15 s (up to 20 percent jitter, never past the consent's deadline) and a stream retry every minute; stops at the consent's expiry plus 10 s with "timed out". Pauses fallback polling after the tab was hidden for more than 30 s and polls once when visible. Stops on dispose, reset (sign-out), Cancel (resumed if the cancel fails). A card found `AwaitingApproval` (a replayed `preview_card`) re-attaches with one poll first. The confirm outcome goes through the same `settleResult` as a direct confirm. A new consent round is followed, at most 3 per preview. The binding code is removed from the card the moment the consent leaves Pending, also for stream events from chat.
- Prediction: loaded once when a card is first shown, rendered in place of the generic "approval expected" line; a failure shows nothing.
- Texts (en, sk) in a new `consent` section of `chat.json`: denied, timed out, wrong code, no phone set up, daily cap, preview expired, resume failed, receipt missing, cannot be followed.
- `core.ts`: real `ConsentClient` only for the real backend; injected test services opt in with their own `consent`. `onEvent` feeds consent and preview changes to the Activity timeline.

**Verification:** `npx vitest run` 474 passed, 5 skipped (35 new tests in `test/consent-follow.test.ts`); `npx vue-tsc --noEmit -p tsconfig.json` shows only the three `playground/dex` errors that exist on `main` too (the package is not built).

**Open points for the reviewer:**
- **Reasons by code need a backend field.** `ConfirmResponse` (`backend/Biatec.Chat.Api/Chat/Confirm/ConfirmEndpoints.cs`) does not carry `ConfirmResult.ErrorCode`, so today the widget cannot tell `no_active_device` or `code_failed` from any other failure: it shows the denied text for a Denied consent and the backend's message otherwise. The widget already reads `errorCode` when it is present and maps `no_active_device`, `no_device_daily_cap`, `code_failed`, `preview_expired`, `resume_failed`, `receipt_missing`. T-240 (backend, "map each to a user-facing code the widget can render") should add `errorCode` to `ConfirmResponse`, and the stream's `ConsentStatus` for a wrong-code denial. Nothing outside T-237's area was changed.
- **Restore after a reload.** The widget keeps no conversation across a reload today; the re-attach runs when the backend replays a `preview_card` in `AwaitingApproval`. Covered by a test with such a card, not against a real replay.
- Approved consent whose outcome never reaches this tab (another tab finished it) is shown as "can no longer be followed", not as timed out.

**Unblocks:** none by itself; T-243 still waits on T-238 and T-240.

## Review

- 2026-11-13 15:10 · approve · PR #408 merged into main @ 22fea54 (recorded by kanban-sync)

## Activity log

- 2026-11-12 18:43 · scholtz · (new) → backlog · created from the consent architecture record (AlgorandGoogleDriveAccount issue 23)
- 2026-11-13 13:44 · scholtz · backlog → in-progress · taken and claimed (session-20261113-1344), branch task/T-237
- 2026-11-13 14:05 · scholtz · in-progress → review · PR for task/T-237
- 2026-11-13 15:10 · user · review → done · PR #408 merged into main @ 22fea54 (synced by kanban-sync)
