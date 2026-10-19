# CI/CD

Two GitHub Actions workflows. Nothing in the repository holds a credential: deploy credentials are GitHub environment
secrets, and AWS access in the cluster is a keyless IAM role (see [k8s/README.md](../k8s/README.md#aws-access-no-keys)).

| Workflow | Trigger | What it does |
|---|---|---|
| [`ci.yml`](../.github/workflows/ci.yml) | every pull request; called by `deploy-stage.yml` | backend `dotnet test`; typecheck, test and build of `chat-protocol`, `chat-widget`, `builders`, `consent-protocol`, `mock-oidc`; Flutter analyze + test of the authenticator (non-blocking until T-072); Docker builds of both images (no push); `kubectl kustomize` of both overlays |
| [`deploy-stage.yml`](../.github/workflows/deploy-stage.yml) | push to `main` (except `kanban/**`, `docs/**`, `*.md`) and manual run | runs `ci.yml`; then, in the `Stage` environment: pushes `scholtz2/biatec-chat-backend` and `scholtz2/biatec-chat-builders` tagged `1.<yyyy.mm.dd>-<sha7>-beta`, generates DB passwords once, optionally syncs `biatec-chat-secrets`, applies `k8s/stage` (backend, builders, Postgres, Redis) with that tag, migrates the database, waits for the rollouts |

Production promotion is not automated yet (follow the BiatecDEX `promote-production.yml` pattern: re-tag the verified
stage image, never rebuild).

## Secrets for the `Stage` environment

Set in GitHub → Settings → Environments → `Stage` (add required reviewers there if deploys should be approved). Where
the AWS values come from is in [aws-setup.md](aws-setup.md) §3.

| Secret | Required | Value |
|---|---|---|
| `DOCKERHUB_USER` | yes | Docker Hub user that can push `scholtz2/biatec-chat-*` |
| `DOCKERHUB_TOKEN` | yes | Docker Hub access token (read/write) for that user |
| `KUBE_CONFIG` | yes | `base64 -w0` of a kubeconfig allowed to manage namespace `biatec-chat-stage` (deployments, statefulsets, services, ingress, network policies, config maps, secrets, service accounts, `pods/exec` for the migration; and create the namespace on the first run) |
| `OIDC_CLIENT_SECRET` | optional* | Biatec OIDC client secret of the `biatec-chat` tenant on stage OIDC |
| `KMS_KEY_ID` | optional* | ARN of the token-vault key (`infra/aws/kms-key.sh`) |
| `AWS_ROLE_ARN` | optional* | role from `infra/aws/k8s-role.sh`, e.g. `arn:aws:iam::<account>:role/biatec-chat-k8s-role` |

\* All three or none. When `OIDC_CLIENT_SECRET` is set, every deploy recreates `biatec-chat-secrets` from them and
fails if one is missing; when it isn't, the cluster's `biatec-chat-secrets` is left alone (managed out of band).

**No database credentials.** Postgres and Redis run in the namespace. On the first deploy the workflow creates
`biatec-chat-generated` with random `postgres-password`, `redis-password` and `builders-internal-secret`; later deploys
keep it. The backend builds its connection strings from those passwords and the in-cluster service names, so they are
configuration, not secrets. Deleting `biatec-chat-generated` without also deleting the Postgres volume locks the backend
out (Postgres keeps the password it was initialised with).

## What a deploy does

1. Runs `ci.yml`. 2. Builds and pushes both images. 3. Ensures the namespace and `biatec-chat-generated`, syncs
`biatec-chat-secrets`. 4. `kubectl apply -k k8s/stage` with the pinned tag. 5. Waits for Postgres and Redis.
6. Applies database migrations: `dotnet ef migrations script --idempotent` piped into `psql` in the Postgres pod (the
backend migrates on startup only in Development). 7. Waits for builders and backend rollouts.

## First deploy checklist

Follow [aws-setup.md](aws-setup.md) §3 (AWS, cluster, GitHub secrets), then run `deploy-stage` manually and seed the
`biatec-chat` tenant row (k8s/README.md).
