# CI/CD

Biatec Chat runs on Biatec's own Kubernetes cluster in two environments. AWS is used for Bedrock only
([aws-setup.md](aws-setup.md)). Nothing in the repository holds a credential: deploy credentials are GitHub secrets.

| Environment | GitHub environment | Overlay | Namespace | Host | Image tag |
|---|---|---|---|---|---|
| stage | `Stage` | `k8s/stage` | `biatec-chat-stage` | stage.chat.biatec.io | `1.<yyyy.mm.dd>-<sha7>-beta` |
| production | `Production` | `k8s/main` | `biatec-chat-main` | chat.biatec.io | `1.<yyyy.mm.dd>-<sha7>-main` (same image) |

## Workflows

| Workflow | Trigger | What it does |
|---|---|---|
| [`ci.yml`](../.github/workflows/ci.yml) | every pull request; called by `deploy-stage.yml` | backend `dotnet test` (with Postgres and Redis containers); typecheck, test and build of `chat-protocol`, `chat-widget`, `builders`, `consent-protocol`, `mock-oidc`; Flutter analyze + test of the authenticator; Docker builds of both images; `kubectl kustomize` of both overlays |
| [`deploy-stage.yml`](../.github/workflows/deploy-stage.yml) | push to `main` (except `kanban/**`, `docs/**`, `*.md`) and manual run | runs `ci.yml`, builds and pushes `scholtz2/biatec-chat-backend` and `scholtz2/biatec-chat-builders` as `<version>-beta` and the moving `stage` tag, then `deploy.yml` to `Stage` |
| [`promote-production.yml`](../.github/workflows/promote-production.yml) | manual only (Actions → promote-production → Run workflow) | takes a stage version (empty = what `stage` runs now), re-tags both images `<version>-beta` → `<version>-main` without rebuilding (same digest, checked), then `deploy.yml` to `Production` |
| [`publish-widget.yml`](../.github/workflows/publish-widget.yml) | `chat-widget-v<version>` tag push, manual run (publishes only with "dry_run" unticked), and pull requests touching the widget (dry run only) | builds, typechecks and tests `@biatec/chat-widget`, packs it, `npm publish --dry-run`; on a tag or a manual non-dry run, publishes the tarball to npm with provenance in the `npm` environment. See [packages/chat-widget/README.md, "Publishing"](../packages/chat-widget/README.md#publishing) |
| [`deploy.yml`](../.github/workflows/deploy.yml) | called by the two above | in the given environment: ensures the namespace and the generated secrets, syncs `biatec-chat-secrets`, pins the tag in the overlay, `kubectl apply -k`, waits for Postgres and Redis, applies database migrations, waits for builders and backend |

Production is never deployed by a push. To approve production deploys, add required reviewers to the `Production`
environment (Settings → Environments → Production → Required reviewers); the deploy job then waits for approval.

## Secrets

Settings → Secrets and variables → Actions:

| Secret | Where | Value |
|---|---|---|
| `DOCKERHUB_USER` | repository | Docker Hub user that can push `scholtz2/biatec-chat-*` |
| `DOCKERHUB_TOKEN` | repository | Docker Hub access token (read/write) for that user |
| `KUBE_CONFIG` | `Stage` and `Production` environments, each its own | `base64 -w0` of a kubeconfig allowed to manage that environment's namespace: deployments, statefulsets, services, ingresses, network policies, config maps, secrets, service accounts, persistent volume claims, `pods/exec` (migration). The namespace must already exist (see below); the deploy never creates it |
| `OIDC_CLIENT_ID` | `Stage` and `Production` | Client id of the `biatec-chat` client at that environment's Biatec OIDC, e.g. `biatec-chat-stage` on stage.oidc.biatec.io. Its allowed redirect URI there must include `https://<host>/auth/callback` (`stage.chat.biatec.io` / `chat.biatec.io`) |
| `OIDC_CLIENT_SECRET` | `Stage` and `Production` | Biatec OIDC client secret of the `biatec-chat` tenant at that environment's OIDC (stage.oidc.biatec.io / oidc.biatec.io) |
| `AWS_ROLE_ARN` | `Stage` and `Production` | Bedrock role from `infra/aws/rolesanywhere.sh` (aws-setup.md §3), e.g. `arn:aws:iam::<account>:role/biatec-chat-bedrock-role` |
| `AWS_TRUST_ANCHOR_ARN`, `AWS_PROFILE_ARN` | `Stage` and `Production` | IAM Roles Anywhere trust anchor and profile ARNs, printed by the same script |
| `AWS_CLIENT_CERT`, `AWS_CLIENT_KEY` | `Stage` and `Production`, each its own | PEM client certificate and private key for that environment (`rolesanywhere-out/stage/…` for `Stage`, `…/main/…` for `Production`); expire after 365 days, see aws-setup.md "Renewing" |

| `POSTGRES_PASSWORD`, `REDIS_PASSWORD` | `Stage` and `Production`, each its own | Passwords of the in-cluster Postgres and Redis: `openssl rand -hex 24`. **Never change `POSTGRES_PASSWORD` after the first deploy** (Postgres reads it only when its volume is first created) |
| `BUILDERS_INTERNAL_SECRET` | `Stage` and `Production`, each its own | Shared secret between backend and builders: `openssl rand -hex 32` |
| `VAULT_MASTER_KEY` | `Stage` and `Production`, each its own | Token-vault master key, base64 of 32 bytes: `openssl rand -base64 32`. Losing it signs every user out; GitHub never shows a secret again, so keep a copy in the team password manager |
| `NPM_TOKEN` | `npm` environment only (optional) | **Fallback only** for `publish-widget.yml` when npm trusted publishing (GitHub OIDC, no secret) is not configured yet: an npmjs.com granular access token with read and write on the `@biatec` packages and a short expiry. A repository admin adds it. See [packages/chat-widget/README.md, "Publishing"](../packages/chat-widget/README.md#publishing) |
| `EVIDENCE_MASTER_KEY` | `Stage` and `Production`, each its own | Evidence-signing master key, base64 of at least 32 bytes: `openssl rand -base64 32`. All replicas derive the same ES256 keys from it; back it up and never change it |

**Every secret lives in GitHub, none is generated in the cluster.** Each deploy writes the environment's secrets into
three cluster secrets and fails with a clear message if one is missing: `biatec-chat-generated` (the four random values
above, name kept from the earlier design; the backend builds its connection strings from them and the in-cluster service
names), `biatec-chat-secrets` (`OIDC_CLIENT_SECRET`; `OIDC_CLIENT_ID` goes into the tenant row, see "Tenant") and `biatec-chat-aws` (certificate, key and the AWS config file;
the five `AWS_*` secrets are validated: certificate matches key, expiry warning). To rotate a value, set the GitHub
secret and deploy; Postgres is the exception above.

## Tenant

The backend finds its OIDC client through the `Tenants` table, which it seeds only in Development. After the migration,
each deploy upserts the tenant `biatec-chat`: host from the environment URL, `OidcClientId` from `OIDC_CLIENT_ID`,
`ClientSecretRef` `secret://biatec-chat/oidc-client-secret` (the `OIDC_CLIENT_SECRET` secret) and `RedirectUri`
`https://<host>/auth/callback`. On later deploys only those four columns are updated; limits, origins and other settings
changed through onboarding stay. Changing `OIDC_CLIENT_ID` and deploying switches the client.

## Database migrations

The backend migrates on startup only in Development. Each deploy runs
`dotnet ef migrations script --idempotent` and pipes it into `psql` in the Postgres pod, which applies just the
migrations missing from `__EFMigrationsHistory`. A migration must therefore be compatible with the previous backend
version for the few seconds both run.

## Namespaces

The deploy account has no cluster-scope rights, so the namespaces are created once by a cluster admin; neither the
workflow nor the overlays create them. Per environment (`biatec-chat-stage` / `biatec-chat-main`):

```
kubectl create namespace biatec-chat-stage
kubectl label namespace biatec-chat-stage app.kubernetes.io/part-of=biatec-chat environment=stage
kubectl create rolebinding github-actions-deploy -n biatec-chat-stage \
  --clusterrole=admin --serviceaccount=<namespace of the account>:<service account of KUBE_CONFIG>
```

## Storage

The cluster has no dynamic volume provisioner and no default StorageClass, so Postgres and Redis (StatefulSet claims
`data-biatec-chat-postgres-0` and `data-biatec-chat-redis-0`, which set no class) are bound to fixed volumes. On stage
they are `hostPath` PersistentVolumes in [k8s/stage/storage.yaml](../k8s/stage/storage.yaml), pinned to node `sch-g-04`
and pre-bound to their claims, under `/mnt/nvme1/chat-stage/`. They have no class on purpose: a claim without a class
binds only to a volume without one. `Retain` keeps the data on the drive if a claim is deleted. The deploy account
needs `persistentvolumes` (cluster-scoped; `create-token.sh` grants it).

The kubelet does not apply `fsGroup` to hostPath directories, and the databases run as user 999, so the directories are
created and owned once, on the node, before the first deploy:

```
mkdir -p /mnt/nvme1/chat-stage/postgres /mnt/nvme1/chat-stage/redis
chown 999:999 /mnt/nvme1/chat-stage/postgres /mnt/nvme1/chat-stage/redis
```

Both ingresses use the `letsencrypt-dns` ClusterIssuer (DNS-01), so the certificate for the host does not need the ingress to be reachable first.

Production (`k8s/main`) has no volumes defined yet: add a `storage.yaml` for it the same way (own directory, claims
`data-biatec-chat-postgres-0` / `data-biatec-chat-redis-0` in `biatec-chat-main`) before its first deploy.

## First deploy

1. AWS part: [aws-setup.md](aws-setup.md) §3 (guardrail exists; Roles Anywhere role, certificates and the five `AWS_*` secrets).
2. Secrets above, `Stage` first.
3. Merge to `main` or run `deploy-stage` manually; check https://stage.chat.biatec.io/health.
4. Check the tenant: the deploy created the `biatec-chat` row for the host with `OIDC_CLIENT_ID` (see "Tenant").
5. When stage is verified: set the `Production` secrets and run `promote-production`.
