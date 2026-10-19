---
id: T-057
title: OpenTelemetry traces and metrics
status: done
priority: P3
epic: "Deploy & docs"
blocked_by: [T-014]
area: ["backend/Biatec.Chat.Api/Telemetry/**"]
parallel: yes
conflicts_with: []
issue: "#58"
branch: "task/T-057"
owner: "session-20261019-2359"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#non-functional-requirements"
created: 2026-10-19
updated: 2026-10-20
---

# T-057 · OpenTelemetry traces and metrics

## Context

Traces across widget request, Bedrock, MCP and OIDC calls; metrics for refusals per tier, consent outcomes, policy violations.

## Goal

Observability for the backend.

## Scope

- In: OTel setup with OTLP exporter config, spans around outbound calls, the listed metrics, no PII in attributes.
- Out: Dashboards.
- Constraint: hook metrics in by decorating registered services (`IPolicyEventSink`, `IEvidenceLog`, executors) from `Telemetry/`; don't edit `ConversationOrchestrator`, `ConfirmService`, consent or evidence code (T-035, T-049).

## Parallel work

Parallel work possible: **yes**. It changes only its own area, plus append-only wiring.

## Acceptance criteria

- [x] Spans emitted for each outbound dependency
- [x] Metrics exported
- [x] No tokens or IPs in attributes
- [x] Disabled by default in tests

## Verification

dotnet test; run with console exporter

## Hand-off

- **Branch:** `task/T-057` @ `e825900`, PR #123 (`Closes #58`).
- **What:** `backend/Biatec.Chat.Api/Telemetry/`: `AddBiatecTelemetry` (last call in `Program.cs`, no-op unless `Telemetry:Enabled`), OTLP/Console/None exporters, `ChatMetrics` (`biatec.chat.refusals`, `biatec.chat.policy_violations`, `biatec.chat.consent`) fed by decorators on `IPolicyEventSink`, `IEvidenceLog` and `RequestSignInExecutor`, and `TelemetryScrubber` (addresses, user agent, query, URL credentials, address-like path segments).
- **Outside the area:** append-only `Program.cs` line, `Telemetry` section in `appsettings.json`, package references, `docs/architecture.md` (built table; also added the missing T-018 Budget row).
- **Verification:** `dotnet test` 565 passed, 1 skipped (KMS), 0 failed. Local run with `Telemetry__Exporter=Console`: server and Npgsql spans plus HTTP/Kestrel metrics, no client address, user agent or query value.
- **Review (medium):** fixed address leak in URL paths, OTLP log export, model errors counted as refusals, gRPC hard-coded, exporter switch, sign-in counted before success.
- **Follow-ups / not done:** budget-breaker refusals (`Budget/BudgetedModelClient`) aren't counted; consent outcomes appear only once T-049 appends `consent_*` outcome entries; if T-035 adds members to `IEvidenceLog`, `MeteringEvidenceLog` needs the pass-throughs.
- **Unblocks:** no task is blocked by T-057.

## Review

- 2026-10-20 00:20 · approve · PR #123 merged into main @ 0cbdb3a (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 21:21 · kanban-board · blocked → backlog · blockers done: T-014
- 2026-10-19 23:24 · LukassSoukup · backlog · conflicts_with revised: dropped done T-016, T-019, T-022, T-027 and T-024, T-025: spans come from instrumentation at registration
- 2026-10-19 23:25 · LukassSoukup · taken · status stays backlog
- 2026-10-19 23:59 · LukassSoukup · backlog · conflicts_with revised: dropped T-018 (done), T-035 and T-049: metrics come from decorators on IPolicyEventSink, IEvidenceLog and the requestSignIn executor registered from Telemetry/, so no orchestrator, confirm, consent or evidence code is edited
- 2026-10-19 23:59 · session-20261019-2359 · backlog → in-progress · claimed by /kanban-next
- 2026-10-20 00:19 · session-20261019-2359 · in-progress → review · PR #123, tests and review passed
- 2026-10-20 00:20 · user · review → done · PR #123 merged into main @ 0cbdb3a (synced by kanban-sync)
