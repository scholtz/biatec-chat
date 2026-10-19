---
id: T-054
title: Containers and k8s manifests
status: done
priority: P2
epic: "Deploy & docs"
blocked_by: [T-002, T-027]
area: ["backend/Dockerfile", "builders/Dockerfile", "k8s/**"]
parallel: yes
conflicts_with: []
issue: "#55"
branch: "task/T-054"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#repository-layout"
created: 2026-10-19
updated: 2026-10-19
---

# T-054 · Containers and k8s manifests

## Context

k8s manifests for main and stage following Biatec conventions; secrets referenced, never inlined.

## Goal

Deployable artifacts (not applied).

## Scope

- In: Dockerfiles for backend and builders, k8s deployment/service/ingress/configmap for main and stage, secret references, health probes.
- Out: Applying to any cluster.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] docker build succeeds for both images
- [ ] kubectl apply --dry-run=client passes
- [ ] No secrets in manifests
- [ ] Probes use /health

## Verification

docker build; kubectl apply --dry-run=client -f k8s/stage

## Hand-off

## Review

- 2026-10-19 21:21 · approve · PR #91 merged into main @ baa1cff (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 20:10 · kanban-board · blocked → backlog · blockers done: T-002, T-027
- 2026-10-19 20:38 · scholtz · taken · status stays backlog
- 2026-10-19 20:38 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-19 21:21 · user · in-progress → done · PR #91 merged into main @ baa1cff (synced by kanban-sync)
