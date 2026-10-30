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

The T-214 pull request (#356) went through 19 rounds of code review. Rounds 10 to 18 fixed what was listed here before (sign-out in the controller, reload in place, cards never closed by the bar or the notice, one tracker of open cards, store-level handling of failed writes and clears). The user capped the reviews after round 19; what that round still found is listed below. All of it is about storage that fails or hangs, plus small cleanups. They are collected here so T-214 could merge.

## Goal

Each item below is fixed with a test, or closed with a reason.

## Scope

- In: the items below.
- Out: the screen redesigns (T-215 to T-219).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-222, T-225: they change the same files under `lib/shell/`. It also waits for T-214, T-215 and T-216.

## Acceptance criteria

- [ ] **Store design (root cause).** `ObservableEnrollmentStore` grew six pieces of state over the review rounds (`_unsaved`, `_lastSaveTry`, `_clearFailed`, `_clearQueued`, `_writes`/`_lastWritten`, `_clearCount`). Replace them with one in-memory authoritative state (current record or signed out), loaded once, with the disk as a write-behind. That also covers the next three items.
- [ ] A write or clear that hangs in the keystore holds up every later write: `write()` awaits the serial queue, so `enroll()` can stay in `enrolling` behind a hung clear. Writes should complete against the in-memory state and persist behind it.
- [ ] `read()` lets an exception from the inner read escape even when a clear or a write happened meanwhile and the right answer is known ("not enrolled", or the record just written).
- [ ] `_saveLater` copies the body of `write()`'s serial step; one `_persist(record)` step for both.
- [ ] `SettingsController.cancelDevice` swallows a failed `enrollment.clear()` and deletes the key, which is only safe when the store is the observable one. Type `SettingsServices.enrollment`/`SettingsController.enrollment` as `ObservableEnrollmentStore`, like `ApprovalServices` and `EnrollmentController` now are.
- [ ] The gate leaves "Could not read this device" only when a later refresh finds an enrollment; a later successful read that finds none (a device never enrolled) leaves the error up until Retry. Have the gate re-check `readFailed` after a resume refresh.
- [ ] `PendingScreen._onActive` refreshes even while the app is in the background (`foreground` false).
- [ ] While covered, the list counts fast ticks to poll at the slow rate; restart the timer at `backgroundEvery` when the list becomes covered instead (needs a route-change signal).
- [ ] Push registration is asked for from several places (`main.dart` resume, `AppGate._onPhase`, `PushController.didPush/didPop` through every tab's navigator, `start`). Pick one owner.
- [ ] Carried over: `ApprovalScreen` redraws its countdown from a `Timer.periodic` that `TickerMode` does not stop, so a card in a hidden tab keeps rebuilding every second (after T-216).
- [ ] Carried over: the mock strip shows only inside the shell. Show it on the startup, retry and enrollment screens too (after T-215).
- [ ] (From the T-227 review, PR #370.) Android 10 and 11 let a partially obscured touch reach the Approve button (an overlay painting a fake amount next to it). Gating every such touch window-wide locks the whole app when a chat head or screen filter is on screen, so check it on the approve action alone, through a method channel to Dart (`FLAG_WINDOW_IS_PARTIALLY_OBSCURED` of the tap's event).
- [ ] (From the T-227 review, PR #370.) The authenticator CI job builds an obfuscated, signed release APK on every pull request. Add a path filter (or a changed-files guard) so backend-only and widget-only changes do not wait for it.
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
- 2026-10-29 · claude · blocked · criteria replaced with what review round 19 of PR #356 left open (rounds 10–18 fixed the earlier list); reviews capped there on the user's decision
