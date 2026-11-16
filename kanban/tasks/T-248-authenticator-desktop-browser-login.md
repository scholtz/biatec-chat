---
id: T-248
title: "Authenticator: sign in with the system browser on desktop, stage defaults for the Windows build, guide for another provider"
status: in-progress
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

- [ ] A release Windows build with no defines resolves to `https://stage.oidc.biatec.io`, client
      `biatec-authenticator-stage-app`, redirect `http://localhost:42815`, browser login on (unit test).
- [ ] The connect step shows the sign-in button and no token field in that configuration (widget test).
- [ ] The loopback client sends `response_type=code`, `code_challenge_method=S256`, a `state` and a `nonce`, accepts
      the code only with the right `state` on the right path, redeems it with the verifier, checks the `nonce` of the
      ID token, and returns the access token (unit tests against a local fake server).
- [ ] A wrong `state`, an `error` answer, a timeout, a busy port and a discovery document that names another issuer or
      a non-https endpoint each fail without a token (unit tests).
- [ ] The listener binds loopback addresses only.
- [ ] A release build on a phone still refuses a redirect that is not an https app link.
- [ ] `docs/authenticator-provider-setup.md` lists what a provider registers and every build setting.
- [ ] Sign-in on a real Windows build against stage (manual; needs a person at the browser).

## Verification

    cd apps/biatec_authenticator
    flutter test
    flutter build windows

## Hand-off

## Review

## Activity log

- 2026-11-16 09:00 · session-20261116-0900 · (new) → in-progress · created at the user's request and claimed; T-249 (same files) is blocked by it
