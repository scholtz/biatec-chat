---
id: T-071
title: CI/CD pipelines, stage deploy and production promotion
status: review
priority: P1
epic: "Deployment"
blocked_by: [T-054]
area: [".github/workflows/**", "docs/cicd.md", "docs/aws-setup.md", "docs/deploy-aws.md", "docs/AWS-AGENTCORE-GUIDE.md", "runGuide.md", "k8s/**", "infra/aws/**", "infra/guardrail/**", "backend/Biatec.Chat.Api/Vault/**", "backend/Biatec.Chat.Api/Config/Options.cs", "backend/Biatec.Chat.Tests/Vault/**", "backend/Biatec.Chat.Tests/ConfigAndHealthTests.cs"]
parallel: yes
conflicts_with: []
issue: "#128"
branch: "task/T-071"
owner: "session-20261020-claude"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#non-functional-requirements"
created: 2026-10-20
updated: 2026-10-20
---

# T-071 · CI/CD pipelines and stage deploy

## Context

Biatec Chat will run on Biatec's **stage** Kubernetes environment (`stage.chat.biatec.io`, namespace
`biatec-chat-stage`, overlay `k8s/stage` from T-054). There is no CI today: `.github/workflows` only holds
`kanban-sync.yml`. Scholtz provides the deployment credentials as **GitHub environment secrets** (environment
`Stage`), the same way BiatecDEX deploys (`BiatecDEX/.github/workflows/deploy-stage.yml`: Docker Hub
`DOCKERHUB_USER` / `DOCKERHUB_TOKEN`, a base64 `KUBE_CONFIG`, an immutable image tag, `kubectl rollout status`).

AWS access in the cluster uses the keyless web-identity role from `infra/aws/k8s-role.sh` (`aws-role-arn` key of
`biatec-chat-secrets`, see `k8s/README.md` "AWS access"). Never AWS access keys.

## Goal

Every PR is built and tested automatically, and every push to `main` deploys the tested backend and builders images
to stage, with all credentials coming from the `Stage` environment secrets.

## Scope

- In: `ci.yml` (PRs and `main`: backend build + tests, JS packages typecheck/test/build, builders build, Flutter
  analyze + test, Docker builds without push, `kubectl kustomize` render of both overlays); `deploy-stage.yml`
  (push to `main` + manual: tests, build and push both images with an immutable tag, sync `biatec-chat-secrets`
  from environment secrets, `kubectl apply -k` of `k8s/stage` with the pinned tag, rollout status); image names
  following Biatec's Docker Hub convention; `docs/cicd.md` with the exact secret list for Scholtz.
- Out: production promotion (follow-up, like BiatecDEX `promote-production.yml`); npm publishing of the widget;
  creating the AWS role and cluster OIDC issuer (`infra/aws/k8s-role.sh`, operator step); tenant seeding.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [x] `ci.yml` runs on pull requests and pushes to `main` and fails on any failing build or test.
- [x] `deploy-stage.yml` deploys only from `main` (or manual dispatch), uses the `Stage` environment, pins an
      immutable image tag, and waits for `kubectl rollout status` of backend and builders.
- [x] No credential appears in the repository; every secret is read from `secrets.*` and the kubeconfig is removed
      after the job.
- [x] `docs/cicd.md` lists every secret Scholtz must set, with its format.
- [x] Workflows pass `actionlint` (or an equivalent YAML/syntax check) and `kubectl kustomize` renders both
      overlays.

## Verification

`actionlint .github/workflows/*.yml`; `kubectl kustomize k8s/stage` and `k8s/main`; `dotnet test`, `pnpm -r test`
locally; first real run on GitHub once Scholtz has set the secrets.

## Hand-off

- **Summary:** Target changed by the user: Biatec's own Kubernetes with stage and production, AWS for Bedrock only.
  `ci.yml` (PRs). `deploy-stage.yml` (push to `main`): CI, build/push `<version>-beta` + moving `stage` tag, deploy to
  `Stage`. `promote-production.yml` (manual): re-tag the stage images `-beta` → `-main` (same digest, no rebuild) and
  deploy to `Production`. `deploy.yml` (reusable): generated secrets (DB passwords, builders secret, vault master key),
  `biatec-chat-secrets` sync, pinned tag, apply, DB waits, idempotent migrations, rollouts. In-cluster Postgres/Redis.
  Token vault: new `Secret` key provider (no KMS); `Kms:KeyId` required only with the Kms provider. AWS: keyless
  Bedrock-only role; EC2/KMS path removed.
- **Files changed:** `.github/workflows/{ci,deploy,deploy-stage,promote-production}.yml`; `backend/.../Vault/{SecretKeyProvider,
  TokenVault,VaultRegistration}.cs`, `Config/Options.cs` and their tests; `k8s/base/*`, `k8s/{main,stage}/kustomization.yaml`,
  `k8s/README.md`; `infra/aws/*` (create/user-data/ec2-trust/kms-key removed); `docs/{cicd,aws-setup}.md`
  (`deploy-aws.md` removed), `runGuide.md`.
- **How it was verified:** backend 875 passed / 1 skipped; pnpm steps from a clean `dist`; actionlint; both overlays
  render with pinned tags; `infra/aws/test-scripts.sh` 8/8; promote re-tag flow against a local registry (label read,
  same digest, missing tag fails); Postgres/Redis with the manifests' security settings; migration script applied twice.
  A real deploy is unverified until the secrets and the cluster issuer exist.
- **Follow-up tasks created:** T-072.
- **Unblocks:** T-072 (ready once this is done).
- **Open questions:** the cluster's service-account issuer URL (for `infra/aws/k8s-role.sh`); whether stage and
  production share one cluster (the role script trusts one issuer).

## Review

## Activity log

Append-only. Format: `- YYYY-MM-DD HH:MM · <actor> · <from> → <to> · <note>`

- 2026-10-20 00:44 · LukassSoukup · (new) → backlog · created on user request (stage deploy via GitHub Actions);
  blockers scan: T-054 (k8s manifests, done) is the only ordering need
- 2026-10-20 00:44 · session-20261020-claude · backlog → in-progress · claimed by LukassSoukup
- 2026-10-20 00:49 · session-20261020-claude · in-progress → review · PR #130
- 2026-10-20 00:57 · session-20261020-claude · review → review · scope extended at the user's request: in-cluster Postgres/Redis with generated passwords, deploy-time migrations, AWS setup guide, merged fix/aws-access (#124)
- 2026-10-20 01:30 · session-20261020-claude · review → review · rework at the user's request: own k8s stage + prod, promote pipeline, AWS for Bedrock only (Secret vault key provider, EC2/KMS removed)
