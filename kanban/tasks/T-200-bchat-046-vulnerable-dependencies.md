---
id: T-200
title: "Patch known-vulnerable transitive dependencies (BCHAT-046)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Biatec.Chat.Api.csproj", "package.json", "pnpm-lock.yaml", "docs/security-notes-dependencies.md"]
parallel: yes
conflicts_with: []
issue: "#299"
branch: "task/T-200"
owner: "session-20261023-1152"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-200 - Patch known-vulnerable transitive dependencies (BCHAT-046)

## Context

Security audit finding BCHAT-046 (Low, issue #299, TEST-003/TEST-004). MessagePack 3.1.8 (moderate, GHSA-qhrr-8q5h-9q3h) is a transitive .NET dependency of Api, Tools and Tests. `pnpm audit` reports high advisories through `biatec-concentrated-liquidity-amm 0.9.44` in the builders sidecar (parse-duration, serialize-javascript, undici, sharp, nanoid) and critical ones in vitest tooling (tinypool). The sidecar only exercises the library's transaction builders; the advisories sit in websocket, image and build-tooling paths, so the practical risk is low, but the versions should not stay stale.

## Goal

`dotnet list package --vulnerable --include-transitive` reports nothing, and `pnpm audit --prod` has no high or critical advisory that a patched version exists for. What cannot be fixed is recorded with its reason.

## Scope

- In: a direct patched MessagePack reference; `pnpm.overrides` for the patched transitive npm versions; a short note listing what remains (no patched version) and why it is not reachable.
- Out: changes to BiatecCLAMM (a sibling repository: consume, do not change); SBOM generation (BCHAT-018).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] `dotnet list package --vulnerable --include-transitive` shows no vulnerable package.
- [x] `pnpm audit` shows no critical advisory and no high advisory with a patched version.
- [x] Backend tests, builders tests and the widget/demo/admin builds still pass.
- [x] The remaining advisories (no patch available) are recorded in docs/security-notes-dependencies.md with a reachability note.

## Verification

`dotnet list backend package --vulnerable --include-transitive`, `pnpm audit`, `dotnet test backend`, `pnpm -r test`, `pnpm --filter builders test`.

## Hand-off

- **Summary:** `MessagePack 3.1.12` referenced directly (Api, Tools); `overrides` in pnpm-workspace.yaml for nine transitive npm packages; vitest 3 -> 4.1.12 everywhere; docs/security-notes-dependencies.md. One advisory remains (`braces`, no patched version, docs tooling path).
- **Verification:** dotnet list package --vulnerable: clean; `pnpm audit`: 1 high (braces); admin 40, sidecar 63, evidence-verifier 34, e2e 10 tests pass; widget unchanged from main (the 5 DEX playground failures are BCHAT-045).

## Review

## Activity log

- 2026-10-23 11:52 · kanban-split · (new) → backlog · created from audit issue #299
- 2026-10-23 11:52 · scholtz · taken · status stays backlog
- 2026-10-23 11:52 · session-20261023-1152 · backlog → in-progress · claimed, branch task/T-200
