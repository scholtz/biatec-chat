---
id: T-060
title: Retention jobs and user erasure
status: review
priority: P3
epic: "Evidence"
blocked_by: [T-035, T-036]
area: ["backend/Biatec.Chat.Api/Retention/**"]
parallel: no
conflicts_with: [T-032]
issue: "#61"
branch: "task/T-060"
owner: "worker-T-060"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#session-recording-and-evidence"
created: 2026-10-19
updated: 2026-10-20
---

# T-060 · Retention jobs and user erasure

## Context

"Retention and deletion work as one rule: message payloads (and their salts) are kept 90 days, then removed; on a
signed-in user's request they are removed immediately, `oidcSub` is nulled on the conversation, and
`ActionPreview.canonicalJson`, `ActionStep.unsignedGroup`, `limitsRequest` and `senderAddress` are blanked (group
hashes, transaction IDs, and receipts stay)." Anonymous conversations are removed by 30-day retention only.
`PendingRevocation` rows are never deleted until revocation succeeds or the token expires. Compliance posture
requires user export and deletion.

## Goal

Configurable retention and a user-requested erasure that keep every bundle verifiable.

## Scope

- In: Retention options (defaults 90 days payloads, 30 days anonymous, 7 years fund-moving chain and receipts), a
  background job applying them, an erasure endpoint for the signed-in user (CSRF-guarded), a list of the user's own
  conversations for the widget's export view.
- Out: Soft-cap and safe-cap row pruning (T-025), payload-deletion primitive (T-034).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-032: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [x] Payloads older than the configured window are removed and the bundle still verifies
- [x] Erasure blanks exactly the fields listed in the spec and keeps hashes, receipts, tx IDs
- [x] `PendingRevocation` rows survive erasure
- [x] Tests with a fake clock

## Verification

dotnet test --filter Retention

## Hand-off

- **Summary:** `RetentionOptions` (payloads 90 d, anonymous 30 d, records 2557 d) and an hourly `RetentionHostedService` (first run after `StartupDelay`) applying per-row rules in batches within a run budget: evidence payloads/salts by age; messages by their own `CreatedAt` (new column); preview/step payload columns by their own expiry; anonymous conversations (never erased ones) removed after 30 d keeping the chain without payloads; everything incl. orphan chains after 7 y (SignedAt-prefiltered). `POST /session/erase` (signed-in, CSRF) blanks the listed fields, marks conversations `ErasedAt`, nulls oidcSub, moves live sessions to a fresh conversation (returned `conversationId`), refuses with 409 while an action is in flight (same in-flight rule as SessionLiveness). `GET /session/conversations` and `GET /session/conversations/{id}/evidence` for the export view. PendingRevocation and cap rows untouched.
- **Branch:** `task/T-060` @ `93ce615` (merged with main), PR #185 (`Closes #61`).
- **Files outside the area:** `Core/Data/Entities.cs` (`Message.CreatedAt`, `Conversation.ErasedAt`), `ChatDbContext.cs` (defaults, indexes), migration `RetentionTimestamps`, `ConversationOrchestrator.cs` (sets CreatedAt), `Program.cs`, `appsettings.json`.
- **Verification:** Retention 7 passed; full backend on current main 1152 passed, 1 skipped, 0 failed. Bundles verify before and after retention and erasure. Review (medium): all realistic findings fixed.
- **Known limitations:** a pending elevate popup started before an erase fails (retry); a turn streaming at the moment of erase can still write to the old conversation (cleared by the 90-day rule); the per-conversation export duplicates the export endpoint's body.
- **Follow-up tasks created:** T-094 (widget export view and erase button).
- **Unblocks:** T-094.

## Review

## Activity log

- 2026-10-19 18:00 · coordinator · (new) → blocked · created in task review: retention and erasure had no task
- 2026-10-20 01:09 · LukassSoukup · blocked · parallel analysis: dropped T-038 (done)
- 2026-10-20 03:14 · kanban-board · blocked → backlog · blockers done: T-035, T-036
- 2026-10-20 04:17 · LukassSoukup · taken · status stays backlog
- 2026-10-20 04:17 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 04:59 · coordinator · in-progress → review · branch task/T-060 @ 93ce615, PR #185
