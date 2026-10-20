---
id: T-061
title: Backend fallback cap for users without an active device
status: review
priority: P3
epic: "Consent & authenticator"
blocked_by: [T-025, T-049]
area: ["backend/Biatec.Chat.Tools/Caps/NoDevice/**"]
parallel: no
conflicts_with: [T-024, T-035]
issue: "#62"
branch: "task/T-061"
owner: "worker-T-061"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#required-changes-to-biatec-oidc"
created: 2026-10-19
updated: 2026-10-20
---

# T-061 · Backend fallback cap for users without an active device

## Context

Required Changes row "Reduced per-client daily cap for users without an active device" (protocol rule 4) has the
fallback: "When the user has no active device, enforce the reduced 100 EUR per-client daily cap in the backend for
such users (weaker because it is backend-side), and block above-threshold signing for them." Whether the user has an
active device comes from `GET /consent/settings` (mock in T-047, client in T-049).

## Goal

Implement the rule 4 fallback until Biatec OIDC enforces it.

## Scope

- In: Daily per-tenant 100 EUR (configurable) cap for users without an active device, reusing the T-025 reservation
  store; refusal of above-threshold signing with a message that offers enrollment; a config switch to turn it off
  once Biatec OIDC ships the real rule.
- Out: The OIDC-side implementation (record it in `docs/required-changes-biatec-oidc.md`, T-044).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-024, T-035: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [x] No-device user is refused once the day's total would exceed the cap
- [x] Above-threshold action for a no-device user is refused with an enrollment hint
- [x] Users with an active device are unaffected
- [x] Tests with fake consent settings

## Verification

dotnet test --filter NoDevice

## Hand-off

- **Summary:** backend fallback for rules 4 and 8 (required-changes row 10). Before signing, the confirm path reads `GET /consent/settings` (cached per session 60 s; a missing endpoint is remembered 10 min and skipped). For a user with no active device: above-threshold (threshold clamped to the 1000 EUR ceiling), unpredictable value, `always_ask`, preview `requireConsent` or cap-forced consent → refused with `no_active_device` and an enrollment hint; cooling-off and first use go through the reduced cap (rule 4); otherwise the value is reserved in the T-025 store against a rolling 24 h total (`Caps:NoDevice:DailyCapEur`, default 100), zero-value actions never refused. If OIDC still answers StepUp for a no-device user, no ConsentRequest is opened: reservation released, preview reverted, `no_active_device` returned at once. `CapOutcome { Allowed, RequireConsent, Refused }` replaces the bool pair (unknown outcomes refused). `Caps:NoDevice:Enabled` (default true) off switch; disabled resolves nothing.
- **Branch:** `task/T-061` @ `e321468` (merged with main), PR #184 (`Closes #62`).
- **Files outside the area:** `Tools/Caps/{SpendCaps,CapPolicy}.cs`, `Tools/Consent/CibaTokenClient.cs` (`FetchAsync`), `Api/Chat/Confirm/ConfirmService.cs`, `Api/Caps/CapsRegistration.cs`, `Tests/Caps/SpendCapsTests.cs`, docs/required-changes-biatec-oidc.md (row 10).
- **Verification:** Caps 53 passed; full backend on current main 1172 passed, 1 skipped, 0 failed. Review (medium): all realistic findings fixed; the coordinator's first instruction for cooling-off/first use contradicted rule 4 and was corrected.
- **Known limitations:** the 100 EUR/day applies every day for no-device users (the backend doesn't know the client's first 24 h / user's first day); the settings cache is per pod (60 s); an OIDC-side CIBA request opened on StepUp for a no-device user is left to expire.
- **Unblocks:** no task is blocked by T-061.

## Review

## Activity log

- 2026-10-19 18:00 · coordinator · (new) → blocked · created in task review: rule 4 fallback had no task
- 2026-10-20 01:09 · LukassSoukup · blocked · parallel analysis: parallel: no, added T-024 and T-035: the above-threshold refusal goes into ConfirmService.cs
- 2026-10-20 03:29 · kanban-board · blocked → backlog · blockers done: T-025, T-049
- 2026-10-20 04:17 · LukassSoukup · taken · status stays backlog
- 2026-10-20 04:17 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 05:07 · coordinator · in-progress → review · branch task/T-061 @ e321468, PR #184
