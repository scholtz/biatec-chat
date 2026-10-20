---
id: T-072
title: Fix failing authenticator enrollment tests
status: done
priority: P2
epic: "Authenticator"
blocked_by: [T-051, T-071]
area: ["apps/biatec_authenticator/lib/enrollment/**", "apps/biatec_authenticator/test/enrollment/**", ".github/workflows/ci.yml"]
parallel: yes
conflicts_with: []
issue: "#129"
branch: "task/T-072"
owner: "worker-T-072"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#biatec-authenticator"
created: 2026-10-20
updated: 2026-10-20
---

# T-072 · Fix failing authenticator enrollment tests

## Context

Found while building CI (T-071): on `main`, `flutter test` in `apps/biatec_authenticator` fails two tests in
`test/enrollment/enrollment_test.dart`:

- `screen shows the recovery code after activation` (expects no "AAAA-BBBB-CCCC" text, finds one)
- `state machine approved by an active device is active at once`

`ci.yml` runs the authenticator job with `continue-on-error: true` until this is fixed.

## Goal

`flutter test` passes, and the authenticator CI job is blocking again.

## Scope

- In: decide whether the tests or the enrollment code are wrong (spec: recovery code is shown only after a device
  became active, never during enrollment); fix; remove `continue-on-error` from the `authenticator` job in
  `.github/workflows/ci.yml`.
- Out: anything else in the authenticator.

## Parallel work

Parallel work possible: **yes**. It edits `ci.yml` that T-071 creates, so it waits for T-071 (blocked_by).

## Acceptance criteria

- [x] `cd apps/biatec_authenticator && flutter test` passes.
- [x] `continue-on-error` is gone from the `authenticator` job.

## Verification

`flutter analyze && flutter test` in `apps/biatec_authenticator`.

## Hand-off

- **Summary:** the enrollment tests were already fixed on main by T-071 (9690a2c): the tests were wrong, the code right (spec `PRODUCT-DESCRIPTION.md:489`: the recovery code is "shown once only after a device has become active (never during enrollment)"); 9690a2c also removed `continue-on-error`. The now-blocking authenticator job still failed at `flutter analyze`; this task removes the two flagged imports (`lib/main.dart` routes.dart, `lib/security/settings_controller.dart` dart:typed_data).
- **Branch:** `task/T-072` @ `619ceb8`, PR #178 (`Closes #129`).
- **Files changed:** outside the area, at the coordinator's request: `apps/biatec_authenticator/lib/main.dart`, `apps/biatec_authenticator/lib/security/settings_controller.dart` (one import each).
- **How it was verified:** coordinator re-ran `flutter analyze` (No issues found) and `flutter test` (114 passed, 8 skipped: MOCK_OIDC_URL contract tests).
- **Follow-up tasks created:** none.
- **Unblocks:** no task is blocked by T-072.
- **Open questions:** none.

## Review

- 2026-10-20 04:16 · approve · PR #178 merged into main @ 0986467 (recorded by kanban-sync)

## Activity log

Append-only. Format: `- YYYY-MM-DD HH:MM · <actor> · <from> → <to> · <note>`

- 2026-10-20 00:47 · session-20261020-claude · (new) → backlog · follow-up from T-071 (pre-existing Flutter test failures on main)
- 2026-10-20 00:47 · kanban-board · backlog → blocked · waiting on: T-071
- 2026-10-20 03:49 · kanban-board · blocked → backlog · blockers done: T-051, T-071
- 2026-10-20 04:06 · LukassSoukup · taken · status stays backlog
- 2026-10-20 04:06 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 04:16 · coordinator · in-progress → review · branch task/T-072 @ 619ceb8, PR #178
- 2026-10-20 04:16 · user · review → done · PR #178 merged into main @ 0986467 (synced by kanban-sync)
