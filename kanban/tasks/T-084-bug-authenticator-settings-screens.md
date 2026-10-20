---
id: T-084
title: "Bug: authenticator device management, trusted clients and security settings screens"
status: in-progress
priority: P2
epic: "Consent & authenticator"
blocked_by: []
area: ["apps/biatec_authenticator/lib/devices/**", "apps/biatec_authenticator/lib/clients/**", "apps/biatec_authenticator/lib/security/**", "apps/biatec_authenticator/lib/routes.dart", "apps/biatec_authenticator/test/settings/**", "packages/consent-protocol/mock-oidc/**", "docs/required-changes-biatec-oidc.md"]
parallel: yes
conflicts_with: []
issue: "#153"
branch: "task/T-084"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-20
updated: 2026-10-20
---

# T-084 · Bug: authenticator device management, trusted clients and security settings screens

## Context

Spec, Authenticator App: *"Screens: enrollment, pending approvals list, approval card, approval history, device
management, trusted clients and per-client thresholds, security settings (biometric requirement, threshold, blocked
clients)."* Rule 7: a pending enrollment *"can be cancelled from that device"*. Rule 10: the 24-hour client pause
*"the user can lift from the trusted-clients screen"*. Goal: loosening settings is consent-elevated on an active device.

## Analysis

Recorded by the spec-vs-implementation audit on 2026-10-20 (`main @ 6b701e5`, finding #7).

- `apps/biatec_authenticator/lib/routes.dart:41-59`: `/devices`, `/clients`, `/security` render `PlaceholderScreen`.
  Enrollment, pending, approval card and history are implemented (T-051..T-053).
- No task covers the three screens; T-050 only scaffolded them.
- They depend on Biatec OIDC APIs that do not exist yet (device list/cancel, per-client settings, pauses, threshold).
  Per AGENTS.md: build against the mock (`packages/consent-protocol/mock-oidc`), add the API shapes to
  docs/required-changes-biatec-oidc.md, and keep the screens working against the mock only.
- Loosening (raise threshold, turn off always-ask, lift a pause) must itself be a signed consent on this device;
  tightening applies immediately.

## Analysis prompt

Paste this into the session that picks the task up, before writing any code:

> Analyse task T-084 in biatec-chat. Read PRODUCT-DESCRIPTION.md "Goal" of the authenticator section, Protocol Rules
3, 4, 7, 8 and 10, "Authenticator App (Flutter)" and the Required Changes rows on consent settings and loosening.
Read apps/biatec_authenticator/lib/** (routes, enrollment, approvals), packages/consent-protocol/mock-oidc/** and
docs/required-changes-biatec-oidc.md. List the OIDC endpoints each screen needs, which exist in the mock, and which
must be added (with shapes). Decide what is in scope before the code freeze and what stays a placeholder with an
explicit "not available yet" state.
>
> Report: (a) does the conflict still exist on current `main`, with file:line evidence; (b) is the spec text
> unambiguous, quoting it; (c) the smallest change that satisfies it and what it touches outside this task's `area`;
> (d) risks and tests needed. End with a verdict line: `APPROVED`, `PARTIAL: <approved items>` or
> `REJECTED: <reason>`, and write it into the task's Analysis verdict section.

## Analysis gate

This task starts with an analysis step. **No code may be written until the Analysis verdict below says `APPROVED`
or `PARTIAL`.**

1. Run the **Analysis prompt** above against the current `main` (the finding was recorded on `main @ 6b701e5`;
   later merges may already have fixed or changed it).
2. Write the result into **Analysis verdict**: `APPROVED` (implement as scoped), `PARTIAL` (list exactly which
   findings or acceptance criteria are approved; only those may be implemented, the rest are struck through with a
   reason), or `REJECTED` (no conflict with the spec after all, or already fixed; give the evidence).
3. `APPROVED` / `PARTIAL`: implement only the approved part, then hand off as usual.
   `REJECTED`: change no code; move the task to `review` with the evidence in the Hand-off, so the user can close it.
4. If the analysis shows the fix needs a spec decision (the spec is ambiguous or two spec sections disagree), record
   the question under Hand-off → Open questions and ask the user before implementing that part.

## Analysis verdict

_pending: filled in by the agent that runs the Analysis prompt (verdict, date, evidence, approved items)._

## Goal

The three settings screens work end to end against the mock Biatec OIDC, with loosening changes requiring an on-device signed consent.

## Scope

- In: device list with cancel of a pending enrollment; trusted clients with per-client threshold/always-ask and lifting
  a pause; security settings (biometric requirement, threshold, block unverified clients); mock endpoints; required
  changes doc rows; widget tests.
- Out: real Biatec OIDC (sibling repo, never modified here); enrollment fixes (T-072).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [ ] The Analysis verdict is recorded (APPROVED / PARTIAL / REJECTED) with evidence
- [ ] No route renders PlaceholderScreen
- [ ] Cancelling a pending enrollment and lifting a client pause work against the mock
- [ ] Raising a threshold or disabling always-ask requires a device-signed approval; lowering applies immediately
- [ ] New OIDC API shapes are listed in docs/required-changes-biatec-oidc.md with fallback "None" and a config flag
- [ ] `flutter test` passes

## Implementation notes

Reuse the approval signing path from lib/approvals for loosening consents. No colour-only warnings (accessibility rule).

## Verification

`cd apps/biatec_authenticator && flutter test`; `pnpm --filter mock-oidc test` (or the package's test command).

## Hand-off

Filled in by the agent when it moves the task to `review`.

- **Summary:** what was done.
- **Files changed:** ...
- **How it was verified:** commands run and their results.
- **Follow-up tasks created:** IDs, or "none".
- **Unblocks:** output of `kanban/scripts/board.py unblocks T-084`: tasks that become ready once this is approved.
- **Open questions:** for the user, or "none".

## Review

## Activity log

- 2026-10-20 02:40 · coordinator · (new) → backlog · created from the spec-vs-implementation audit of main @ 6b701e5 (finding #7)
- 2026-10-20 03:21 · scholtz · taken · status stays backlog
- 2026-10-20 03:21 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
