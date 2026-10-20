---
id: T-075
title: Backend preview cancel endpoint
status: done
priority: P2
epic: "Wallet tools"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Chat/Cancel/**", "backend/Biatec.Chat.Tests/Cancel/**"]
parallel: yes
conflicts_with: []
issue: "#142"
branch: "task/T-075"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#user-experience"
created: 2026-10-20
updated: 2026-10-20
---

# T-075 · Backend preview cancel endpoint

## Context

Spec, User Experience 5: the action preview card has "Review, Confirm and Cancel", and a consent-pending card has a
Cancel button. User Experience 8: "**Revoke all access** cancels every open preview and signs out." The data model
lists `cancelled` as an `ActionPreview` status, and `ActionDecisionRequest` ("Body of POST /actions/{previewId}/confirm
and /cancel") is in the contract, but the backend maps only `POST /chat/previews/{previewId}/confirm` and `/reconcile`.
There is no cancel route.

T-066 (merged, PR #141) already calls `POST /chat/previews/{previewId}/cancel` with `{ "groupHash": "…" }` from the widget's
Revoke all access (`packages/chat-widget/src/components/agent/api.ts`). Until this endpoint exists the call fails and
the widget relies on sign-out to end the session.

## Goal

The user can cancel an open preview, and Revoke all access really cancels the previews it lists.

## Scope

- In: `POST /chat/previews/{previewId:guid}/cancel` with `ActionDecisionRequest`, behind the global CSRF guard, only for an
  authenticated session of the preview's own conversation; the group hash must match the stored step. Moves the preview and
  its open steps to `Cancelled`, cancels a pending CIBA consent request for it, records a `preview_cancelled` evidence
  entry, and answers 200 with the new status (idempotent for an already-cancelled preview; 409 for a completed, failed or
  expired one; 404 for an unknown one).
- In: allowed while the agent is paused or analyze-only (cancelling never needs permission).
- In: OpenAPI entry, so `packages/chat-protocol` regenerates with the route.
- Out: widget Cancel button wiring on the card (T-076); soft-cap reservation release (T-025 hooks into it once both exist).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Cancel of an open preview returns 200 and the preview and its open steps are `Cancelled`; confirm afterwards is refused
- [ ] Wrong group hash → 409; anonymous or another conversation's session → 401; unknown preview → 404
- [ ] Cancel works while paused and in analyze-only mode
- [ ] A pending consent request for the preview is cancelled, and an evidence entry is written
- [ ] The route is in the generated chat-protocol paths

## Verification

dotnet test backend (new tests under `backend/Biatec.Chat.Tests/Cancel/`); `pnpm --filter @biatec/chat-protocol generate` shows the path.

## Hand-off

## Review

- 2026-10-20 03:21 · approve · PR #158 merged into main @ bf2f393 (recorded by kanban-sync)

## Activity log

- 2026-10-20 02:10 · session-20261020-0154 · (new) → backlog · follow-up from T-066: the widget's Revoke all access calls a cancel route the backend lacks
- 2026-10-20 02:19 · scholtz · taken · status stays backlog
- 2026-10-20 02:19 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-20 03:21 · user · in-progress → done · PR #158 merged into main @ bf2f393 (synced by kanban-sync)
