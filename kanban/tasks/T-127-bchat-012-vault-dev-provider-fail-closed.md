---
id: T-127
title: "Refuse the Dev vault key provider outside Development (BCHAT-012)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Vault/**", "backend/Biatec.Chat.Tests/Vault/**", "backend/Biatec.Chat.Tests/ConfigAndHealthTests.cs"]
parallel: yes
conflicts_with: []
issue: "#265"
branch: "task/T-127"
owner: "session-20261023-0931"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-127 - Refuse the Dev vault key provider outside Development (BCHAT-012)

## Context

Security audit finding BCHAT-012 (Low, issue #265). `Vault:KeyProvider` defaults to `Dev`; outside Development the vault only logs
a warning and keeps going with a per-process (or configured) dev key. The evidence key store already fails closed unless
`Evidence:AllowEphemeralKeys=true`. The deployed manifest sets `Secret`, so this is a misconfiguration guard.

## Goal

The backend refuses to build the token vault with the Dev key provider outside Development unless `Vault:AllowEphemeralKeys=true`.

## Scope

- In: `VaultOptions.AllowEphemeralKeys`, the fail-closed check in `VaultRegistration` (checked at start-up; Development is exempt), tests.
- Out: other findings.

## Parallel work

Parallel work possible: **yes**.

## Acceptance criteria

- [x] Outside Development with the Dev provider and no flag, resolving the key provider throws a clear error; with the flag, or in Development, it works.
- [x] `Secret` and `Kms` providers are unaffected.
- [x] Existing tests stay green; TEST-011 passes.

## Verification

`dotnet test backend`; `BIATEC_ROOT="$PWD" bash audits/harness/claude-fable-5-1/*/TEST-011.sh`.

## Hand-off

- **Summary:** `VaultOptionsValidator` (runs at start-up) now refuses `Vault:KeyProvider=Dev` outside Development unless `Vault:AllowEphemeralKeys=true`, and validates `Vault:DevKey`. `Secret` and `Kms` are unaffected. The test process opts in once through a module initializer (environment variable), so no test factory changed. PR #309.
- **Files changed:** Api/Vault/TokenVault.cs, Api/Vault/VaultRegistration.cs, Tests/TestHostDefaults.cs, Tests/Vault/VaultKeyProviderGateTests.cs.
- **How it was verified:** dotnet test full suite (1553 passed); TEST-011 passes.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** None.

## Review

## Activity log

- 2026-10-23 09:00 · claude · (new) → backlog · created from GitHub issue #265 (BCHAT-012); blockers scan: none
- 2026-10-23 09:31 · scholtz · taken, and session-20261023-0931 · backlog → in-progress · claimed
