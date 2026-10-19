---
id: T-056
title: Architecture, threat model and demo docs
status: in-progress
priority: P2
epic: "Deploy & docs"
blocked_by: []
area: ["docs/architecture.md", "docs/threat-model.md", "docs/integration-widget.md", "docs/integration-third-party-consent.md", "docs/hackathon-demo.md"]
parallel: no
conflicts_with: [T-011]
issue: "#57"
branch: "task/T-056"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#repository-layout"
created: 2026-10-19
updated: 2026-10-19
---

# T-056 · Architecture, threat model and demo docs

## Context

docs/ holds architecture, threat model, integration guides and the hackathon demo script.

## Goal

Docs a judge or integrator can follow.

## Scope

- In: Write the five docs from the product description adapted to what this repo implements and its fallbacks.
- Out: Code.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-011: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] All five docs exist and render
- [ ] Demo script matches the fallback mode (always ask) or mock
- [ ] Widget integration guide shows plugin install
- [ ] Threat model table reflects implemented controls

## Verification

Review docs

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → backlog · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 20:38 · scholtz · taken · status stays backlog
- 2026-10-19 20:38 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
