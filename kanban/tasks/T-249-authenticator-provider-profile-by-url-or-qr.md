---
id: T-249
title: "Authenticator: pick the provider at run time from a profile URL or QR code"
status: backlog
priority: P2
epic: "Consent & authenticator"
blocked_by: [T-248]
area: ["apps/biatec_authenticator/lib/provider/**", "apps/biatec_authenticator/lib/config.dart", "apps/biatec_authenticator/lib/main.dart", "apps/biatec_authenticator/lib/enrollment/**", "apps/biatec_authenticator/test/provider/**", "docs/authenticator-provider-setup.md", "PRODUCT-DESCRIPTION.md"]
parallel: no
conflicts_with: [T-250, T-251, T-252, T-253]
issue: "#421"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-11-16
updated: 2026-11-16
---

# T-249 · Authenticator: pick the provider at run time from a profile URL or QR code

## Context

Asked for by the user on 2026-11-16: "make sure the user can pick somehow the authenticator options by just providing
url where to download this data using qr code scanner for example". After T-248 the provider (OIDC base URL, client
id, redirect, feature switches, pins) is fixed when the app is built. A bank that wants its customers to use the
universal authenticator then needs its own build.

## Goal

A person who has not enrolled yet can point the app at another provider by typing the provider's address or scanning
its QR code. The app downloads the provider profile, shows who it is, and uses it after the person confirms.

## Scope

- In: the profile document (`https://<host>/.well-known/biatec-authenticator.json`: issuer, client id, redirect per
  platform, feature switches, public key pins), its schema and rules; download with no redirects, https only, the
  issuer on the profile's own host; a confirmation screen that shows the host; storage on the device; applying it at
  start; "use another provider" on the first enrollment screen (a typed address everywhere, the QR scanner on phones);
  going back to the built-in provider; tests; the guide and PRODUCT-DESCRIPTION.
- Out: changing the provider while a device is enrolled (erase first); app links for hosts that are not compiled into
  the app (a phone release build can only return from the browser to a host in its manifest, so a profile for another
  host works on desktop and says so on a phone).

## Parallel work

Parallel work possible: **no**. It shares `lib/enrollment/**` with T-250 and its files under `lib/**` with T-251.
T-248, which changed the same files, is done.

## Acceptance criteria

- [ ] A profile on an https host is downloaded, validated and shown for confirmation with its host; nothing is used
      before the person confirms.
- [ ] A profile that is not https, redirects, names an issuer on another host, or has no pins is refused with the
      reason.
- [ ] The chosen provider survives a restart and is used for the login, the enrollment and every later call, with its
      pins.
- [ ] The provider cannot be changed while a device is enrolled.
- [ ] The guide describes the profile document field by field.

## Verification

    cd apps/biatec_authenticator
    flutter test

## Hand-off

## Review

## Activity log

- 2026-11-16 09:00 · session-20261116-0900 · (new) → blocked · created at the user's request; waits for T-248 (same files, and it builds on the browser login)
- 2026-11-16 10:30 · kanban-board · backlog → blocked · waiting on: T-248
- 2026-11-16 11:46 · kanban-board · blocked → backlog · blockers done: T-248
- 2026-11-16 14:30 · session-20261116-0900 · backlog → backlog · conflicts with T-250 (plan widened to lib/enrollment) and the new T-251 (all of lib); parallel: no
