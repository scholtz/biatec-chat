---
id: T-060
title: Retention jobs and user erasure
status: blocked
priority: P3
epic: "Evidence"
blocked_by: [T-035, T-036]
area: ["backend/Biatec.Chat.Api/Retention/**"]
parallel: no
conflicts_with: [T-032, T-038]
issue: "#61"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#session-recording-and-evidence"
created: 2026-10-19
updated: 2026-10-19
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

Parallel work possible: **no**. Don't run it at the same time as T-032, T-038: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] Payloads older than the configured window are removed and the bundle still verifies
- [ ] Erasure blanks exactly the fields listed in the spec and keeps hashes, receipts, tx IDs
- [ ] `PendingRevocation` rows survive erasure
- [ ] Tests with a fake clock

## Verification

dotnet test --filter Retention

## Hand-off

## Review

## Activity log

- 2026-10-19 18:00 · coordinator · (new) → blocked · created in task review: retention and erasure had no task
