---
id: T-079
title: "Bug: report the page origin, not the backend host, on signing requests"
status: review
priority: P1
epic: "Wallet tools"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Chat/Confirm/ConfirmService.cs", "backend/Biatec.Chat.Api/Chat/Confirm/ConfirmEndpoints.cs", "backend/Biatec.Chat.Tests/Signing/**"]
parallel: no
conflicts_with: [T-025, T-087]
issue: "#148"
branch: "task/T-079"
owner: "worker-T-079"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#protocol-rules"
created: 2026-10-20
updated: 2026-10-20
---

# T-079 · Bug: report the page origin, not the backend host, on signing requests

## Context

Spec, Protocol Rules 10: *"Biatec OIDC allows at most three pending requests per client per user per origin (so a
compromised page on one Biatec site cannot occupy the slots another site needs; origin is reported by the backend with
each request)"*. Required Changes row "Origin-aware consent requests: the backend reports the page origin with each
signing request". The Biatec tenant has two page origins, `web.biatec.io` and `dex.biatec.io`, on one backend host.

## Analysis

Recorded by the spec-vs-implementation audit on 2026-10-20 (`main @ 6b701e5`, finding #2).

- `ConfirmService.cs:181` (and the later-step path near `:377`) computes
  `origin = new Uri(tenant.RedirectUri).GetLeftPart(UriPartial.Authority)`, i.e. always `https://chat.biatec.io`.
- So every signing request from Biatec Web and Biatec DEX reports the same origin; per-origin slots, rate limits and
  24-hour pauses can never be scoped per site, which is the exact property the spec row exists for.
- The confirm endpoint is CSRF-guarded and already requires a registered `Origin` header
  (`Security/CsrfMiddleware.cs`, `OriginPolicy.AllowedOrigin`), so the validated page origin is available.
- Multi-step actions: later steps are confirmed by a later request, so each step should carry the origin of the request
  that confirmed it; record it in the `signing_requested` evidence payload (already has an `origin` field).

## Analysis prompt

Paste this into the session that picks the task up, before writing any code:

> Analyse task T-079 in biatec-chat. Read PRODUCT-DESCRIPTION.md Protocol Rules 10 and the Required Changes row
"Origin-aware consent requests". Find every place that builds WalletSignRequest (grep "Origin:" in
backend/Biatec.Chat.Api and Biatec.Chat.Tools, including limits and safe paths) and check where the origin comes
from. Confirm that the confirm/limits endpoints already validate the Origin header against the tenant's allowed
origins and propose how to pass that validated value down (ConfirmService signature, limits path, consent retry
path).
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

APPROVED, 2026-10-20 (worker, recorded by the coordinator; origin/main @ eda9c9b). Evidence: the only two
WalletSignRequest builders, both in `Chat/Confirm/ConfirmService.cs` (confirm path `:185/:205/:211`, consent resume
`:473-475`), reported `tenant.RedirectUri`'s origin (always https://chat.biatec.io). Spec: `PRODUCT-DESCRIPTION.md:492`
(Protocol Rules 10, "origin is reported by the backend with each request"; pauses scoped to the reporting origin) and
`:525` ("the backend reports the page origin with each signing request"). CSRF already requires a registered Origin
on every POST (`Security/CsrfMiddleware.cs:28`, `OriginPolicy.cs:38-49`).

## Goal

Every signing and limits request to Biatec OIDC carries the validated page origin of the request the user confirmed from.

## Scope

- In: pass the validated `Origin` from the confirm (and limits/consent-retry) endpoints into `WalletSignRequest.Origin`
  and the evidence payload; tests for web vs dex origins.
- Out: the soft-cap `requireConsent` flag (T-025).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-025, T-087: they change the same files, and
running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [x] The Analysis verdict is recorded (APPROVED / PARTIAL / REJECTED) with evidence
- [x] A confirm from `https://dex.biatec.io` sends `origin: https://dex.biatec.io` to the fake wallet endpoint; one from `https://web.biatec.io` sends that
- [x] A request without a registered Origin never reaches signing (existing CSRF guard, covered by a test)
- [x] The `signing_requested` evidence entry records the page origin

## Implementation notes

Use the canonical origin from `OriginPolicy.AllowedOrigin(tenant, request)`, never the raw header. T-025 and T-087 touch ConfirmService too.

## Verification

`dotnet test --filter Signing`.

## Hand-off

- **Summary:** the confirm endpoint passes the canonical registered origin (`OriginPolicy.AllowedOrigin`, never the raw header; 403 without one) as a required `pageOrigin` to ConfirmService, which re-checks it against the tenant's origins, sends it as WalletSignRequest.Origin and records it in signing_requested evidence. The consent request stores it (new nullable `ConsentRequest.Origin`, migration `ConsentRequestOrigin`) and the resume after phone approval reports the same origin; missing/unregistered → backend origin.
- **Branch:** `task/T-079` @ `3ef52ef` (merged with main), PR #171 (`Closes #148`).
- **Files changed:** `Chat/Confirm/{ConfirmService,ConfirmEndpoints}.cs`, Signing tests; outside the area: `Core/Data/Entities.cs`, migration + snapshot, `Security/OriginPolicy.cs` (new overload), one-line call-site changes in 7 test files.
- **How it was verified:** Signing + Consent 160 passed; full backend on current main 1080 passed, 1 skipped, 0 failed; endpoint tests for dex/web, canonicalization, missing / evil / suffix-trick / "null" origins (403, no wallet calls). Review (medium): all realistic findings fixed.
- **Follow-up tasks created:** none (proposed: report the origin on PUT /wallet/limits if Biatec OIDC scopes limits by origin — a spec question).
- **Unblocks:** T-087 (conflict) can start once this leaves in-progress.
- **Open questions:** none.

## Review

## Activity log

- 2026-10-20 02:40 · coordinator · (new) → backlog · created from the spec-vs-implementation audit of main @ 6b701e5 (finding #2)
- 2026-10-20 03:34 · LukassSoukup · taken · status stays backlog
- 2026-10-20 03:34 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 04:05 · coordinator · in-progress → review · branch task/T-079 @ 3ef52ef, PR #171; analysis verdict APPROVED recorded
