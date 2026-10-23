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
| [`ci.yml`](../.github/workflows/ci.yml) | every pull request; called by `deploy-stage.yml`; manual run | backend `dotnet test` (with Postgres and Redis containers); build of `chat-protocol`, `chat-widget` and `builders`, typecheck of `chat-widget` and `builders`, tests of `chat-widget`, `builders`, `consent-protocol` and `mock-oidc`, plus the consent conformance suite and the `evidence-verifier` tests (`e2e` is not run in CI); the demo site (`pnpm --filter demo build`, `pnpm --filter demo test`); Flutter analyze + test of the authenticator; Docker builds (no push) of the backend, builders, mock OIDC and demo images, the demo with `BACKEND_URL=https://backend.invalid`; `kubectl kustomize` of both overlays; the deploy RBAC check (`k8s/rbac/check_role.py`, its self-test and the preflight test) |
| [`deploy-stage.yml`](../.github/workflows/deploy-stage.yml) | push to `main` (except `kanban/**`, `docs/**`, `*.md`) and manual run | runs `ci.yml`, builds and pushes `scholtz2/biatec-chat-backend`, `scholtz2/biatec-chat-builders` and `scholtz2/biatec-chat-demo` (built with `BACKEND_URL` from `STAGE_URL`) as `<version>-beta` and the moving `stage` tag, then `deploy.yml` to `Stage` with `url` = `STAGE_URL` |
| [`promote-production.yml`](../.github/workflows/promote-production.yml) | manual only (Actions → promote-production → Run workflow) | takes a stage version (empty = what `stage` runs now), re-tags the backend and builders images `<version>-beta` → `<version>-main` without rebuilding (same digest, checked; the demo image is not re-tagged), then `deploy.yml` to `Production` with `url` `https://chat.biatec.io` |
| [`publish-widget.yml`](../.github/workflows/publish-widget.yml) | push to `main` and pull requests touching `packages/chat-widget/**`, `packages/chat-protocol/**` or the workflow; `chat-widget-v<version>` tag push; manual run (publishes only with "dry_run" unticked) | **Releasing = merge a PR that bumps `version` in `packages/chat-widget/package.json` and adds its CHANGELOG.md section.** First checks whether the version changed in the push and whether it is already on npm (public registry, no auth). Push to `main` with a new, unpublished version: builds, typechecks and tests `@biatec/chat-widget`, packs it, publishes the tarball to npm with provenance in the `npm` environment, then (job `release`, the only one with `contents: write`) creates the tag `chat-widget-v<version>` and a GitHub release with that CHANGELOG section. Unchanged or already published version: publishes nothing, `::notice` says why (a `::warning` when the unchanged version is not on npm, i.e. an earlier release failed). Only a version greater than the highest published release (semver) is published; a lower or equal one fails the check. Prereleases (`x.y.z-<id>…`) go to the npm dist-tag `<id>` (`next` if it isn't a word), never `latest`, and get a GitHub prerelease. Tag push or manual non-dry run: the same publish, tag and release for the version in `package.json`, only for a commit on `main` (other refs fail), skipped if that version is on npm, so the workflow's own tag never publishes twice; the publish job re-checks npm right before publishing, so a tag and a merge racing for one version publish it once. Pull requests and dry runs: build, test, pack, `npm publish --dry-run` (skipped with a notice for a version already on npm, which npm rejects), and (for a PR into `main`) a notice saying whether merging would publish. Auth: npm trusted publishing (OIDC); fallback the `npm` environment secret `NPM_TOKEN` (0.1.0, the first publish, is done). See [packages/chat-widget/README.md, "Publishing"](../packages/chat-widget/README.md#publishing) |
| [`deploy.yml`](../.github/workflows/deploy.yml) | called by the two above | in the given environment (namespace must already exist): syncs `biatec-chat-generated`, `biatec-chat-secrets` and `biatec-chat-aws`, pins the tag in the overlay, renders it with `kubectl kustomize` and applies the result, waits for Postgres and Redis, applies database migrations, upserts the tenant, registers the demo origin when the overlay has the demo (see "Demo site"), waits for builders, backend and, when present, the demo |
| [`kanban-sync.yml`](../.github/workflows/kanban-sync.yml) | push to `main`; manual run | marks merged task PRs done on the kanban board, moves unblocked dependants to backlog and regenerates `kanban/BOARD.md` (commits to `main`) |

Production is never deployed by a push. Production deploys need a human approval: the `Production` environment must have required
reviewers (see "The deploy identity" below for the commands); the deploy job then waits for approval. A single reviewer who also starts the promotion can approve their own run; add a second reviewer and set `prevent_self_review` if the team wants a real second pair of eyes.

## Secrets

Settings → Secrets and variables → Actions:

| Secret | Where | Value |
|---|---|---|
| `DOCKERHUB_USER` | repository | Docker Hub user that can push `scholtz2/biatec-chat-*` |
| `DOCKERHUB_TOKEN` | repository | Docker Hub access token (read/write) for that user |
| `KUBE_CONFIG` | `Stage` and `Production` environments, each its own | `base64 -w0` of a kubeconfig for the deploy identity of that environment: a service account bound to the scoped Role in [`k8s/rbac/`](../k8s/rbac/deploy-role.yaml) (deployments, statefulsets, services, ingresses, network policies, config maps, service accounts, three named secrets, `pods/exec` on the Postgres pod only) with a time-limited token. The namespace must already exist (see "Namespaces"); the deploy never creates it |
| `OIDC_CLIENT_ID` | `Stage` and `Production` | Client id of the `biatec-chat` client at that environment's Biatec OIDC, e.g. `biatec-chat-stage` on stage.oidc.biatec.io. Its allowed redirect URI there must include `https://<host>/auth/callback` (`stage.chat.biatec.io` / `chat.biatec.io`) |
| `OIDC_CLIENT_SECRET` | `Stage` and `Production` | Biatec OIDC client secret of the `biatec-chat` tenant at that environment's OIDC (stage.oidc.biatec.io / oidc.biatec.io) |
| `AWS_ROLE_ARN` | `Stage` and `Production` | Bedrock role from `infra/aws/rolesanywhere.sh` (aws-setup.md §3), e.g. `arn:aws:iam::<account>:role/biatec-chat-bedrock-role` |
| `AWS_TRUST_ANCHOR_ARN`, `AWS_PROFILE_ARN` | `Stage` and `Production` | IAM Roles Anywhere trust anchor and profile ARNs, printed by the same script |
| `AWS_CLIENT_CERT`, `AWS_CLIENT_KEY` | `Stage` and `Production`, each its own | PEM client certificate and private key for that environment (`rolesanywhere-out/stage/…` for `Stage`, `…/main/…` for `Production`); expire after 90 days (`CERT_DAYS`), see aws-setup.md "Renewing" |
| `POSTGRES_PASSWORD`, `REDIS_PASSWORD` | `Stage` and `Production`, each its own | Passwords of the in-cluster Postgres and Redis: `openssl rand -hex 24`. **Never change `POSTGRES_PASSWORD` after the first deploy** (Postgres reads it only when its volume is first created) |
| `BUILDERS_INTERNAL_SECRET` | `Stage` and `Production`, each its own | Shared secret between backend and builders: `openssl rand -hex 32` |
| `VAULT_MASTER_KEY` | `Stage` and `Production`, each its own | Token-vault master key, base64 of 32 bytes: `openssl rand -base64 32`. Losing it signs every user out; GitHub never shows a secret again, so keep a copy in the team password manager |
| `NPM_TOKEN` | `npm` environment only (optional) | **Fallback only** for `publish-widget.yml` when npm trusted publishing (GitHub OIDC, no secret) is not configured yet, and required for the very first publish of `@biatec/chat-widget` through the workflow (a trusted publisher needs an existing package): an npmjs.com granular access token with read and write on the `@biatec` packages and a short expiry. A repository admin adds it under Settings → Environments → `npm` of scholtz/biatec-chat, and deletes it once the trusted publisher is configured. See [packages/chat-widget/README.md, "Publishing"](../packages/chat-widget/README.md#publishing) |
| `EVIDENCE_MASTER_KEY` | `Stage` and `Production`, each its own | Evidence-signing master key, base64 of at least 32 bytes: `openssl rand -base64 32`. All replicas derive the same ES256 keys from it; back it up and never change it |

**Who creates which secret.** Credentials only a person can issue (`KUBE_CONFIG`, `OIDC_CLIENT_ID`, `OIDC_CLIENT_SECRET`, the
five `AWS_*`) are GitHub environment secrets and the deploy fails with a clear message if one is missing. The five random
values (`POSTGRES_PASSWORD`, `REDIS_PASSWORD`, `BUILDERS_INTERNAL_SECRET`, `VAULT_MASTER_KEY`, `EVIDENCE_MASTER_KEY`) are
optional as GitHub secrets: each deploy uses the GitHub secret if it is set (to pin or rotate a value), otherwise the
value already in the cluster, otherwise it generates one (`openssl rand`) and keeps it in the cluster, so a new
environment needs none of them. The deploy log says per key where the value came from (never the value). Each deploy
writes three cluster secrets: `biatec-chat-generated` (those five; the backend builds its connection strings from them
and the in-cluster service names), `biatec-chat-secrets` (`OIDC_CLIENT_SECRET`, and the optional `ADMINS`: emails separated by `;` who may open the
[administrator dashboard](admin-dashboard.md), empty means off; `OIDC_CLIENT_ID` goes into the tenant row,
see "Tenant") and `biatec-chat-aws` (certificate, key and the AWS config file; the five `AWS_*` secrets are validated:
certificate matches key, expiry warning). Postgres is the exception to rotating by deploy (above). Generated values exist
only in the cluster secret, so back up `vault-master-key` and `evidence-master-key` from it
(`kubectl get secret biatec-chat-generated -n <ns> -o jsonpath='{.data.vault-master-key}' | base64 -d`).

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

### Mounted hosts

A site can serve this backend under a path prefix of its own origin (Biatec DEX beta: `https://beta.dex.biatec.io/chat`,
[integration-biatec-dex.md](integration-biatec-dex.md) §7). The overlay lists it in the backend ConfigMap
(`Hosting__Mounts__0__Host`, `__Prefix`, `__ClientKey` in `k8s/stage/backend-config.patch.yaml`); the backend serves that
host only under the prefix. The route to it is `k8s/stage/dex-beta-ingress.yaml`, an Ingress for the DEX host on this cluster: the DEX cluster (another cluster) forwards `/chat` over HTTPS to `stage.chat.biatec.io` with the Host header kept, and this Ingress routes that Host to the backend. After the migration the step "Provision tenants of mounted hosts" reads those keys from the
rendered overlay and upserts one tenant per mount, id `mount-<host with dashes>`: a copy of `biatec-chat` (same OIDC
client and secret pointer, limits) with its own host, sign-in page and allowed origin (`https://<host>`), the mount's
client key and the redirect URI `https://<host><prefix>/auth/callback`. The overlay stays the source of truth for host,
sign-in page, OIDC client and redirect URI, which a later deploy rewrites; the allowed origin and the client key are only
ensured present (an origin or key an operator added is never removed, and a key dropped from the overlay stays until it is
removed by hand; changing `Hosting__Mounts__0__ClientKey` adds the new key). Limits and budgets are copied once and then
belong to onboarding. Nothing privileged is inherited: the new tenant starts with limits updates, x402 payments and the
trust-anchor flag off, a deploy never changes them, and an operator enables what the site needs through the tenant policy
API. The step fails unless the row afterwards holds the redirect URI, the origin and the client key. The
mounts are validated before anything is applied (same rules as the backend's startup validation, plus the client key and
that two hosts do not map to the same tenant id, and a prefix that does not start with `/health` or `/onboarding`; the step also fails if the ConfigMap is not found in the rendered overlay, if an entry lacks Host, Prefix or ClientKey, if a key is defined twice in different case or a value spans lines (keys are read case-insensitively, like .NET configuration), or if another tenant already owns the host or the derived tenant id),
so a bad overlay fails the deploy instead of crash-looping the backend. The client key is read by the deploy only; the
backend ignores it. The backend reads the mounts once at startup; an overlay change always arrives with a new commit, hence a
new image tag and a rollout, which is what applies it (re-running a deploy of the same commit changes nothing). The redirect URI must be exactly `https://<host><prefix>/auth/callback`
(the OIDC callback is that stored value); the backend logs an error when a mounted host's tenant disagrees (the path is compared exactly, case included, as Biatec OIDC does; host spelling and a default port are not) and refuses every `/auth` route but logout for it in one place, the tenant middleware (503, so also the callback and the popup status poll), and its sign-in cards fall back to the sign-in page. The popup,
elevation and card URLs follow the mount, not the stored value. The Biatec OIDC client must allow that redirect URI.
Production has no mount.

## Database migrations

The backend migrates on startup only in Development. Each deploy runs
`dotnet ef migrations script --idempotent` and pipes it into `psql` in the Postgres pod, which applies just the
migrations missing from `__EFMigrationsHistory`. A migration must therefore be compatible with the previous backend
version for the few seconds both run.

## Namespaces

The deploy account has no cluster-scope rights apart from the stage volumes (see "Storage"), so the namespaces are created once by a
cluster admin; neither the workflow nor the overlays create them. Per environment (`biatec-chat-stage` / `biatec-chat-main`):

```
kubectl create namespace biatec-chat-stage
kubectl label namespace biatec-chat-stage app.kubernetes.io/part-of=biatec-chat environment=stage
kubectl apply -n biatec-chat-stage -f k8s/rbac/deploy-role.yaml
# edit the two placeholders in deploy-rolebinding.yaml (the service account behind KUBE_CONFIG), then
kubectl apply -n biatec-chat-stage -f k8s/rbac/deploy-rolebinding.yaml
```

Do not bind the built-in `admin` ClusterRole: it reads every secret of the namespace, edits RBAC and execs into any pod.

## The deploy identity

What `KUBE_CONFIG` can do is the blast radius of anyone who can run a workflow in the environment or read the secret
(BCHAT-020). Three layers limit it:

1. **A scoped Role.** [`k8s/rbac/deploy-role.yaml`](../k8s/rbac/deploy-role.yaml) lists exactly what `deploy.yml` does: apply the
   overlay's kinds (get, create, patch, no delete), read and patch only the three secrets it writes, read pods, claims, events and
   logs for diagnostics, and `pods/exec` on `biatec-chat-postgres-0` only. It has no secrets list, no RBAC objects, no wildcards.
   `python k8s/rbac/check_role.py` renders both overlays, reads `deploy.yml` and fails when a resource, a secret or the exec target
   is not covered or when a rule is too wide (`--self-test` proves it rejects a list of deliberately bad roles and kubectl calls); CI runs it, so a new kind in an
   overlay or a new secret in the workflow cannot merge without a conscious change of the Role.
2. **A time-limited token.** Issue the kubeconfig token for the service account with an expiry and replace the secret before it lapses:
   ```
   kubectl create token <service-account> -n <its namespace> --duration=720h     # 30 days; the API server may cap it
   ```
   Build the kubeconfig with that token, `base64 -w0` it and set it: `gh secret set KUBE_CONFIG --env Stage --body "<base64>"`.
   The deploy's "Check the deploy identity" step (`k8s/rbac/preflight.sh`) fails with a clear message when the token has expired,
   and warns when it expires within 7 days, when it has no expiry, and when the identity can read every secret or edit RBAC (that is,
   it still has the built-in `admin` role). The warnings do not fail the deploy, so an environment can move to the scoped Role when
   the cluster admin gets to it; once it has, set the environment variable `STRICT_IDENTITY` to `1` (Settings → Environments → Variables)
   and they do.
3. **A human in front of Production, and only `main`.** These are repository settings, not files; run them once as a repository admin
   (replace `<id>` with the user or team id from `gh api users/<login>` or `gh api orgs/<org>/teams/<slug>`):
   ```
   gh api -X PUT repos/scholtz/biatec-chat/environments/Production --input - <<'JSON'
   {"reviewers": [{"type": "User", "id": <id>}],
    "deployment_branch_policy": {"protected_branches": false, "custom_branch_policies": true}}
   JSON
   gh api -X POST repos/scholtz/biatec-chat/environments/Production/deployment-branch-policies -f name=main -f type=branch
   gh api -X PUT repos/scholtz/biatec-chat/environments/Stage --input - <<'JSON'
   {"deployment_branch_policy": {"protected_branches": false, "custom_branch_policies": true}}
   JSON
   gh api -X POST repos/scholtz/biatec-chat/environments/Stage/deployment-branch-policies -f name=main -f type=branch
   ```
   A branch policy means a workflow on another branch cannot read the environment's secrets; it also blocks a manual run of a deploy
   workflow from a task branch ("Branch is not allowed to deploy"). `PUT` replaces the environment's whole protection configuration, so read
   it first (`gh api repos/scholtz/biatec-chat/environments/Production`) and repeat any wait timer or reviewer you want to keep.
   Required reviewers also hold the weekly `rolesanywhere-expiry` check of Production until someone approves it; approve it, or accept
   that Stage's check is the only unattended one.

What the Role does not do: it does not keep the identity away from the namespace's secrets altogether. Anyone holding the token (or
able to change `deploy.yml` and run it in the environment) can still create a Deployment that mounts a secret of the namespace and read
it from the pod's log, or create a service-account-token secret, and a hostPath volume (stage uses them) can expose node paths to a
workload that mounts it. What the Role takes away are the other rights of `admin`: deleting objects, editing RBAC, reading secrets
directly, exec into any pod. The token lifetime, the branch policy and the reviewers are what limit who can hold the identity, so all
four layers belong together.

## Storage

The cluster has no dynamic volume provisioner and no default StorageClass, so Postgres and Redis (StatefulSet claims
`data-biatec-chat-postgres-0` and `data-biatec-chat-redis-0`, which set no class) are bound to fixed volumes. On stage
they are `hostPath` PersistentVolumes in [k8s/stage/storage.yaml](../k8s/stage/storage.yaml), pinned to node `sch-g-04`
and pre-bound to their claims, under `/mnt/nvme1/chat-stage/`. They have no class on purpose: a claim without a class
binds only to a volume without one. `Retain` keeps the data on the drive if a claim is deleted. The deploy account
needs `persistentvolumes` (cluster-scoped): [`k8s/rbac/deploy-pv-clusterrole.yaml`](../k8s/rbac/deploy-pv-clusterrole.yaml) grants
create, and get and patch on the two stage volumes by name, nothing else. Production has no volumes yet.

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
