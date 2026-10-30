---
id: T-215
title: "Authenticator: redesign enrollment into a guided onboarding"
status: backlog
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-214]
area: ["apps/biatec_authenticator/lib/enrollment/enrollment_screen.dart", "apps/biatec_authenticator/lib/enrollment/widgets/**", "apps/biatec_authenticator/test/enrollment/**"]
parallel: yes
conflicts_with: []
issue: "#351"
branch: ""
owner: ""
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-24
updated: 2026-10-29
---

# T-215 - Authenticator: redesign enrollment into a guided onboarding

## Context

Enrollment is the first thing every user sees. Today it is a form-like screen. Spec: enrollment, device key in the hardware keystore, activation delay and a recovery code. The controller and client (`enrollment_controller.dart`, `oidc_enrollment_client.dart`) already implement the flow and stay as they are.

Read first: `PRODUCT-DESCRIPTION.md` (Authenticator App (Flutter)), `AGENTS.md`, `apps/biatec_authenticator/README.md`. The redesign follows current Material 3 and mobile UX practice: bottom navigation with icons and labels for top-level destinations, one primary action per screen, clear states (loading, empty, error, offline), accessible by default.

## Goal

Enrollment is a guided, accessible, step-based onboarding that explains what the device is for, shows progress, handles every state and ends with a clear recovery-code step.

## Scope

- In: a welcome step (what Biatec Authenticator does and does not hold), a step indicator, the binding or sign-in step, key-protection explanation (hardware or software key shown with the key security level, not by color alone), the activation delay state with a countdown, the recovery-code step (copy, 'I saved it' confirmation, never shown again), error and retry states, and a success state that opens the shell; widget tests for each step.
- Out: changing the enrollment protocol, controller or client; the shell (T-214).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Onboarding shows a progress indicator and the steps welcome, connect, protect the key, recovery code, done.
- [ ] The key protection level (hardware-backed or software) is stated in words with an icon.
- [ ] The activation-delay state shows the remaining time, and the recovery code step requires an explicit confirmation before continuing.
- [ ] Network failure, a refused code and an already-enrolled device each show a specific message with a retry or next action.
- [ ] Existing enrollment controller tests still pass unchanged.
- [ ] Every interactive element is at least 48x48 dp, has a screen-reader label (amounts and names are announced in full), and no state is shown by color alone.
- [ ] Layouts hold at 3x system text scale and in light and dark mode without overflow (widget tests at textScaler 1.0 and 3.0).
- [ ] `flutter analyze` is clean and `flutter test` passes, including new widget tests for every new or redesigned screen state (loading, empty, error, offline, data).

## Implementation notes

Presentation only: services, controllers, clients and the consent protocol stay as they are. Keep widget keys that existing tests use, or update those tests in the same change. Use only packages already in `pubspec.yaml` unless the task says otherwise (Material 3 provides everything needed). Run `flutter analyze` and `flutter test` in `apps/biatec_authenticator`; build with `./build-windows.ps1` to see it.

## Verification

`cd apps/biatec_authenticator && flutter analyze && flutter test`, then `./build-windows.ps1` and a manual look at the changed screens in light and dark mode.

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

Written only by the user (or by `/kanban-review` on the user's behalf).

## Activity log

- 2026-10-24 12:00 · claude · (new) → blocked · created from the authenticator redesign request
- 2026-10-24 17:07 · kanban-board · backlog → blocked · waiting on: T-214
- 2026-10-24 12:05 · scholtz · taken · status stays blocked
- 2026-10-29 16:26 · kanban-board · blocked → backlog · blockers done: T-214
