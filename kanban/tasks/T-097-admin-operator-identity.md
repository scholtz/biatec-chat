---
id: T-097
title: "Admin: per-operator identity and evidence for admin changes"
status: done
priority: P3
epic: "Sessions & auth"
blocked_by: [T-086]
area: ["backend/Biatec.Chat.Api/Admin/**", "backend/Biatec.Chat.Api/Onboarding/OperatorAuth.cs", "docs/required-changes-biatec-oidc.md"]
parallel: no
conflicts_with: [T-096]
issue: "#192"
branch: "task/T-097"
owner: "worker-T-097"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-097 · Admin: per-operator identity and evidence for admin changes

## Context

From T-086: all operators share one bearer key (`Onboarding:OperatorKey`), so admin audit lines record the IP and reason but not who made a change, and admin changes aren't in a signed audit chain.

## Goal

Each admin change is attributable to a named operator and recorded verifiably.

## Scope

- In: an operator identity (Biatec OIDC operator claim/group if available — record the needed change in docs/required-changes-biatec-oidc.md with a fallback — or named keys from a secret); admin changes appended to a signed evidence chain.
- Out: an operator UI.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-096: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [x] Each admin change records the operator identity (test)
- [x] Admin changes are in a verifiable chain (test)

## Verification

`dotnet test --filter Admin`, then the full backend suite.

## Hand-off

- **Summary:** named operator keys (`Admin:Operators:<name>:KeyHash`, SHA-256 only, keys from a secret; decoded once by `OperatorKeyring`; constant-time match; minimum key length for named and legacy keys; startup errors name the entry and rule). The old `Onboarding:OperatorKey` is the deprecated operator `legacy`. Every committed admin change (policy PATCH, KB upload/delete in the same transaction; KB reload best-effort with CancellationToken.None) is an `admin_change` entry in one signed chain (`00000000-0000-0000-0000-00000000ad01`) with operator, action, target, reason, before/after — never keys, secrets or page content. `GET /onboarding/operator/evidence?fromSeq=&limit=` returns a page in the conversation bundle format (jwksUrl) with a verdict over the same snapshot, `Cache-Control: no-store`. The admin chain is exempt from the 90-day payload wipe and keeps the 7-year record window (operator and tenant data, not end-user data). docs/required-changes-biatec-oidc.md row 23: an operator claim at Biatec OIDC, with this fallback.
- **Branch:** `task/T-097` @ `6175f8f` (merged with main), PR #197 (`Closes #192`).
- **Files outside the area:** `Retention/RetentionService.cs` (admin chain filter), `RetentionOptions.cs` (comment), `docs/admin-api.md`, tests.
- **Verification:** Admin + Retention + Onboarding 102 passed; full backend 1273 passed, 1 skipped, 0 failed. Review (medium): all realistic findings fixed.
- **Known limitations:** the offline evidence-verifier checks a page only from seq 1 (join later pages first; documented, fine while the chain is small); CleanReason computed in three places.
- **Follow-up tasks created:** T-098 (record operator tenant approvals in the admin chain).
- **Unblocks:** T-098.

## Review

- 2026-10-20 06:59 · approve · PR #197 merged into main @ b273055 (recorded by kanban-sync)

## Activity log

- 2026-10-20 05:31 · coordinator · (new) → backlog · proposed by a worker / found during verification
- 2026-10-20 05:31 · coordinator · backlog · conflicts_with + T-096 (blockers scan: shared files)
- 2026-10-20 06:07 · LukassSoukup · taken · status stays backlog
- 2026-10-20 06:07 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 06:59 · coordinator · in-progress → review · branch task/T-097 @ 6175f8f, PR #197
- 2026-10-20 06:59 · user · review → done · PR #197 merged into main @ b273055 (synced by kanban-sync)
