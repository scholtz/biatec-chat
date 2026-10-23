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

- [x] `k8s/rbac/deploy-role.yaml` is a namespaced Role with explicit resources and verbs only (no wildcards, no `delete`, no RBAC
      objects, no `secrets` list/watch, secrets limited to the named ones where the verb allows it) and a check fails when a rendered
      resource of `k8s/stage` or `k8s/main` is not covered, or when a forbidden rule appears.
- [x] `deploy.yml` warns when the deploy identity can edit RBAC or read all secrets in the namespace, and when the kubeconfig token
      has no expiry or expires within 7 days; an expired token fails with a clear message.
- [x] `docs/cicd.md` replaces the `admin` binding with the scoped Role, documents a time-limited token with rotation, and the
      required-reviewer and branch-policy commands.
- [x] The check runs in CI next to the overlay render.

## Verification

`python k8s/rbac/check_role.py` locally and in CI; `kubectl kustomize k8s/stage` and `k8s/main` still render.

## Hand-off

- **Summary:** k8s/rbac holds a namespaced Role limited to what deploy.yml does (apply without delete, three named secrets, pods/exec on the Postgres pod only, no secrets list, no RBAC objects), a RoleBinding template and a stage-only ClusterRole for the two named PersistentVolumes. check_role.py renders both overlays, reads deploy.yml and fails when anything is uncovered or too wide (its --self-test shows 12 bad roles are rejected); CI runs it. deploy.yml gets a 'Check the deploy identity' step (preflight.sh): an expired kubeconfig token fails clearly; no expiry, expiry within 7 days, reading all secrets or editing RBAC are warnings. docs/cicd.md replaces the admin binding, adds the token and required-reviewer/branch-policy runbook.
- **Files changed:** k8s/rbac/{deploy-role,deploy-rolebinding,deploy-pv-clusterrole}.yaml, k8s/rbac/check_role.py, k8s/rbac/preflight.sh, k8s/rbac/test-preflight.sh, .github/workflows/deploy.yml, .github/workflows/ci.yml, docs/cicd.md.
- **How it was verified:** python k8s/rbac/check_role.py passes on the rendered stage and main overlays; --self-test rejects all 12 mutated roles; k8s/rbac/test-preflight.sh passes 9 checks (valid, expiring, expired, no exp, opaque, no token, wide identity, scoped identity, missing namespace) with a fake kubectl and forged JWTs. Both workflows parse as YAML.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** Nothing was applied to a cluster or to GitHub: the Role is untested against a real API server, so the first bind should be tried on Stage with a deploy run (kubectl auth can-i --list as the account is a quick check). The required-reviewer and branch-policy commands are documented, not run; Production reviewers also hold the weekly rolesanywhere-expiry check of Production until approved. Identity warnings do not fail the deploy so existing environments can move on the cluster admin's schedule. PV create cannot be limited by name, and pods/exec still grants psql access to the Postgres pod, which the migration needs.

## Review

## Activity log

- 2026-10-23 17:00 · claude · (new) → backlog · created from GitHub issue #273 (BCHAT-020); blockers scan: none
- 2026-10-23 15:22 · scholtz · taken, and session-20261023-1522 · backlog → in-progress · claimed
