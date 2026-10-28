---
id: T-231
title: "Authenticator: brand identity, motion and haptics"
status: blocked
priority: P2
epic: "Consent & authenticator"
blocked_by: [T-219, T-224, T-227, T-228, T-229, T-226]
area: ["apps/biatec_authenticator/assets/**", "apps/biatec_authenticator/lib/theme.dart", "apps/biatec_authenticator/lib/ui/**", "apps/biatec_authenticator/android/app/src/main/res/**", "apps/biatec_authenticator/ios/Runner/Assets.xcassets/**", "apps/biatec_authenticator/pubspec.yaml", "apps/biatec_authenticator/test/ui/**"]
parallel: yes
conflicts_with: []
issue: "#368"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-28
updated: 2026-10-28
---

# T-231 - Authenticator: brand identity, motion and haptics

## Context

Spec: Design. After the screens are redesigned (T-215 to T-219) and the Codes tab exists, the app needs the finish that makes it feel professional: an identity, consistent motion and tactile feedback.

Read first: `PRODUCT-DESCRIPTION.md` (Biatec Authenticator, Authenticator App (Flutter) and its subsections), `AGENTS.md`, `apps/biatec_authenticator/README.md`. Security rules in the spec are requirements, not suggestions: when in doubt choose the stricter reading and say so in the PR.

## Goal

The app has an icon, a splash, a consistent animated and haptic feel that respects accessibility settings, and a design review against the spec's design rules.

## Scope

- In: app icon and adaptive icon, splash and lock screen mark (vector, light and dark), a typographic scale pass and a numeric font style for codes (tabular figures), page and tab transitions, a countdown ring animation, haptics for copy, approve, deny and error through one `Haptics` helper, empty-state illustrations (vector, small), and a pass over every screen for spacing, alignment and states; every animation and haptic off when the system asks for reduced motion or haptics are disabled.
- Out: new features.

## Parallel work

Ordering: T-227, T-228, T-229, T-226 change files this task also edits (MainActivity, the manifests, Info.plist or pubspec.yaml), so they go first.

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Icon and splash exist for Android (adaptive, monochrome) and iOS and show the brand in light and dark.
- [ ] Codes use tabular figures so they do not jitter as digits change.
- [ ] A `Haptics` helper is the only place that triggers feedback, and it does nothing when reduce-motion is on (test).
- [ ] Transitions and the countdown ring are skipped under reduce-motion (test), and nothing animates while the app is locked.
- [ ] A design review checklist (spacing scale, 48 dp targets, contrast 4.5:1 for text, states present on each screen) is in the PR with every screen ticked.
- [ ] `flutter analyze` is clean and `flutter test` passes in `apps/biatec_authenticator`, with new tests for every behaviour above (the failing case first for each security rule).

## Implementation notes

Write the failing test first for every security rule. Never log, print or put in an exception message any secret, code, token or key. Use platform cryptography, not your own. Keep widget keys that existing tests use, or update those tests in the same change.

## Verification

`cd apps/biatec_authenticator && flutter analyze && flutter test`; for native work also build the platform that changed (`flutter build windows` proves only the Dart side; say in the hand-off what could not be run on a device).

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

Written only by the user (or by `/kanban-review` on the user's behalf).

## Activity log

- 2026-10-28 09:00 · claude · (new) → blocked · created from the request to add two-factor codes, a biometric lock and industry-standard security
- 2026-10-28 09:42 · kanban-board · backlog → blocked · waiting on: T-219, T-224
