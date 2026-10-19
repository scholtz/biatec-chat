---
id: T-056
title: Architecture, threat model and demo docs
status: backlog
priority: P2
epic: "Deploy & docs"
blocked_by: []
area: ["docs/architecture.md", "docs/threat-model.md", "docs/integration-widget.md", "docs/integration-third-party-consent.md", "docs/hackathon-demo.md"]
parallel: no
conflicts_with: [T-011]
issue: "#57"
branch: ""
owner: ""
taken_by: ""
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
