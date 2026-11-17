---
id: T-252
title: "Enrollment: the first device of an account is active at once, without the 24 hour wait"
status: in-progress
priority: P1
epic: "Consent & authenticator"
blocked_by: []
area: ["packages/consent-protocol/mock-oidc/**", "packages/consent-protocol/conformance/**", "docs/required-changes-biatec-oidc.md", "PRODUCT-DESCRIPTION.md"]
parallel: no
conflicts_with: [T-249, T-250]
issue: "#426"
branch: task/T-252
owner: session-20261117-1530
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#consent-rules"
created: 2026-11-17
updated: 2026-11-17
---

# T-252 · Enrollment: the first device of an account is active at once

## Context

Asked by the owner on 2026-11-17, with a screenshot of a first enrollment on Windows ("Device inactive", "Time left:
23:59:17"): "on first setup do not require the 24 hour cooldown".

Rule 7 of the consent rules in PRODUCT-DESCRIPTION.md says today: "Otherwise (the very first device, or a lost-device
recovery) the device is enrolled but inactive for 24 hours". The spec already says what the wait is worth for a user
with no device: "the email notice goes to a mailbox that the same login controls, so the delay only buys time".

The wait is set by Biatec OIDC (`DevicesController`, `ActiveAt = now + ActivationSeconds`), which this repository
does not change. Here it is modelled by the mock (`packages/consent-protocol/mock-oidc/src/app.ts`,
`INACTIVE_DEVICE_SECONDS`).

## Goal

A device enrolled for an account that has no other device (none active, none pending) is active at once. A later
device without a device approval or a recovery code (a lost-device recovery) still waits 24 hours.

## Scope

- In: rule 7 and the passages of PRODUCT-DESCRIPTION.md that repeat it; the mock and its tests; the conformance
  checks that read the rule; the row of the needed server change in `docs/required-changes-biatec-oidc.md`.
- Out: Biatec OIDC itself (another repository: until it changes, stage and production keep the 24 hours for a first
  device); the authenticator app (it reads `active` and `active_at` from the server and needs no change: the waiting
  screen stays for a later device).

## Parallel work

Parallel work possible: **no**. T-249 and T-250 also list PRODUCT-DESCRIPTION.md (T-250 also
`docs/required-changes-biatec-oidc.md`). They change the Authenticator App section and this task changes the consent
rules, so there is no ordering need, only the shared files.

## Acceptance criteria

- [ ] PRODUCT-DESCRIPTION.md rule 7 says the first device is active at once, and names what that costs.
- [ ] The mock answers `active: true` for the first device of an account, and `active: false` with `active_at` 24
      hours later for a later device without an approval or a recovery code.
- [ ] A cancelled enrollment does not count as a device: the next one is a first device again.
- [ ] The mock's tests and the conformance checks pass.
- [ ] `docs/required-changes-biatec-oidc.md` records the change Biatec OIDC needs, with the fallback.

## Implementation notes

What the owner should know (also written into the spec): a person who holds only the Google or Microsoft login of a
user who never enrolled a device can enroll their own device and it approves at once. Before, they had to wait a
day, and the reduced daily cap of a user without a device (rule 4) limited them in that day. The email notice still
goes out. A user who has a device is not affected: a second device still needs the first one, a recovery code, or
the 24 hours.

## Verification

`npm test` in `packages/consent-protocol/mock-oidc`; the conformance run of `packages/consent-protocol`.

## Hand-off

## Review

## Activity log

- 2026-11-17 15:30 · scholtz · (new) → backlog · created at the owner's request; blockers scanned: T-249 and T-250 share PRODUCT-DESCRIPTION.md in another section, no ordering need
- 2026-11-17 15:35 · scholtz · taken · status stays backlog
- 2026-11-17 15:35 · scholtz · backlog → in-progress · claimed; branch task/T-252 (T-250 moved to review first)
