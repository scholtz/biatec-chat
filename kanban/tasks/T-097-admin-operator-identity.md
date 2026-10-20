---
id: T-097
title: "Admin: per-operator identity and evidence for admin changes"
status: backlog
priority: P3
epic: "Sessions & auth"
blocked_by: [T-086]
area: ["backend/Biatec.Chat.Api/Admin/**", "backend/Biatec.Chat.Api/Onboarding/OperatorAuth.cs", "docs/required-changes-biatec-oidc.md"]
parallel: yes
conflicts_with: []
issue: "#192"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-097 · Admin: per-operator identity and evidence for admin changes

## Context

From T-086: all operators share one bearer key (`Onboarding:OperatorKey`), so admin audit lines record the IP and reason but not who made a change, and admin changes aren't in a signed audit chain.

## Goal

Each admin change is attributable to a named operator and recorded verifiably.

## Scope

- In: an operator identity (Biatec OIDC operator claim/group if available — record the needed change in docs/required-changes-biatec-oidc.md with a fallback — or named keys from a secret); admin changes appended to a signed evidence chain.
- Out: an operator UI.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [ ] Each admin change records the operator identity (test)
- [ ] Admin changes are in a verifiable chain (test)

## Verification

`dotnet test --filter Admin`, then the full backend suite.

## Hand-off

## Review

## Activity log

- 2026-10-20 05:31 · coordinator · (new) → backlog · proposed by a worker / found during verification
