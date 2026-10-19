---
id: T-052
title: Authenticator approval flow and receipts
status: done
priority: P2
epic: "Consent & authenticator"
blocked_by: [T-051]
area: ["apps/biatec_authenticator/lib/approvals/**"]
parallel: yes
conflicts_with: []
issue: "#53"
branch: "task/T-052"
owner: "session-20261019-2111"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#protocol-rules"
created: 2026-10-19
updated: 2026-10-19
---

# T-052 · Authenticator approval flow and receipts

## Context

Card rendered only from fetched authorization_details; user types binding code (3 attempts); biometric; device key signs canonical receipt; deny is signed too; offline refuses approval.

## Goal

Phone approval that the backend consent flow (T-049) completes with.

## Scope

- In: Pending list fetch, card, code entry, biometric gate, JCS receipt signing, post to mock, deny, history.
- Out: Push (T-053).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [x] Approve flow produces a receipt the mock accepts
- [x] Third wrong code yields code_failed
- [x] Offline mode cannot approve
- [x] Card fields match authorization_details

## Verification

flutter test; manual run with mock + backend

## Hand-off

- Branch `task/T-052` @ 698e7cc, PR #96.
- `apps/biatec_authenticator/lib/approvals/`: pending list (5 s polling), card rendered only from the fetched request
  (rule 3 for unverified clients), binding code entry, biometric gate (`local_auth`, fails closed), RFC 8785 receipt
  signed by the device key with the enrollment attestation, signed deny and code_failed receipts, offline refusal
  (fresh fetch required right before signing), local history with full receipts.
- Outside the area: `lib/enrollment/enrollment_state.dart` and `oidc_enrollment_client.dart` now keep the attestation
  from `POST /devices`; `main.dart`/`routes.dart` wiring; `docs/required-changes-biatec-oidc.md` section 4 now lists the
  authenticator endpoints actually used (`/devices/requests`, `/consent/requests/{id}`, `.../receipt`).
- Verification: `flutter analyze` clean; `flutter test test/approvals test/app_test.dart` 42 passed; contract test against
  the running mock-oidc (it verifies signature and attestation) 3 passed, plus the T-051 contract test.
- Known, not from this task: 2 enrollment tests from T-051 fail when the whole suite runs (also on main; pass alone).
  Native platform folders are still not generated, so `local_auth` (needs `FlutterFragmentActivity`, Face ID usage
  string) and the hardware key were not run on a device.
- **Unblocks:** T-053 (push notifications) once approved.

## Review

- 2026-10-19 21:37 · approve · no need for my approval later if reviews and tests pass; try to fix the failing enrollment tests

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 21:07 · kanban-board · blocked → backlog · blockers done: T-051
- 2026-10-19 21:11 · LukassSoukup · taken · status stays backlog
- 2026-10-19 21:11 · session-20261019-2111 · backlog → in-progress · claimed by LukassSoukup
- 2026-10-19 21:31 · session-20261019-2111 · in-progress → review · PR #96
- 2026-10-19 21:37 · user · review → done · approved, merged @ 7cb106f
