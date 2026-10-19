---
id: T-057
title: OpenTelemetry traces and metrics
status: in-progress
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
updated: 2026-10-19
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

- [ ] Spans emitted for each outbound dependency
- [ ] Metrics exported
- [ ] No tokens or IPs in attributes
- [ ] Disabled by default in tests

## Verification

dotnet test; run with console exporter

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 21:21 · kanban-board · blocked → backlog · blockers done: T-014
- 2026-10-19 23:24 · LukassSoukup · backlog · conflicts_with revised: dropped done T-016, T-019, T-022, T-027 and T-024, T-025: spans come from instrumentation at registration
- 2026-10-19 23:25 · LukassSoukup · taken · status stays backlog
- 2026-10-19 23:59 · LukassSoukup · backlog · conflicts_with revised: dropped T-018 (done), T-035 and T-049: metrics come from decorators on IPolicyEventSink, IEvidenceLog and the requestSignIn executor registered from Telemetry/, so no orchestrator, confirm, consent or evidence code is edited
- 2026-10-19 23:59 · session-20261019-2359 · backlog → in-progress · claimed by /kanban-next
