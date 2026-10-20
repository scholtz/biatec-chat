# CI/CD

Biatec Chat runs on Biatec's own Kubernetes cluster in two environments. AWS is used for Bedrock only
([aws-setup.md](aws-setup.md)). Nothing in the repository holds a credential: deploy credentials are GitHub secrets.

| Environment | GitHub environment | Overlay | Namespace | Host | Image tag |
|---|---|---|---|---|---|
| stage | `Stage` | `k8s/stage` | `biatec-chat-stage` | stage.chat.biatec.io | `1.<yyyy.mm.dd>-<sha7>-beta` |
| production | `Production` | `k8s/main` | `biatec-chat-main` | chat.biatec.io | `1.<yyyy.mm.dd>-<sha7>-main` (same image) |

Stage also runs the demo site on demo.stage.chat.biatec.io (`scholtz2/biatec-chat-demo`, same `<version>-beta` tag);
production has none. See "Demo site" below and [demo-site.md](demo-site.md).

## Workflows

| Workflow | Trigger | What it does |
|---|---|---|
| [`ci.yml`](../.github/workflows/ci.yml) | every pull request; called by `deploy-stage.yml`; manual run | backend `dotnet test` (with Postgres and Redis containers); build of `chat-protocol`, `chat-widget` and `builders`, typecheck of `chat-widget` and `builders`, tests of `chat-widget`, `builders`, `consent-protocol` and `mock-oidc` (the consent conformance suite, `evidence-verifier` and `e2e` are not run in CI); the demo site (`pnpm --filter demo build`, `pnpm --filter demo test`); Flutter analyze + test of the authenticator; Docker builds (no push) of the backend, builders and demo images, the demo with `BACKEND_URL=https://backend.invalid`; `kubectl kustomize` of both overlays |
| [`deploy-stage.yml`](../.github/workflows/deploy-stage.yml) | push to `main` (except `kanban/**`, `docs/**`, `*.md`) and manual run | runs `ci.yml`, builds and pushes `scholtz2/biatec-chat-backend`, `scholtz2/biatec-chat-builders` and `scholtz2/biatec-chat-demo` (built with `BACKEND_URL` from `STAGE_URL`) as `<version>-beta` and the moving `stage` tag, then `deploy.yml` to `Stage` with `url` = `STAGE_URL` |
| [`promote-production.yml`](../.github/workflows/promote-production.yml) | manual only (Actions → promote-production → Run workflow) | takes a stage version (empty = what `stage` runs now), re-tags the backend and builders images `<version>-beta` → `<version>-main` without rebuilding (same digest, checked; the demo image is not re-tagged), then `deploy.yml` to `Production` with `url` `https://chat.biatec.io` |
| [`publish-widget.yml`](../.github/workflows/publish-widget.yml) | push to `main` and pull requests touching `packages/chat-widget/**`, `packages/chat-protocol/**` or the workflow; `chat-widget-v<version>` tag push; manual run (publishes only with "dry_run" unticked) | **Releasing = merge a PR that bumps `version` in `packages/chat-widget/package.json` and adds its CHANGELOG.md section.** First checks whether the version changed in the push and whether it is already on npm (public registry, no auth). Push to `main` with a new, unpublished version: builds, typechecks and tests `@biatec/chat-widget`, packs it, publishes the tarball to npm with provenance in the `npm` environment, then (job `release`, the only one with `contents: write`) creates the tag `chat-widget-v<version>` and a GitHub release with that CHANGELOG section. Unchanged or already published version: publishes nothing, `::notice` says why (a `::warning` when the unchanged version is not on npm, i.e. an earlier release failed). Only a version greater than the highest published release (semver) is published; a lower or equal one fails the check. Prereleases (`x.y.z-<id>…`) go to the npm dist-tag `<id>` (`next` if it isn't a word), never `latest`, and get a GitHub prerelease. Tag push or manual non-dry run: the same publish, tag and release for the version in `package.json`, only for a commit on `main` (other refs fail), skipped if that version is on npm, so the workflow's own tag never publishes twice; the publish job re-checks npm right before publishing, so a tag and a merge racing for one version publish it once. Pull requests and dry runs: build, test, pack, `npm publish --dry-run` (skipped with a notice for a version already on npm, which npm rejects), and (for a PR into `main`) a notice saying whether merging would publish. Auth: npm trusted publishing (OIDC); fallback the `npm` environment secret `NPM_TOKEN` (0.1.0, the first publish, is done). See [packages/chat-widget/README.md, "Publishing"](../packages/chat-widget/README.md#publishing) |
| [`deploy.yml`](../.github/workflows/deploy.yml) | called by the two above | in the given environment (namespace must already exist): syncs `biatec-chat-generated`, `biatec-chat-secrets` and `biatec-chat-aws`, pins the tag in the overlay, renders it with `kubectl kustomize` and applies the result, waits for Postgres and Redis, applies database migrations, upserts the tenant, registers the demo origin when the overlay has the demo (see "Demo site"), waits for builders, backend and, when present, the demo |
| [`kanban-sync.yml`](../.github/workflows/kanban-sync.yml) | push to `main`; manual run | marks merged task PRs done on the kanban board, moves unblocked dependants to backlog and regenerates `kanban/BOARD.md` (commits to `main`) |

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
| `NPM_TOKEN` | `npm` environment only (optional) | **Fallback only** for `publish-widget.yml` when npm trusted publishing (GitHub OIDC, no secret) is not configured yet, and required for the very first publish of `@biatec/chat-widget` through the workflow (a trusted publisher needs an existing package): an npmjs.com granular access token with read and write on the `@biatec` packages and a short expiry. A repository admin adds it under Settings → Environments → `npm` of scholtz/biatec-chat, and deletes it once the trusted publisher is configured. See [packages/chat-widget/README.md, "Publishing"](../packages/chat-widget/README.md#publishing) |
| `EVIDENCE_MASTER_KEY` | `Stage` and `Production`, each its own | Evidence-signing master key, base64 of at least 32 bytes: `openssl rand -base64 32`. All replicas derive the same ES256 keys from it; back it up and never change it |

**Every secret lives in GitHub, none is generated in the cluster.** Each deploy writes the environment's secrets into
three cluster secrets and fails with a clear message if one is missing: `biatec-chat-generated` (`POSTGRES_PASSWORD`, `REDIS_PASSWORD`, `BUILDERS_INTERNAL_SECRET`, `VAULT_MASTER_KEY` and
`EVIDENCE_MASTER_KEY`, name kept from the earlier design; the backend builds its connection strings from them and the in-cluster service
names), `biatec-chat-secrets` (`OIDC_CLIENT_SECRET`; `OIDC_CLIENT_ID` goes into the tenant row, see "Tenant") and `biatec-chat-aws` (certificate, key and the AWS config file;
the five `AWS_*` secrets are validated: certificate matches key, expiry warning). To rotate a value, set the GitHub
secret and deploy; Postgres is the exception above.

## Tenant

The backend finds its OIDC client through the `Tenants` table, which it seeds only in Development. After the migration,
each deploy upserts the tenant `biatec-chat`: host from the environment URL, `OidcClientId` from `OIDC_CLIENT_ID`,
`ClientSecretRef` `secret://biatec-chat/oidc-client-secret` (the `OIDC_CLIENT_SECRET` secret) and `RedirectUri`
`https://<host>/auth/callback`. On later deploys only those four columns are updated; limits, origins and other settings
changed through onboarding stay. Changing `OIDC_CLIENT_ID` and deploying switches the client. On stage a further step
adds the demo site's origin and client key to the same row (see "Demo site").

## Demo site

The demo site ([demo-site.md](demo-site.md), source in `apps/demo`) runs on stage only.

**`STAGE_URL` is the single source of the stage backend URL.** `deploy-stage.yml` sets it once as a workflow `env`
(`STAGE_URL: https://stage.chat.biatec.io`) and uses it for the demo image's `BACKEND_URL` build argument and, through
the build job's `url` output, as the `url` input of `deploy.yml` (the `Stage` environment URL and the tenant host).
To move the stage backend, change `STAGE_URL` only.

**Image.** `apps/demo/Dockerfile` is built per environment: `BACKEND_URL` has no default and the build fails without
it. It is baked into the bundle (`VITE_BIATEC_CHAT_URL`) and into the CSP (`connect-src` set to the backend's origin in
`apps/demo/security-headers.conf`); the build checks that both hold it, and fails if `dist/` contains anything that
looks like a token, client secret, private key or AWS key. `CLIENT_KEY` defaults to `biatec-demo` (a label, not a
secret). The runtime image is `nginxinc/nginx-unprivileged:1.27-alpine`, user 101, port 8080. `ci.yml` builds it
without pushing with `BACKEND_URL=https://backend.invalid`; `deploy-stage.yml` pushes it as
`scholtz2/biatec-chat-demo:<version>-beta` and `:stage`.

**Detection in `deploy.yml`.** The step "Apply ${{ inputs.overlay }} with tag ${{ inputs.tag }}" (id `apply`) renders
the overlay to a file and looks in it for an Ingress named `biatec-chat-demo` (only `k8s/stage/demo-ingress.yaml` provides one); its host becomes the step output `demo_host`.
Nothing else tells `deploy.yml` about the demo. When `demo_host` is set:

- "Register demo origin on tenant biatec-chat" runs after "Seed tenant biatec-chat". It accepts only a host of
  `[a-z0-9.-]`, requires it to sit under the `url` host minus its first label (`*.chat.biatec.io` for
  `stage.chat.biatec.io`, so same site: the session cookie is `SameSite=Lax`), and appends `https://<demo_host>`
  to `AllowedOrigins` and `biatec-demo` to `ClientKeys` of tenant `biatec-chat` when missing. NULL columns count as
  empty, onboarding's caps apply (10 origins, 20 client keys) and nothing else on the row changes. A final `SELECT`
  must find both on the row, otherwise the step fails (tenant missing, or caps reached).
- "Wait for rollout" also waits for `deployment/biatec-chat-demo`.

**Production.** `k8s/main` deletes the `biatec-chat-demo` Deployment and Service from the base and has no demo
Ingress, so `demo_host` is empty: the production tenant is not touched and no demo rollout is awaited.
`promote-production.yml` does not re-tag the demo image.

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
needs `persistentvolumes` (cluster-scoped, granted by the cluster admin's `create-token.sh`, which is not in this
repository), an exception to the namespace-only rights below.

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
