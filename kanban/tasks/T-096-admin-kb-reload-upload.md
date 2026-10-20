---
id: T-096
title: "Admin: knowledge-base reload and upload"
status: in-progress
priority: P3
epic: "Orchestrator"
blocked_by: [T-086]
area: ["backend/Biatec.Chat.Tools/Kb/**", "backend/Biatec.Chat.Api/Chat/SignIn/**", "backend/Biatec.Chat.Api/Admin/**", "backend/Biatec.Chat.Tests/Kb/**"]
parallel: no
conflicts_with: [T-097]
issue: "#191"
branch: "task/T-096"
owner: "worker-T-096"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-096 · Admin: knowledge-base reload and upload

## Context

Deferred half of T-086 (spec PRODUCT-DESCRIPTION.md:302, "an admin API for policy and knowledge-base management"). `KbSearchService` is an immutable singleton loaded once from `Kb:Path` (Tools/Kb/KbSearchService.cs:24-26; registered in Chat/SignIn/AnonymousTierExecutors.cs:76-81), and a file uploaded to one replica is not seen by the others.

## Goal

Operators can reload and upload knowledge-base pages, and every replica serves the same pages.

## Scope

- In: a reloadable KbSearchService; shared KB storage (a Postgres table, or S3 in eu-central-1 tagged Project=biatec); operator endpoints to upload, list, delete and reload pages (same OperatorAuth and audit as T-086); size and type limits; pages stay untrusted tool-result data.
- Out: a KB editor UI.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-097: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] An uploaded page is searchable on every replica after reload (test)
- [ ] Uploads over the size limit or of a disallowed type are refused (test)

## Verification

`dotnet test --filter Kb|Admin`, then the full backend suite.

## Hand-off

## Review

## Activity log

- 2026-10-20 05:31 · coordinator · (new) → backlog · proposed by a worker / found during verification
- 2026-10-20 05:31 · coordinator · backlog · conflicts_with + T-097 (blockers scan: shared files)
- 2026-10-20 05:32 · LukassSoukup · taken · status stays backlog
- 2026-10-20 05:32 · coordinator · backlog → in-progress · dispatched to worker
