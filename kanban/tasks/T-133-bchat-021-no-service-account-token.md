---
id: T-133
title: "Do not mount service-account tokens into the backend and builders pods (BCHAT-021)"
status: done
priority: P3
epic: "Security"
blocked_by: []
area: ["k8s/base/backend.yaml", "k8s/base/builders.yaml"]
parallel: yes
conflicts_with: []
issue: "#274"
branch: "task/T-133"
owner: "session-20261023-1119"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-133 - Do not mount service-account tokens into the backend and builders pods (BCHAT-021)

## Context

Security audit finding BCHAT-021 (Low, issue #274). Kubernetes mounts a service-account token into every pod by default. The backend and
the builders sidecar never call the Kubernetes API (AWS access uses the Roles Anywhere certificate), so the token only helps an attacker
who has code execution in the pod.

## Goal

Neither the backend nor the builders pod gets a service-account token.

## Scope

- In: `automountServiceAccountToken: false` on both pod specs and on the backend ServiceAccount.
- Out: the postgres and redis pods (not named by the finding; the demo pod already sets it).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] `kubectl kustomize` of `k8s/main` and `k8s/stage` renders `automountServiceAccountToken: false` on the backend and builders pod templates.
- [x] Nothing in the repository reads the service-account token or calls the Kubernetes API from these pods.
- [x] CI's kustomize step still passes.

## Verification

`kubectl kustomize k8s/main | grep -B3 automountServiceAccountToken`; the PR's CI run.

## Hand-off

- **Summary:** automountServiceAccountToken: false on the backend and builders pod templates and on the backend ServiceAccount, so neither pod gets a Kubernetes API token. PR #317.
- **Files changed:** k8s/base/backend.yaml, k8s/base/builders.yaml.
- **How it was verified:** kubectl kustomize k8s/main and k8s/stage render the setting on the backend and builders pods (stage's demo pod already had it); a repository search finds no code that reads the service-account token or calls the Kubernetes API; the PR's CI (including the kustomize step) passes.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** The postgres and redis pods still mount the default token (not named by the finding), and the builders pod runs under the namespace default ServiceAccount, so only its pod-level setting protects it; a dedicated ServiceAccount or a default-ServiceAccount patch would cover all pods. Say if you want that.

## Review

- 2026-10-23 11:26 · approve · PR #317 merged into main @ 5046fbf (recorded by kanban-sync)

## Activity log

- 2026-10-23 15:00 · claude · (new) → backlog · created from GitHub issue #274 (BCHAT-021); blockers scan: none
- 2026-10-23 11:19 · scholtz · taken, and session-20261023-1119 · backlog → in-progress · claimed
- 2026-10-23 11:26 · user · in-progress → done · PR #317 merged into main @ 5046fbf (synced by kanban-sync)
