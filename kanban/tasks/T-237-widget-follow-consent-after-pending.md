---
id: T-237
title: "Widget: follow the phone approval after consent_pending (consent stream, result, resume after reload)"
status: in-progress
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

- [ ] With a fake transport: `consent_pending` → the stream is opened once → `consent_status` Pending keeps the code and countdown → `confirm_result` submitted shows the submitted state.
- [ ] Denied, timed out and code-failed results each show their own text and remove the code.
- [ ] A stream error falls back to polling with the stated backoff, and no more than one request is in flight per preview.
- [ ] Dispose, sign-out, reset and Cancel stop the follower (no request after them).
- [ ] A restored `AwaitingApproval` card re-attaches; an expired one shows timed out without a request.
- [ ] The binding code never appears in `localStorage`, `sessionStorage` or console output (asserted in a test).
- [ ] `npx vitest run` and `npx vue-tsc --noEmit -p tsconfig.json` pass in `packages/chat-widget`.

## Implementation notes

- The SSE validator `src/transport/sse.ts` already knows `consent_status`; add `confirm_result`.
- The consent stream is a `POST` that answers `text/event-stream`: use the same fetch-based reader as the chat stream, not `EventSource`.
- Fake timers for every timing test; no real waits.

## Verification

`cd packages/chat-widget && npx vitest run && npx vue-tsc --noEmit -p tsconfig.json`

## Hand-off

Filled in when the task moves to review.

## Review

## Activity log

- 2026-11-12 18:43 · scholtz · (new) → backlog · created from the consent architecture record (AlgorandGoogleDriveAccount issue 23)
- 2026-11-13 13:44 · scholtz · backlog → in-progress · taken and claimed (session-20261113-1344), branch task/T-237
