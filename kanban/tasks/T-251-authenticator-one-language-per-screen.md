---
id: T-251
title: "Authenticator: every text in the user's language (English and Slovak), no mixed screens"
status: backlog
priority: P2
epic: "Consent & authenticator"
blocked_by: []
area: ["apps/biatec_authenticator/lib/**", "apps/biatec_authenticator/test/**", "apps/biatec_authenticator/pubspec.yaml", "apps/biatec_authenticator/l10n.yaml", "apps/biatec_authenticator/README.md"]
parallel: no
conflicts_with: [T-249, T-250]
issue: "#424"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-11-16
updated: 2026-11-16
---

# T-251 · Authenticator: every text in the user's language

## Context

Reported by the user on 2026-11-16 with a screenshot of the connect step on a Slovak Windows: the title bar ("Set up
this device"), the progress line ("Step 2 of 5 · Connect") and the heading ("Connect this device to your account")
are English, while the introduction, the error box and the button are Slovak.

Why: only four groups of texts are translated, each with its own hand-written class and an English and a Slovak map
(`EnrollStrings` 84 keys, `TrustStrings` 170, `ChannelStrings` 32, `RpStrings` 118). The comment in
`enrollment_strings.dart` says it: "the rest of the app is English only for now". The rest is about 130 `Text('…')`
literals in 26 files, plus labels, tooltips and hints. The largest: `settings_screen.dart`, `lock_screens.dart`,
`add_account_screen.dart`, `codes_screen.dart`, `backup_screen.dart`, `enrollment_screen.dart`.

Two more sources of mixed language:

- Reasons inside translated sentences stay English, for example "Prihlásenie zlyhalo: The provider refused the token
  answer (401, invalid_client)." (`LoginFailed` carries English text).
- The app does not load `flutter_localizations`, so the texts Flutter itself draws (dialog buttons, date and time
  formats, screen reader labels) are English.

## Goal

On a device set to Slovak every text of the app is Slovak; on any other language every text is English. A new text
cannot be added in one language only.

## Implementation plan

1. **One mechanism.** Flutter's own `gen-l10n`: `lib/l10n/app_en.arb` and `app_sk.arb`, `l10n.yaml`,
   `flutter_localizations` in `pubspec.yaml`, `localizationsDelegates` and `supportedLocales` on both `MaterialApp`s
   in `main.dart`. A key missing from the Slovak file fails the build (`untranslated-messages-file` checked in a
   test). Plurals and placeholders use the ARB format.
2. **Move the four classes.** `EnrollStrings`, `TrustStrings`, `ChannelStrings` and `RpStrings` keep their names and
   getters and read from the generated class, so their callers and tests do not change in this step; then the
   callers move over and the classes go. Code with no `BuildContext` (controllers) returns a key or a typed error,
   and the screen turns it into text.
3. **Translate the rest**, screen by screen, in this order: enrollment and onboarding, lock, approvals and history,
   codes and backup, settings, devices and clients, privacy, shell and banners.
4. **Errors.** `LoginFailed`, `ConfigException` and the enrollment problems carry a kind and its values (a status,
   an error code), not an English sentence. Codes from the server stay as they are (`invalid_client`).
5. **A guard.** A test that reads `lib/**` and fails on a string literal handed to `Text`, `label`, `title`,
   `tooltip`, `hintText`, `labelText` or `semanticLabel`, with a short allow-list (brand names, codes).
6. **Language choice.** The system language, as today. An override in the settings is out of scope.
7. **Review of the Slovak texts** by a native speaker (the user) before the task goes to review: the ARB file is
   the one place to read.

## Scope

- In: the seven steps above, for the Flutter app on every platform.
- Out: more languages; a language switch in the settings; the texts of the native prompts that the system words
  itself; server texts.

## Parallel work

Parallel work possible: **no**. It touches most files under `lib/**`, so it conflicts with T-249 and T-250. Best
done after them, or in a quiet period.

## Acceptance criteria

- [ ] On a Slovak system no English sentence is on any screen of the onboarding, the lock, the approvals, the codes,
      the settings, the devices and the privacy screens (checked with screenshots of each).
- [ ] On an English system no Slovak text appears.
- [ ] A key that is missing in one language fails `flutter test`.
- [ ] The guard test fails when a literal is put into a `Text`.
- [ ] Flutter's own texts (dialog buttons, dates) follow the language.
- [ ] `flutter test` passes; `flutter build windows` builds.

## Verification

    cd apps/biatec_authenticator
    flutter test
    flutter build windows

Then run the Windows build on a system set to Slovak and to English and walk the screens.

## Hand-off

## Review

## Activity log

- 2026-11-16 14:30 · session-20261116-0900 · (new) → backlog · created at the user's request (plan only); conflicts with T-249 and T-250 by file overlap, no ordering need
