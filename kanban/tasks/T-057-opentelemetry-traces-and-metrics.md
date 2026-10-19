---
id: T-057
title: OpenTelemetry traces and metrics
status: blocked
priority: P3
epic: "Deploy & docs"
blocked_by: [T-014]
area: ["backend/Biatec.Chat.Api/Telemetry/**"]
parallel: no
conflicts_with: [T-016, T-018, T-019, T-022, T-024, T-025, T-027, T-035, T-049]
issue: "#58"
branch: ""
owner: ""
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

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-016, T-018, T-019, T-022, T-024, T-025, T-027, T-035, T-049: they change the same files, and running them together causes merge conflicts or broken behaviour.

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
