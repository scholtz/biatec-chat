---
id: T-054
title: Containers and k8s manifests
status: blocked
priority: P2
epic: "Deploy & docs"
blocked_by: [T-002, T-027]
area: ["backend/Dockerfile", "builders/Dockerfile", "k8s/**"]
parallel: yes
conflicts_with: []
issue: "#55"
branch: ""
owner: ""
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

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
