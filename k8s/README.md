# Kubernetes manifests

Kustomize overlays: `base/` (backend + builders sidecar), `main/` (chat.biatec.io) and `stage/` (stage.chat.biatec.io).
Not applied by this repository.

Validate offline: `kubectl kustomize k8s/stage` and `kubectl kustomize k8s/main` must render. `kubectl apply --dry-run=client`
still needs a reachable API server for discovery (even with `--validate=false`), so run it against a kind cluster.

## Environment-specific endpoints

`base/` holds no environment endpoints. Each overlay sets them in `backend-config.patch.yaml` (ConfigMap:
`Oidc__Issuer`, `Oidc__McpResource`, `Mcp__Endpoint`, `Algod__Networks__*`) and `builders-env.patch.yaml` (`ALGOD_URL_*`).
`main` uses `oidc.biatec.io` and `mcp.biatec.io/tools`; `stage` uses `stage.oidc.biatec.io` and `stage.mcp.biatec.io/tools`
(per `docs/AWS-AGENTCORE-GUIDE.md`). Both use the public Nodely mainnet/testnet algod; change per overlay if stage needs other nodes.

## Dry-run status

`kubectl kustomize` renders both overlays offline (the substitute used when no cluster is reachable). The literal
`kubectl apply --dry-run=client` step needs an API server (for example a kind cluster) and was not run here; the cluster
owner should run `kubectl kustomize k8s/stage | kubectl apply --dry-run=client -f -`.

## Secrets (never in git)

Every secret is a GitHub environment secret; `deploy.yml` writes them into three cluster secrets on each deploy
(docs/cicd.md): `biatec-chat-secrets` (`oidc-client-secret`), `biatec-chat-generated` (`postgres-password`,
`redis-password`, `builders-internal-secret`, `vault-master-key`; the name is from an earlier design when they were
generated in the cluster) and `biatec-chat-aws` (Roles Anywhere client certificate, key and AWS config file, see "AWS
access"). Postgres and Redis run in the namespace (`base/postgres.yaml`, `base/redis.yaml`, reachable only from the
backend through network policies); the backend's connection strings are built from those passwords. Schema
migrations are a deploy step (docs/cicd.md).

## AWS access (Bedrock only, no keys)

AWS is used for Bedrock (model and guardrail) and nothing else. The backend authenticates with an environment-specific
client certificate through IAM Roles Anywhere: `AWS_CONFIG_FILE` points at the `config` of the `biatec-chat-aws` secret
(mounted at `/var/run/secrets/aws-rolesanywhere`), whose `credential_process` runs the `aws_signing_helper` shipped in
the backend image. This needs nothing from the cluster (no public service-account issuer, no API-server flag). One-time
setup with `infra/aws/rolesanywhere.sh`; the full walk-through is [docs/aws-setup.md](../docs/aws-setup.md).

## Images

`scholtz2/biatec-chat-backend` (`backend/Dockerfile`) and `scholtz2/biatec-chat-builders`
(`builders/sidecar/Dockerfile`); both build with the repository root as context. Startup and readiness probes use `/health` (Redis and PostgreSQL checks); liveness uses `/health/live`, which runs no dependency checks, so a dependency outage removes pods from service without restarting them.

## Deployment prerequisites

- **Image tags.** The `newTag` values in the overlays are placeholders. `.github/workflows/deploy.yml` replaces both with
  the immutable tag it deploys: `1.<date>-<sha7>-beta` on stage (deploy-stage.yml), `1.<date>-<sha7>-main` on production
  (promote-production.yml, the same images re-tagged). Applying an overlay by hand without pinning fails on purpose
  (`REPLACE-WITH-RELEASE-SHA` doesn't exist). See `docs/cicd.md`.
- **Tenants are not seeded by the backend outside Development.** `deploy.yml` upserts the `biatec-chat` tenant row after
  the migration: host from the environment URL, client id from the `OIDC_CLIENT_ID` secret,
  `ClientSecretRef=secret://biatec-chat/oidc-client-secret`.
  The secret itself is the `oidc-client-secret` key above, exposed as `Secrets__biatec-chat__oidc-client-secret`.
- **Vault** uses the `Secret` key provider: `Vault__MasterKey` from `biatec-chat-generated` wraps the per-entry data keys
  (AES-GCM). Losing that key only forces users to sign in again.
- **Evidence signing keys** (`Evidence__KeyStore=Secret`). Add `evidence-master-key` to `biatec-chat-secrets`: base64 of
  at least 32 random bytes (`openssl rand -base64 32`). Every replica derives the same ES256 keys from it (HKDF), and the
  key metadata (kid, public point, created/retired dates, which key is active) lives in the PostgreSQL table
  `EvidenceSigningKeys`, so all replicas publish an identical JWKS and retired keys stay published across restarts and
  rollouts. Rotation is automatic: every replica checks hourly and, when the active key is older than
  `Evidence__RotationDays` (default 90, `0` disables), retires it and activates a new kid (one replica wins; others
  follow within 30 s, signing meanwhile with the retired kid, which still verifies). To rotate sooner, lower
  `Evidence__RotationDays` (for example `1`) and roll out; there is no HTTP endpoint for it. Back the master key up and never change it: a new master cannot sign for the existing kids. Rotation
  retires the active key and activates a new kid; the database holds no private key material.
  Alternative `Evidence__KeyStore=Kms` with `Evidence__Kms__KeyIds__0=<key id>`, `...__1=...` (oldest first): operators
  pre-create ECC_NIST_P256 `SIGN_VERIFY` keys in eu-central-1 tagged `Project=biatec` (about 1 USD/month each plus
  per-sign requests; ask the user first), and the pod role needs `kms:Sign` and `kms:GetPublicKey` on them. The backend
  never creates KMS keys. Outside Development the app refuses to start without a configured store.
- **Evidence anchoring** (`Evidence__Anchoring__Enabled=true`, network `testnet`, set in `base/backend.yaml`, so stage and
  main both anchor). Every 10 minutes the backend writes a Merkle root of the conversation heads into the note of a 0 ALGO
  self-payment from the *evidence account*, a dedicated account whose address is public (publish it so anyone can read
  the anchor chain from the indexer). Create it once per environment and put its 25-word mnemonic in the secret
  `biatec-chat-evidence-anchor`, key `account-mnemonic` (never in the repository or a manifest; the env var is
  `Evidence__Anchoring__AccountMnemonic`):
  `kubectl -n biatec-chat-stage create secret generic biatec-chat-evidence-anchor --from-literal=account-mnemonic="word1 ... word25"`
  (`deploy.yml` does not manage this secret yet; the reference is `optional`, so a missing secret does not stop the pods,
  but anchoring logs an error each interval). **Fund it** from the testnet dispenser (https://bank.testnet.algorand.network/)
  with the address of that mnemonic: each anchor costs 0.001 ALGO, about 144 per day (roughly 0.15 ALGO/day), so 10 testnet
  ALGO lasts months; top up when the balance falls below 1 ALGO. Mainnet is an operator decision (change
  `Evidence__Anchoring__Network` and fund with real ALGO).
  **Two replicas:** every tick takes a PostgreSQL session advisory lock (`pg_try_advisory_lock`) held across the broadcast.
  The replica that gets it anchors; the other skips that tick and logs "Another replica is anchoring". The lock is dropped
  when the connection closes, so a crashed pod never blocks anchoring.
- The backend runs as numeric UID 1654 (`runAsUser` plus `USER 1654` in the Dockerfile).
