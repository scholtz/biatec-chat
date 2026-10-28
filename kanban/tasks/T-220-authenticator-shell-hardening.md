---
id: T-220
title: "Authenticator: shell hardening leftovers from the T-214 review"
status: blocked
priority: P2
epic: "Consent & authenticator"
blocked_by: [T-214, T-215, T-216]
area: ["apps/biatec_authenticator/lib/shell/**", "apps/biatec_authenticator/lib/enrollment/enrollment_controller.dart", "apps/biatec_authenticator/lib/enrollment/observable_enrollment_store.dart", "apps/biatec_authenticator/lib/push/push_controller.dart", "apps/biatec_authenticator/lib/main.dart", "apps/biatec_authenticator/test/shell/**"]
parallel: no
conflicts_with: [T-222, T-225]
issue: "#357"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-24
updated: 2026-10-24
---

# T-220 - Authenticator: shell hardening leftovers from the T-214 review

## Context

The T-214 pull request (#356) went through nine rounds of code review. What was still open after the ninth is narrow edge cases, listed below; none blocks the shell. They are collected here so T-214 could merge.

## Goal

Each item below is fixed with a test, or closed with a reason.

## Scope

- In: the items below.
- Out: the screen redesigns (T-215 to T-219).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-222, T-225: they change the same files under `lib/shell/`. It also waits for T-214, T-215 and T-216.

## Acceptance criteria

- [ ] `EnrollmentController.refresh()` has no guard against two refreshes running: a stale one that read the record before `acknowledgeRecoveryCode` can set the phase back to `activeRecoveryPending` (banner returns, a second code fetch). Serialise refreshes or drop results older than the last `_set`.
- [ ] `ObservableEnrollmentStore.clear()` notifies before `SettingsController.cancelDevice` has cleared the alias and deleted the key, and the gate swaps the shell at once, so the 'This enrollment was cancelled' message is never shown and a very quick re-enrollment could race the key delete. Signal from `cancelDevice` after it finishes (or notify after the alias is cleared) and show why the user landed on enrollment.
- [ ] Sign-out detection is spread over `_check`, `_checkAgain`, `_clearsSeen`, `_attempt`/`_readAttempt` and `EnrollmentController._discarded`. Replace with one monotonic store generation that the controller also reads.
- [ ] Resume after 30 s or more reloads the showing Settings, Devices or Clients screen under a new key and drops unsaved input; keep the screen while a field is being edited or a request is busy.
- [ ] `ShellController.select(sameTab)` and `showApprovals()` use `popUntil`, which bypasses `PopScope`, so a card in the middle of a decision can be closed by the bar or the snackbar action. Use `maybePop` once `ApprovalScreen` guards its busy state (after T-216).
- [ ] `ApprovalScreen` redraws its countdown from a `Timer.periodic` that `TickerMode` does not stop, so a card in a hidden tab keeps rebuilding every second (after T-216).
- [ ] The mock strip shows only inside the shell. Show it on the startup, retry and enrollment screens too (after T-215).
- [ ] Push registration is asked for from four places (`main.dart` resume, `AppGate._onPhase`, `PushController.didPush/didPop`, `start`). Pick one owner.
- [ ] `flutter analyze` is clean and `flutter test` passes.

## Implementation notes

Presentation and shell wiring only. Each fix gets a test in `test/shell/`.

## Verification

`cd apps/biatec_authenticator && flutter analyze && flutter test`.

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

Written only by the user (or by `/kanban-review` on the user's behalf).

## Activity log

- 2026-10-24 17:45 · claude · (new) → blocked · created from the last review round of PR #356
