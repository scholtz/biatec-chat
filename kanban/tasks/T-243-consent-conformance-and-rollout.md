---
id: T-243
title: "Run the conformance kit against a real Biatec OIDC, and write the consent rollout checklist (flags, stage test script)"
status: blocked
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-237, T-238, T-240]
area: ["packages/consent-protocol/conformance/**", "docs/consent-rollout.md", "docs/required-changes-biatec-oidc.md", "docs/threat-model.md", "docs/architecture.md", "backend/Biatec.Chat.Api/appsettings*.json", "k8s/**"]
parallel: no
conflicts_with: [T-245, T-246]
issue: "#401"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#required-changes-to-biatec-oidc"
created: 2026-11-12
updated: 2026-11-13
---

# T-243 - Conformance against the real Biatec OIDC, and the rollout checklist

## Context

Architecture record: ludovit-scholtz/AlgorandGoogleDriveAccount#23 (decision D4; phase 1, step 7). It can only be finished once the Biatec OIDC issues 24 to 29 are deployed to stage; the parts that do not need the server can be done before.

Everything in this repository is tested against the mock. Nothing has run against the real server, the backend's fallbacks assume the server does not enforce consent (`Caps:OidcHonoursRequireConsent` false, `Caps:NoDevice:Enabled` true), and several documents say above-threshold signing is unavailable (`docs/threat-model.md` lines 47 and 79, `docs/architecture.md` lines 206 to 207).

## Goal

A person can prove, with a script and a phone, that the consent flow works on stage, and the switch from fallbacks to server enforcement is a written, reversible procedure.

## Scope

- In:
  - **Conformance kit against any server.** `CONFORMANCE_OIDC_URL` (default: start the in-process mock as today). Each case declares what it needs (`mockHelpers`, `clientSecret`, `userToken`, `deviceKey`); against a real server the cases needing mock helpers are reported as skipped with the reason, never silently passed. Inputs for a real run come from environment variables only (`CONFORMANCE_CLIENT_ID`, `CONFORMANCE_CLIENT_SECRET`, `CONFORMANCE_USER_TOKEN`, …); nothing is written to disk and the report redacts tokens and codes.
  - **Report.** `conformance/report/` gets one file per run with the server URL, the commit of this repo, and pass, fail and skip per case.
  - **`docs/consent-rollout.md`:**
    1. Prerequisites on Biatec OIDC (which issues are deployed, `Consent__Enforcement=AgentClients`, the Biatec Chat client marked `ConsentEnforced`, its domain proven (the backend serves `/.well-known/biatec-client.json`, T-240, and the client's `ClientUri` points at that host), secrets present).
    2. Backend settings to change, in order, each with its rollback: `Caps__OidcHonoursRequireConsent=true` (so soft and safe caps send the user to the phone instead of refusing), then `Caps__NoDevice__Enabled=false` (the server enforces rule 4 itself).
    3. The stage test script for a person with an enrolled phone, each step with the expected result on the widget, on the phone and in the evidence export: a swap under the threshold (no phone); a transfer above it (code shown, approve, signed, receipt verified); deny; three wrong codes; let it expire; approve and then change the group (refused); two tabs confirming the same preview (one signature); a user with no device above the threshold (clear refusal with the enrollment hint); a first use of the client under the threshold (phone asked once, card says "New service" and shows the domain); raise the threshold on the phone to 5000 and repeat the transfer above 1000 (no phone); a swap (phone asked: untrusted application), tick "Don't ask again" and swap again under the threshold (no phone); lower the threshold to 0 (every action asks).
    4. What to watch for 24 hours after the switch (the security events of AlgorandGoogleDriveAccount issue 35) and the emergency rollback.
  - **Docs.** Update `docs/required-changes-biatec-oidc.md` row by row with "implemented in Biatec OIDC (issue N), enabled on stage: yes/no", and correct the threat model and architecture statements once stage is switched.
  - The configuration changes themselves are prepared as a PR to the stage overlay only; production stays as it is until the user decides.
- Out: any change in Biatec OIDC; enabling production.

## Parallel work

Parallel work possible: **no**. T-245 and T-246 add cases to the same conformance kit; do not run at the same time as either.

## Acceptance criteria

- [ ] `pnpm --filter conformance test` passes against the mock as before.
- [ ] With `CONFORMANCE_OIDC_URL` set to a server that lacks the mock helpers, the run completes, skips are listed with reasons, and no token or code appears in the report (asserted by a test that feeds marker values).
- [ ] `docs/consent-rollout.md` exists with the four parts above.
- [ ] The run against stage and the manual script are recorded in the Hand-off with their results, or the Hand-off says plainly that they were not run and why (server not deployed, no phone).

## Implementation notes

- Do not put a stage client secret or user token in a file, a commit, a CI variable dump or the report. A person supplies them in their own shell.
- AWS and hackathon rules in `AGENTS.md` still apply to any deployment change.

## Verification

`pnpm --filter conformance test`

## Hand-off

Filled in when the task moves to review.

## Review

## Activity log

- 2026-11-12 18:43 · scholtz · (new) → blocked · created from the consent architecture record (AlgorandGoogleDriveAccount issue 23)
- 2026-11-12 18:43 · kanban-board · backlog → blocked · waiting on: T-237, T-238, T-240
- 2026-11-13 09:05 · scholtz · blocked → blocked · amended for revision 2 (proven domain, user-set threshold and trusted application in the stage script)
