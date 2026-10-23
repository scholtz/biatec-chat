---
id: T-145
title: "Scope the deploy identity: a least-privilege Role, a time-limited kubeconfig token and required reviewers (BCHAT-020)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["k8s/rbac/**", ".github/workflows/deploy.yml", ".github/workflows/ci.yml", "docs/cicd.md"]
parallel: yes
conflicts_with: []
issue: "#273"
branch: "task/T-145"
owner: "session-20261023-1522"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-145 - Scope the deploy identity: a least-privilege Role, a time-limited kubeconfig token and required reviewers (BCHAT-020)

## Context

Security audit finding BCHAT-020 (Low, issue #273, RISK-013, CWE-250). `docs/cicd.md` binds the deploy service account to the
built-in `admin` ClusterRole in the namespace and adds cluster-scope `persistentvolumes` rights from a script that is not in
this repository. The kubeconfig in the `KUBE_CONFIG` secret is a long-lived token. Anyone who can run a workflow in an
environment, or read the secret, can read every secret in the namespace, edit RBAC there and exec into the database pod.

## Goal

The deploy identity can do what `deploy.yml` does and nothing more, its token expires, and Production needs a human approval.

## Scope

- In: `k8s/rbac/` with a namespaced Role (and the one cluster-scope rule for the stage PersistentVolumes), a RoleBinding template and
  a check that the Role covers what the rendered overlays contain and grants nothing dangerous; a preflight step in `deploy.yml`
  that reports the identity's broad rights and the token's expiry; `docs/cicd.md`: the scoped Role in place of `clusterrole=admin`,
  a short-lived token runbook, and the commands for required reviewers and a branch policy on the environments.
- Out: running anything against the cluster or changing GitHub environment settings (the commands are documented; the user applies
  them); the cluster admin's `create-token.sh` (not in this repository).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] `k8s/rbac/deploy-role.yaml` is a namespaced Role with explicit resources and verbs only (no wildcards, no `delete`, no RBAC
      objects, no `secrets` list/watch, secrets limited to the named ones where the verb allows it) and a check fails when a rendered
      resource of `k8s/stage` or `k8s/main` is not covered, or when a forbidden rule appears.
- [ ] `deploy.yml` warns when the deploy identity can edit RBAC or read all secrets in the namespace, and when the kubeconfig token
      has no expiry or expires within 7 days; an expired token fails with a clear message.
- [ ] `docs/cicd.md` replaces the `admin` binding with the scoped Role, documents a time-limited token with rotation, and the
      required-reviewer and branch-policy commands.
- [ ] The check runs in CI next to the overlay render.

## Verification

`python k8s/rbac/check_role.py` locally and in CI; `kubectl kustomize k8s/stage` and `k8s/main` still render.

## Hand-off

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

- 2026-10-23 17:00 · claude · (new) → backlog · created from GitHub issue #273 (BCHAT-020); blockers scan: none
- 2026-10-23 15:22 · scholtz · taken, and session-20261023-1522 · backlog → in-progress · claimed
