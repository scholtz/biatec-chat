# Admin API (operators)

PRODUCT-DESCRIPTION.md, Chat Backend Responsibilities: *"Expose health, metrics, and an admin API for policy and
knowledge-base management."* This page documents the tenant policy part (T-086) and knowledge-base management
(T-096, [Knowledge base](#knowledge-base)).

Code: `backend/Biatec.Chat.Api/Admin/`. Tests: `backend/Biatec.Chat.Tests/Admin/`.

## Authentication

- The API is for Biatec Chat operators only. It uses the same operator bearer key as the operator onboarding route
  (`POST /onboarding/operator/tenants`): `Onboarding:OperatorKey`, at least 32 characters.
- The key is injected at deploy time from a Kubernetes secret or AWS Secrets Manager as the environment variable
  `Onboarding__OperatorKey`. It is never written to `appsettings*.json`, the database, or a log. No IAM user or access
  key is involved.
- While the key is unset (or shorter than 32 characters) every admin route answers `404`: the API does not exist.
- A missing or wrong `Authorization: Bearer <key>` gets `401` and a warning log line with method, path and remote IP
  (never the presented value). The comparison is constant-time. Authentication runs before the request body is read,
  so an unauthenticated request never reaches the JSON parser (it gets `401`, or `404` without a key, never `400`).
- The check is shared with the operator onboarding route (`OperatorAuth` in `backend/Biatec.Chat.Api/Onboarding/`).
- Requests go to any registered tenant host (the tenant middleware resolves every request by `Host`), for example
  `https://chat.biatec.io`. The operator acts on any tenant by id; the host used for the call does not scope it.

### Browser and CSRF posture

The routes sit under `/onboarding/operator/`, which already is the operator surface, so they share its posture:

- No cookie or other ambient credential is used, so there is nothing for a cross-site request to borrow; the CSRF
  guard is not needed and does not apply (`OriginPolicy.IsPublic`).
- `Authorization` is not in the CORS allowed headers, and the operator prefix never gets the `/public` wildcard, so a
  page in a browser cannot send the bearer cross-origin.
- The admin routes have their own rate-limit policy `operator` (per remote IP, `Admin:RateLimitPermits`, default 60,
  per `Admin:RateLimitWindowSeconds`, default 60), counted apart from public onboarding. The onboarding body limit
  (`Onboarding:MaxBodyBytes`) applies. A PATCH body must be `application/json` (else `415`).

## Endpoints

### `GET /onboarding/operator/tenants`

All tenants' policy: `{ "tenants": [ <policy>, ... ] }`.

### `GET /onboarding/operator/tenants/{tenantId}`

One tenant's policy, or `404`.

The policy view is policy only. It never contains `clientSecretRef`, `oidcClientId`, tokens or any secret:

```json
{
  "tenantId": "chat.example-bank.com",
  "host": "chat.example-bank.com",
  "softCapEurPerHour": 1500,
  "effectiveSoftCapEurPerHour": 1500,
  "consentThresholdCeilingEur": 1000,
  "trustAnchorVerified": false,
  "limitsUpdateEnabled": false,
  "x402Enabled": false,
  "bedrockUsdPerWindow": null,
  "safeApprovalCaps": { "approvalsPerHour": 5, "approvalEurPerHour": 2000, "executionsPerHour": 10 },
  "defaultModel": null
}
```

`effectiveSoftCapEurPerHour` is what signing applies (`SoftCapPolicy.EffectiveCapEur`). `bedrockUsdPerWindow: null`
means the deployment default `Budget:TenantUsdPerWindow`. `safeApprovalCaps` shows effective values (stored value or
platform default).

### `PATCH /onboarding/operator/tenants/{tenantId}/policy`

Changes policy fields. Every field is optional; a field that is missing or `null` is left unchanged. `reason` is
required (1-200 characters, no control characters) and goes into the audit log.

```json
{
  "softCapEurPerHour": 3000,
  "trustAnchorVerified": true,
  "limitsUpdateEnabled": true,
  "x402Enabled": false,
  "bedrockUsdPerWindow": 10,
  "safeApprovalCaps": { "approvalsPerHour": 3, "approvalEurPerHour": 1000, "executionsPerHour": 10 },
  "defaultModel": "eu.anthropic.claude-sonnet-4-5-20250929-v1:0",
  "reason": "ticket 42: bank pilot, federation chain checked"
}
```

Answer: `200 { "tenant": <policy>, "changes": ["softCapEurPerHour: 1500 -> 3000", ...] }`, `400 { "error": ... }`
for a refused change (nothing is written), `404` for an unknown tenant.

Rules, enforced server-side:

| Field | Rule |
|---|---|
| any field not listed here | `400`. Identity and secret fields (`host`, `allowedOrigins`, `clientKeys`, `oidcClientId`, `clientSecretRef`, `redirectUri`) are set only by onboarding and cannot be changed here. |
| `softCapEurPerHour` | The same save rule as onboarding (`SoftCapPolicy.ValidateOnSave`, with `byOperator: true`): at least `consentThresholdCeilingEur` (the floor), and above the platform default (1500 EUR) only while `trustAnchorVerified` is true. At most 1 000 000. |
| `trustAnchorVerified` | The operator's attestation that the tenant's OIDC client chains to a recognized trust anchor (no automatic federation check exists yet). Clearing it while the soft cap is above the default is refused: lower the cap in the same or an earlier request. |
| `limitsUpdateEnabled`, `x402Enabled` | Booleans. `x402Enabled` still needs the deployment switch `X402:Enabled`. |
| `bedrockUsdPerWindow` | 0 to 10 000; written to `bedrockBudgets.usdPerWindow`, other keys of that JSON are kept. |
| `safeApprovalCaps.*` | Counts 0 to 10 000, EUR 0 to 1 000 000; merged into the stored JSON. |
| `defaultModel` | An `eu.anthropic.claude-(haiku\|sonnet\|opus)-4-*` inference profile id (Claude 5 isn't available on this account); `""` clears it. Stored now; takes effect once T-085 ships (the runtime does not read it yet). |
| `consentThresholdCeilingEur` | Read-only here: it is the deployment's ceiling, not a per-tenant knob. |

The soft-cap save rule runs when the patch changes `softCapEurPerHour` or `trustAnchorVerified`. A patch of other
fields is never blocked by a stored row that predates the rule, so switching a feature off (for example
`x402Enabled: false`) always works.

Numeric fields are compared as numbers: a value equal to the current effective one (stored value, legacy
`safeApprovalCaps.perHour`, or default) is no change and is not written. The audit line shows the effective old value.

Concurrent changes to one tenant are serialized (a PostgreSQL advisory lock), so two operators never overwrite each
other's read-modify-write.

Request handling reads tenant policy (including the gates `x402Enabled`, `limitsUpdateEnabled`, the soft cap and the
safe caps) from the cached tenant row in `DbTenantStore`. A successful change evicts that row on the replica that
handled it, so it applies there at once. Other replicas pick it up when their entry expires: the cache TTL is 10
seconds, so a gate switched off is off everywhere within 10 seconds.

## Knowledge base

`kb.search` serves the seed pages under `Kb:Path` (the `docs/kb/*.md` files shipped in the image) overlaid with pages
operators upload here. Uploaded pages live in the PostgreSQL table `KbPages` (migration `KbPages`), so every replica
serves the same set; no AWS resource is involved. An uploaded page replaces the seed page with the same id; deleting
it brings the seed page back (a seed page cannot be hidden from here).

Uploaded pages are untrusted data, exactly like seed pages: they reach the model only as a `kb.search` tool result
(wrapped in `<untrusted_data source="tool.kb.search">`, excerpt at most 600 characters), never in the system prompt.

Each replica holds an immutable page snapshot and swaps it atomically on reload. Replicas converge in two ways:

- the replica that handles an upload or delete reloads at once;
- every replica polls the table every `Kb:RefreshSeconds` (default 30; `0` turns polling off) and reloads when the
  set of page ids and SHA-256 hashes changed (the poll reads ids and hashes only, not bodies). If the database cannot
  be read, the replica keeps the pages it serves (at start: the seed only) and logs one warning per outage.

Settings (section `Kb`): `Path` (seed directory, default `kb/` next to the binary), `MaxPageBytes` (default 16384),
`MaxPages` (uploaded pages, default 200), `RefreshSeconds` (default 30).

### `GET /onboarding/operator/kb/pages`

```json
{
  "uploaded": [ { "id": "zebra-staking", "contentType": "text/markdown", "sizeBytes": 87, "sha256": "…", "updatedAt": "2026-10-20T10:00:00+00:00" } ],
  "served": [ { "id": "what-is-biatec", "title": "What is Biatec", "source": "seed" }, { "id": "zebra-staking", "title": "Zebra staking", "source": "upload" } ]
}
```

`uploaded` is the shared table (no bodies); `served` is what this replica serves right now.

### `PUT /onboarding/operator/kb/pages/{pageId}`

Creates or replaces a page. The raw request body is the page; the audit reason goes in the header `X-Admin-Reason`
(required, 1-200 characters, no control characters). Answer: `201` created, `200` replaced or unchanged (same
content), with `{ "page": <uploaded page>, "change": "created|replaced|unchanged", "served": <count> }`.

| Check | Refusal |
|---|---|
| `pageId`: 1-64 characters, lower-case letters, digits and inner hyphens (it is the id the model cites) | `400` |
| `X-Admin-Reason` missing or invalid | `400` |
| `Content-Type` `text/markdown` (or `text/x-markdown`) or `text/plain`; a charset other than UTF-8 is refused | `415` |
| Valid UTF-8, no control characters other than tab and line breaks (no binary) | `415` |
| At most `Kb:MaxPageBytes` bytes (also for a chunked body without a length); the `/onboarding` body limit of 32 KiB applies as well | `413` |
| Not empty or whitespace only | `400` |
| A new id while `Kb:MaxPages` pages are stored | `400` |

The title is the first `# heading`, else the id. Writes are serialized with a PostgreSQL advisory lock.

### `DELETE /onboarding/operator/kb/pages/{pageId}`

Removes an uploaded page (`X-Admin-Reason` required). `200 { "deleted": id, "served": <count> }`, or `404`.

### `POST /onboarding/operator/kb/reload`

Re-reads the seed directory (`Kb:Path`) and the uploaded pages on the replica that handles it, for example after a
mounted seed volume changed: `200 { "served": <count> }`. Uploaded pages reach other replicas by polling.

```bash
curl -sS -X PUT https://chat.biatec.io/onboarding/operator/kb/pages/zebra-staking \
  -H "Authorization: Bearer $OPERATOR_KEY" -H 'Content-Type: text/markdown' \
  -H 'X-Admin-Reason: ticket 42: new staking page' --data-binary @zebra-staking.md
```

## Audit

Every call writes a structured log line (category `Biatec.Chat.Api.Admin.TenantPolicyService`):

- read: `Admin: operator from {RemoteIp} read tenant policy {TenantId} (found: ...)`, and the same for the list;
- change: `Admin: operator from {RemoteIp} changed tenant policy {TenantId}: {Changes}. Reason: {Reason}`, with each
  changed field as `name: old -> new`;
- refusal: `Admin: refused tenant policy change {TenantId} from {RemoteIp}: {Error}. Reason: {Reason}`;
- knowledge base (category `Biatec.Chat.Api.Admin.KbAdminService`): `uploaded kb page {PageId} ({Change}, {Bytes}
  bytes, sha256 {Sha256}); serving {Served} pages. Reason: {Reason}`, `deleted kb page {PageId}; ... Reason: {Reason}`,
  `reloaded the knowledge base; serving {Served} pages`, `listed kb pages`, and refusals as `refused kb page change
  {PageId} from {RemoteIp}: {Error}. Reason: {Reason}`. Lines carry the page id, size and hash, never its content;
- rejected authentication (category `Biatec.Chat.Api.Operator`, shared with operator onboarding): `Operator: rejected unauthenticated {Method} {Path} from {RemoteIp}`.

Lines carry policy values only, never the bearer key, `clientSecretRef` or another secret. Caller-supplied text
(tenant id in a path, reason) has control characters removed or refused, so a request cannot forge log lines. The
log is not the hash-chained evidence log, which is per conversation.

## Example

```bash
# The key comes from the secret store, never from a file in the repo.
export OPERATOR_KEY="$(kubectl get secret biatec-chat-operator -o jsonpath='{.data.key}' | base64 -d)"
curl -sS https://chat.biatec.io/onboarding/operator/tenants/chat.example-bank.com \
  -H "Authorization: Bearer $OPERATOR_KEY"
curl -sS -X PATCH https://chat.biatec.io/onboarding/operator/tenants/chat.example-bank.com/policy \
  -H "Authorization: Bearer $OPERATOR_KEY" -H 'Content-Type: application/json' \
  -d '{"x402Enabled": true, "reason": "ticket 42"}'
```

## Not covered

- A knowledge-base editor UI, hiding a seed page, and page version history (an upload overwrites).
- A dedicated `/admin` prefix, operator identities (for example an operator group claim at Biatec OIDC) instead of one
  shared bearer key, and the hash-chained evidence log for admin changes.
- A UI.
