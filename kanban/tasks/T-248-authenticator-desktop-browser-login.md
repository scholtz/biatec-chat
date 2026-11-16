---
id: T-248
title: "Authenticator: sign in with the system browser on desktop, stage defaults for the Windows build, guide for another provider"
status: done
priority: P1
epic: "Consent & authenticator"
blocked_by: []
area: ["apps/biatec_authenticator/lib/enrollment/**", "apps/biatec_authenticator/lib/config.dart", "apps/biatec_authenticator/test/enrollment/**", "apps/biatec_authenticator/test/config_test.dart", "apps/biatec_authenticator/README.md", "docs/authenticator-provider-setup.md", "PRODUCT-DESCRIPTION.md"]
parallel: yes
conflicts_with: []
issue: "#420"
branch: "task/T-248"
owner: "session-20261116-0900"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-11-16
updated: 2026-11-16
---

# T-248 · Authenticator: sign in with the system browser on desktop

## Context

Asked for by the user on 2026-11-16. Stage Biatec OIDC now has the public client `biatec-authenticator-stage-app`
(no secret, redirect `http://localhost:42815`, scopes `openid profile email`). The Windows build of the authenticator
still shows the field for a pasted login token in the step "Connect this device to your account" (debug) or says that
the sign-in is unavailable (release): `flutter_appauth` has no Windows implementation, and the in-app login of T-242 is
behind `IN_APP_LOGIN`, which also turns on server features real Biatec OIDC does not have (`enroll-device`, activation
by another device, the recovery code).

Real Biatec OIDC (`ludovit-scholtz/AlgorandGoogleDriveAccount`): `POST /devices` accepts any ordinary user access token
of a configured client; the authorize endpoint requires PKCE for a public client, matches the redirect exactly
(scheme, host, port, path) and drops scopes it does not know.

## Goal

On Windows the connect step has a "Sign in" button that opens the system browser, and the access token of that login
enrolls the device; nothing is pasted. A plain `flutter build windows` works against stage with no defines. Another
provider can follow one document to point a build at its own OIDC.

## Scope

- In: a loopback login client (RFC 8252: authorization code, PKCE S256, `state`, `nonce`, listener on the loopback
  interface only, one request, time limit); a `BROWSER_LOGIN` setting separate from `IN_APP_LOGIN`; Windows defaults
  (stage URL, client id, redirect); the enrollment screen; tests; `docs/authenticator-provider-setup.md`; README;
  PRODUCT-DESCRIPTION.
- Out: choosing the provider at run time (T-249); changes in Biatec OIDC; the mobile login (unchanged, T-242).

## Parallel work

Parallel work possible: **yes**. T-249 changes the same files, and it is blocked by this task, so the two never run
together.

## Acceptance criteria

- [x] A release Windows build with no defines resolves to `https://stage.oidc.biatec.io`, client
      `biatec-authenticator-stage-app`, redirect `http://localhost:42815`, browser login on (unit test).
- [x] The connect step shows the sign-in button and no token field in that configuration (widget test).
- [x] The loopback client sends `response_type=code`, `code_challenge_method=S256`, a `state` and a `nonce`, accepts
      the code only with the right `state` on the right path, redeems it with the verifier, checks the `nonce` of the
      ID token, and returns the access token (unit tests against a local fake server).
- [x] A wrong `state`, an `error` answer, a timeout, a busy port and a discovery document that names another issuer or
      a non-https endpoint each fail without a token (unit tests).
- [x] The listener binds loopback addresses only.
- [x] A release build on a phone still refuses a redirect that is not an https app link.
- [x] `docs/authenticator-provider-setup.md` lists what a provider registers and every build setting.
- [ ] Sign-in on a real Windows build against stage (manual; needs a person at the browser).

## Verification

    cd apps/biatec_authenticator
    flutter test
    flutter build windows

## Hand-off

- **Summary:** A desktop build signs in with the system browser (`LoopbackAuthClient`: authorization code, PKCE S256,
  `state`, `nonce`, a listener on the loopback interface only, 5 minute limit, "Stop waiting"). `BROWSER_LOGIN` is a
  switch of its own for desktop builds; a phone build refuses it without `IN_APP_LOGIN` (protocol rule 7: a phone
  enrolls only on a login the server proves fresh). A plain `flutter build windows` resolves to stage, client
  `biatec-authenticator-stage-app`, redirect `http://localhost:42815`. `docs/authenticator-provider-setup.md` is the
  guide for another provider. Branch `task/T-248` @ `aa29834`, PR #422.
- **Files changed:** `apps/biatec_authenticator/lib/config.dart`, `lib/enrollment/{auth_client,loopback_auth_client,
  enrollment_controller,enrollment_screen,enrollment_strings}.dart`, `lib/enrollment/widgets/enrollment_problem.dart`,
  `test/enrollment/{loopback_auth_client,desktop_login}_test.dart` (new), two test files (`extends AuthClient`),
  `README.md`, `docs/authenticator-provider-setup.md` (new), `PRODUCT-DESCRIPTION.md`. **Outside the task's area:**
  `docs/required-changes-biatec-oidc.md` (AGENTS.md requires the missing server behaviour to be recorded there).
- **How it was verified:** `flutter test` in `apps/biatec_authenticator`: 1611 passed, 26 skipped. `flutter build
  windows`: builds (when it fails on missing `windows/flutter/ephemeral/cpp_client_wrapper/*.cc`, run it again; those
  are Flutter's own generated files). `curl` of stage's discovery document matches what the client checks, and
  stage's `/authorize` answers `302` to its login page for the client, redirect and PKCE the app sends. Four rounds
  of `/code-review high`; every round had findings, the ones below were left.
- **Not verified:** the last criterion. A sign-in in a real browser against stage needs a person to log in; nobody
  did. The token answer of stage (ID token with `nonce`, without `iss`) was read from the Biatec OIDC source, not
  observed.
- **Accepted limits (review findings not changed):**
  1. A desktop build signs in and then cannot enroll: there is no key store for Windows, Linux or macOS. The app says
     so after the sign-in (not before it, so the sign-in the user asked for can be exercised). T-250.
  2. Real Biatec OIDC ignores `prompt=login` and `max_age=0` and sends no `auth_time`, so the desktop login can come
     from an existing browser session. The text makes no promise of a fresh login there. T-250 must not let a desktop
     device enroll on such a login.
  3. A desktop build cannot sign in against the mock (its address is the Android emulator alias, and it does not
     register the loopback redirect). The desktop login is tested with a fake provider in Dart only.
  4. The default redirect is `http://localhost:42815` (what stage has registered), although RFC 8252 prefers
     `127.0.0.1`; the guide says to prefer `127.0.0.1` for a new client.
  5. One fixed port. A machine that reserves it cannot sign in without another build and registration.
  6. `_same` (constant-time compare) and the unpadded base64url helper exist in three files.
- **Follow-up tasks created:** T-249 (provider chosen at run time), T-250 (Windows key store).
- **Unblocks:** T-249, T-250.
- **Open questions:** (1) Add `http://127.0.0.1:42815` to the stage client and make it the default? (2) The stage
  client also has a Postman callback, with a leading space in its value (`" https://oauth.pstmn.io/v1/callback"`);
  it is an extra place a code can be sent and should not be on a production client.

## Review

- 2026-11-16 11:46 · approve · PR #422 merged into main @ 54b8b66 (recorded by kanban-sync)

## Activity log

- 2026-11-16 09:00 · session-20261116-0900 · (new) → in-progress · created at the user's request and claimed; T-249 (same files) is blocked by it
- 2026-11-16 12:10 · session-20261116-0900 · in-progress → review · PR #422, four review rounds; hand-off written
- 2026-11-16 11:46 · user · review → done · PR #422 merged into main @ 54b8b66 (synced by kanban-sync)
