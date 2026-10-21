---
id: T-115
title: "Serve the backend under a path prefix of a tenant host (beta.dex.biatec.io/chat)"
status: done
priority: P1
epic: "Deploy & docs"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Hosting/**", "backend/Biatec.Chat.Api/Program.cs", "backend/Biatec.Chat.Api/Tenancy/**", "backend/Biatec.Chat.Api/Auth/AuthEndpoints.cs", "backend/Biatec.Chat.Api/Auth/Elevation/ElevationEndpoints.cs", "backend/Biatec.Chat.Api/Chat/SignIn/AnonymousTierExecutors.cs", "backend/Biatec.Chat.Api/Limits/LimitsExecutors.cs", "backend/Biatec.Chat.Api/Admin/AdminEndpoints.cs", "backend/Biatec.Chat.Api/Evidence/Export/EvidenceExportEndpoints.cs", "backend/Biatec.Chat.Api/Retention/RetentionEndpoints.cs", "backend/Biatec.Chat.Tests/Hosting/**", "packages/chat-widget/test/path-mount.test.ts", "k8s/stage/**", ".github/workflows/deploy.yml", "PRODUCT-DESCRIPTION.md", "docs/integration-biatec-dex.md", "docs/cicd.md", "k8s/README.md"]
parallel: yes
conflicts_with: []
issue: "#244"
branch: "task/T-115"
owner: "session-20261021-claude"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#embedding-modes"
created: 2026-10-21
updated: 2026-10-21
---

# T-115 · Serve the backend under a path prefix of a tenant host (beta.dex.biatec.io/chat)

## Context

The Biatec DEX beta site (`https://beta.dex.biatec.io`, namespace `biatec`) should use the chat backend that runs in the
namespace `biatec-chat-stage` without a cross-origin call to `stage.chat.biatec.io`. Today the DEX widget talks to
`https://stage.chat.biatec.io`, which is a credentialed cross-origin fetch: the `__Host-biatec_session` cookie is set by
`stage.chat.biatec.io` and has to survive third-party-cookie rules, CORS and the popup flow. Mounting the backend at
`https://beta.dex.biatec.io/chat` makes every request same-origin and the cookie first-party to the page.

**The spec forbids this today.** `PRODUCT-DESCRIPTION.md` ("Embedding Modes", third-party hosts): "Path-based reverse
proxying is not supported, because it would require trusting forwarded-host and forwarded-prefix headers and would break
exact redirect URI matching." The product owner asked for it on 2026-10-21, so this task changes the spec on purpose and
keeps both stated reasons intact (no forwarded header is trusted; the redirect URI is an exact, stored value that
includes the prefix).

### What breaks when the ingress simply forwards `/chat`

Read from the code (2026-10-21):

1. **Routing.** The backend's own routes start with `/chat` (`POST /chat/messages`, `/chat/previews/{id}/confirm`, ...). Behind a
   `/chat` mount the public path is `/chat/chat/messages`. The prefix has to be removed before routing, and it cannot be
   guessed from the path: a direct request to `/chat/messages` is a real backend route. So the prefix is configuration,
   per host, never a header.
2. **Tenant by Host.** `TenantMiddleware` resolves the tenant from the `Host` header alone (by design). The ingress
   forwards `Host: beta.dex.biatec.io`, so that host needs its own tenant row (own `RedirectUri`, `AllowedOrigins`,
   `ClientKeys`).
3. **Generated URLs drop the prefix.** The popup start response (`POST /auth/popup` -> `LoginUrl`), the elevation URL, and
   the sign-in/limits cards (`AnonymousTierExecutors`, `LimitsExecutors`) build `<scheme>://<authority of
   Tenant.RedirectUri>/auth/...`. That is `https://beta.dex.biatec.io/auth/login`, which is the DEX app, not the chat.
   The evidence JWKS URL fallback (`{Scheme}://{Host}` + path) has the same gap.
4. **Redirect URI.** The OIDC `redirect_uri` is the tenant's stored value, so it can simply be
   `https://beta.dex.biatec.io/chat/auth/callback`. The Biatec OIDC client must allow it (operator step, not in this repo).
5. **Cookie.** `__Host-` requires `Path=/`, `Secure`, no `Domain`. The cookie stays host-only on `beta.dex.biatec.io` with
   `Path=/` (the browser then also sends it to the DEX app's own paths; it is `HttpOnly`, so DEX script cannot read it).
   `SameSite=Lax` is unchanged; same-origin removes the cross-origin cookie problem.
6. **Origin/CSRF.** Same-origin POSTs still send `Origin: https://beta.dex.biatec.io`; the tenant's `AllowedOrigins` must list
   it. `CsrfMiddleware`/`OriginPolicy` match on the path after the mount is removed, so they need no change as long as
   the mount is removed first.
7. **Widget.** All widget URLs are `backendUrl + path` (string concatenation, `options.ts` trims trailing slashes), so
   `backendUrl: "https://beta.dex.biatec.io/chat"` already works; a test pins it.
8. **Ingress (BiatecDEX repo, separate issue).** The DEX ingress is one catch-all regex path (`/(.*)`, rewrite `/$1`). A
   second ingress for `/chat` with an ExternalName service to `biatec-chat-backend.biatec-chat-stage.svc.cluster.local`
   keeps the full path (including `/chat`), turns buffering off for SSE and extends the read timeout.

### "Both full URL path and the prefix"

Interpreted as: the same backend keeps answering on its own host with the full, unprefixed path
(`https://stage.chat.biatec.io/chat/messages`, nothing changes), **and** on the DEX host under the mount with the full path
forwarded unchanged by the ingress including the prefix (`https://beta.dex.biatec.io/chat/chat/messages`). An ingress that
strips the prefix (rewrite) is not supported on a mounted host, because the stripped and the unstripped request cannot be
told apart without trusting a header; a request on a mounted host without the prefix is a 404.

## Goal

The backend can be mounted under a configured path prefix on a configured host: every route, every generated URL and the
OIDC flow work under `https://<host><prefix>/...`, hosts without a mount behave exactly as before, and the stage deploy
provisions the tenant for `beta.dex.biatec.io`.

## Scope

- In:
  - `Hosting:Mounts` configuration (`[{ Host, Prefix, ClientKey }]`), validated at startup; middleware that, for a mounted
    host, moves the prefix from `Request.Path` to `Request.PathBase` before the tenant middleware (404 when the prefix is
    missing); no forwarded header is read.
  - A single `Tenant` helper for the backend base URL (`RedirectUri` without `/auth/callback`, so it carries the prefix),
    used by the popup, elevation, sign-in and limits URLs; the evidence JWKS fallback adds `PathBase`.
  - Stage overlay configuration for `beta.dex.biatec.io` -> `/chat`, and a deploy step that provisions the matching tenant
    row (copy of `biatec-chat` with its own id, host, redirect URI, origin and client key), overlay-driven like the demo
    step.
  - Tests (backend + widget), the spec change, `docs/integration-biatec-dex.md`, `docs/cicd.md`, `k8s/README.md`.
- Out:
  - The BiatecDEX repo change (ExternalName service, `/chat` ingress, `backendUrl`), tracked as its own issue there.
  - Allowing the redirect URI on the Biatec OIDC client (operator step, documented).
  - Production: `k8s/main` gets no mount until the DEX production site wants one.
  - A prefix-stripping ingress, forwarded-prefix headers.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [x] With `Hosting:Mounts = [{Host: beta.dex.biatec.io, Prefix: /chat}]`, `GET /chat/session/probe` and `POST /chat/chat/messages` on that
      host reach the same endpoints as `/session/probe` and `/chat/messages` on an unmounted host (test); `/health` and `/chat/health` are a 404 there.
- [x] On a mounted host a path without the prefix is a 404, and `X-Forwarded-Prefix`/`X-Forwarded-Host` change nothing (test).
- [x] On an unmounted host, including one whose path starts with `/chat/`, nothing changes (test).
- [x] The popup `LoginUrl`, the elevation URL and the sign-in/limits card URLs contain the prefix for the mounted tenant and are
      unchanged for `chat.biatec.io` (tests).
- [x] An invalid mount (host with a scheme/port, prefix `/`, `//x`, trailing `/`, query) fails startup naming the option (test).
- [x] The widget with `backendUrl: "https://beta.dex.biatec.io/chat"` calls `.../chat/session`, `.../chat/chat/messages`,
      `.../chat/auth/popup` (test).
- [x] The stage deploy upserts tenant `mount-beta-dex-biatec-io` from the overlay (host, redirect
      `https://beta.dex.biatec.io/chat/auth/callback`, origin `https://beta.dex.biatec.io`, client key `biatec-dex`), idempotently.
- [x] `PRODUCT-DESCRIPTION.md`, `docs/integration-biatec-dex.md`, `docs/cicd.md`, `k8s/README.md` describe the mount, the
      ingress contract (full path forwarded, no rewrite) and the OIDC redirect URI to allow.
- [x] CI is green.
- [ ] The stage deploy still succeeds and provisions the tenant (can only be verified after the merge; the real run is recorded in the review).

## Implementation notes

- Prefix grammar: `/seg(/seg)*`, segments `[A-Za-z0-9._~-]+`, no trailing slash, not `/`. Host: lower-case DNS name, no
  scheme, port or path (`TenantHost.Normalize` is the single parser). The prefix is matched exactly (ordinal): `/CHAT/...`
  on a mounted `/chat` host is a 404 (what follows the prefix is routed case-insensitively, as everywhere).
- The middleware runs before `UseTenancy` and after `UseClientAddress`; `PathBase` is appended to any existing one.
- Health probes address the pod by IP, so they never match a mounted host and keep working on `/health`; behind a mount `/health` is a 404.
- Cookie attributes stay as they are (`__Host-`, `Path=/`).
- The deploy step reads `Hosting__Mounts__N__*` from the rendered ConfigMap `biatec-chat-backend` (yq, as the demo step
  does) so the overlay is the single source.

## Verification

`dotnet test backend/Biatec.Chat.slnx --filter FullyQualifiedName~Hosting`, `pnpm --filter @biatec/chat-widget test`,
`kubectl kustomize k8s/stage`, CI green, then after the BiatecDEX change:
`curl -i https://beta.dex.biatec.io/chat/session/probe` (200 and the cookie) and `curl -i https://beta.dex.biatec.io/chat/chat/messages` (not the DEX app).

## Hand-off

Filled in by the agent when it moves the task to `review`.

- **Summary:** `Hosting:Mounts` (host, prefix) mounts the backend under a path prefix of a host. `HostMountMiddleware` (before
  routing and tenancy) moves the prefix into `PathBase` (matched exactly); a mounted host without the prefix, with an empty
  path segment, or outside the widget's routes (`/session`, `/chat`, `/auth`, `/public`, `/.well-known`; so `/onboarding`, `/health`, `/contracts` and anything added later) is a 404; unmounted hosts and all forwarded headers are ignored. Routing
  is explicit (`app.UseRouting()` after the mount; the implicit routing matched endpoints on the unstripped path, found by
  the tests). The URLs handed to the browser are the redirect URI's origin plus the host's path base (`BackendUrls`), for the
  popup, elevation and the sign-in/limits cards; the evidence JWKS fallback adds `PathBase` (`EvidenceJwks.PublicUrl`, one
  helper). `TenantMiddleware` checks that a mounted host's tenant has exactly the matching redirect URI
  (`MountConsistency`): every `/auth` route but logout is refused with a 503 in that one place, the cards fall back to the
  sign-in page, and the problem is logged once. Stage overlay lists `beta.dex.biatec.io` -> `/chat` (+ the client key for the
  deploy); the deploy validates the mounts before applying and provisions tenant `mount-beta-dex-biatec-io` (copy of
  `biatec-chat`, nothing privileged inherited, origin/key only ever added). Spec, DEX integration doc, cicd and k8s docs updated.
- **Files changed:** `backend/Biatec.Chat.Api/Hosting/**` (`HostMounts`, `BackendUrls`, `MountConsistency`), `Program.cs`,
  `Tenancy/TenantMiddleware.cs` (consistency check and the central 503), the two auth endpoints, the sign-in/limits cards,
  `ConversationOrchestrator`/`ToolExecution` (PathBase), `Evidence/Jwks/EvidenceJwks.cs` and the three bundle endpoints,
  `ConfirmService`, `backend/Biatec.Chat.Tests/Hosting/**` and `Kb/KbChatTests.cs`, `packages/chat-widget/test/path-mount.test.ts`,
  `k8s/stage/backend-config.patch.yaml`, `.github/workflows/deploy.yml`, `PRODUCT-DESCRIPTION.md`, `docs/{integration-biatec-dex,
  integration-widget,cicd}.md`, `k8s/README.md`.
- **How it was verified:** backend suite 1474 tests (about 150 new: middleware, validator, URL, consistency, JWKS and
  three full sign-in/JWKS tests through the mount against Postgres incl. cookie attributes and the prefixed redirect URI, and an
  end-to-end chat test pinning the prefixed sign-in card); widget 5 new tests; the deploy steps (validation, owner check,
  upsert, caps, add-only) run against a real Postgres with the real rendered overlay and mikefarah yq. Ten rounds of code
  review (see the log). Not verified: a real deploy, and the nginx behaviour of the DEX ingress (BiatecDEX issue).
- **Follow-up tasks created:** BiatecDEX issue (ExternalName service, `/chat` ingress, `backendUrl`).
- **Unblocks:** none.
- **Open questions:** the Biatec OIDC client must allow `https://beta.dex.biatec.io/chat/auth/callback` (operator step).

## Review

- 2026-10-21 12:19 · approve · PR #245 merged into main @ 3173582 (recorded by kanban-sync)

## Activity log

Append-only. Format: `- YYYY-MM-DD HH:MM · <actor> · <from> → <to> · <note>`

- 2026-10-21 00:00 · scholtz · (new) → backlog · created at the product owner's request (chat on beta.dex.biatec.io/chat); the spec's "path-based reverse proxying is not supported" is changed deliberately
- 2026-10-21 00:05 · scholtz · backlog → in-progress · claimed with session-20261021-claude on branch task/T-115; issue #244
- 2026-10-21 00:40 · session-20261021-claude · in-progress → review · PR for T-115
- 2026-10-21 10:45 · session-20261021-claude · review → review · code review round 1: tenant upsert now keeps origin/client key/sign-in page in step with the overlay, mount ClientKey validated, telemetry probe filter covers the mounted /chat/health, one shared JWKS URL helper, warning when a mounted tenant's redirect URI lacks the prefix (fixed a log-template bug found by its test)
- 2026-10-21 10:55 · session-20261021-claude · review → review · correction: the 00:00, 00:05 and 00:40 times above were typed by hand and are wrong; the real times are 10:03 (created), 10:04 (claimed) and 10:19 (review)
- 2026-10-21 10:55 · session-20261021-claude · review → review · code review round 2: the base URL now follows the host's mount instead of the redirect URI's shape (unmounted tenants keep their URLs), `/onboarding/**` is a 404 on a mounted host, the probe filter uses the configured prefixes only, the tenant upsert only adds origins and client keys, mounts are validated before apply, the redirect-URI mismatch is an error; documented: shared origin does not widen the trust model, a configured Evidence:PublicBaseUrl wins
- 2026-10-21 11:25 · session-20261021-claude · review → review · code review round 3: validator regexes end in \z (a trailing newline passed with $), a mounted host rejects empty path segments, the telemetry probe filter uses the validated options and the request's own host, ClientKey is a deploy-only input (not a backend option, cannot crash-loop the pods), a mounted tenant whose redirect URI disagrees with its mount gets a 503 on sign-in/elevation start, the deploy asserts the ConfigMap was found and rejects colliding tenant ids, ConfirmService.BackendOrigin uses BackendUrls; declined: threading PathBase through the turn (cheaper and safer than injecting a mount catalog into every executor)
- 2026-10-21 11:55 · session-20261021-claude · review → review · code review round 4: the redirect-URI check is exact (Ordinal, as Biatec OIDC) and also gates /auth/login, /auth/elevate and the sign-in/limits cards, a mounted tenant starts with limits updates, x402 and the trust-anchor flag off, reserved prefixes /health and /onboarding are rejected (backend and deploy, with a test that keeps the bash and C# patterns in step), the deploy finds the ConfigMap by name, the telemetry filter resolves the mounts once, request overloads shorten the call sites; declined: the claim that the biatec-chat row may not exist yet (the deploy seeds it in an earlier step) and SignInPageUrl being rewritten (documented overlay-owned)
- 2026-10-21 12:20 · session-20261021-claude · review → review · code review round 5: the mount prefix is matched exactly (ordinal), the redirect-URI check compares the stored path instead of a rebuilt string (a default port or host case is fine), the deploy reads mount keys case-insensitively and requires Host, Prefix and ClientKey of every entry and names a tenant that already owns the host, one SignInBase helper for the cards, BackendUrls.Origin, the probe paths are precomputed once; declined: a single shared validator for C# and bash (a test keeps the two in step)
- 2026-10-21 12:50 · session-20261021-claude · review → review · code review round 6: the redirect-URI check also needs the tenant's own host and no dot segments; the tracing probe filter is a DI singleton wired through the AspNetCore options (tested through the real wiring), matches the prefix exactly and what follows like routing; an end-to-end chat test pins the prefixed sign-in card (a forgotten PathBase would fail open); the consistency check runs once per tenant row; the upsert keeps onboarding's 10 origins / 20 keys; declined: hiding /chat/health on the mounted host (status-only body, same as the chat host, and the post-deploy check uses it) and one shared validator for C# and bash
- 2026-10-21 13:15 · session-20261021-claude · review → review · code review round 7: the mount/redirect-URI refusal lives in one place (the tenant middleware: popup, login, elevate; not the callback or logout) instead of six endpoints; `/health` is a 404 behind a mount (probes use the pod's address), which also reverts all telemetry changes (TelemetryRegistration is identical to main); declined again: threading PathBase (pinned by an end-to-end test), a shared C#/bash validator, multi-line ConfigMap values (the values are re-validated by the same patterns)
- 2026-10-21 13:40 · session-20261021-claude · review → review · code review round 8: the consistency verdict is cached per tenant instance (no key strings, no growth, logged once) and the reverse case (a prefixed redirect URI on a host that is not mounted) is logged, the deploy rejects case-variant duplicate mount keys, multi-line mount values and an existing tenant that has the derived id for another host, the docs say who receives the cookie; declined again: PathBase default (pinned end to end), case-sensitive prefix (documented), a cookie narrower than Path=/ (__Host- forbids it)
- 2026-10-21 14:05 · session-20261021-claude · review → review · code review round 9: the consistency problem is logged once per distinct (tenant, redirect URI, mount) even though the store reloads the row every 10 s, mount indexes 1 and 01 are rejected as one slot, inline namespaces replaced by usings, the task notes and hand-off rewritten to match the code
- 2026-10-21 14:40 · session-20261021-claude · review → review · code review round 10: a mounted host answers an allow-list of the widget's routes (`/session`, `/chat`, `/auth`, `/public`, `/.well-known`) instead of denying two names, so `/contracts`, `/openapi`, `/health`, `/onboarding` and later additions are 404 by default; the refusal covers every `/auth` route except logout; the consistency verdict is cached per tenant instance and mount; a redirect URI with userinfo is rejected; the provisioning step insists on the apply step's mounts file; declined again: a shared C#/bash validator
- 2026-10-21 15:00 · session-20261021-claude · review → review · code review round 11: the consistency check runs per request again (a few string operations; the extra cache was heavier than the check) with only the log line remembered, the redirect URI must be https, the unmounted-host diagnostic reads the stored path the way the main check does, a dead branch removed, docs say the refusal covers the callback and the popup poll; declined: a cookie narrower than Path=/ (the spec mandates __Host-), the shared C#/bash validator
- 2026-10-21 15:25 · session-20261021-claude · review → review · code review round 12: stage sets Evidence__PublicBaseUrl so bundles tell verifiers an https key URL (it was built from the request, plain http behind the ingress, for every host), the deploy detects multi-line ConfigMap values by line count instead of by what a continuation looks like, says so when the biatec-chat template tenant is missing, and the docs state that the allow-list is by first segment; declined: refusing less than every /auth route but logout (the callback and the poll are meant to stop with the flow), PathBase plumbing (pinned end to end), case-sensitive later segments (routing is case-insensitive everywhere), the _reported map (a few bytes per distinct problem)
- 2026-10-21 15:50 · session-20261021-claude · review → review · code review round 13: CarriesAPrefix allocates nothing, BackendUrls has no default path base (callers say origin or mounted base), the widget's same-origin test asserts on the requests the widget made, docs note that a mount change arrives with a new image tag; declined: restarting pods on a ConfigMap change (any merged overlay change carries a new tag), the shared C#/bash validator, spec sign-off (the product owner asked for the change; the task and PR say so)
- 2026-10-21 16:10 · session-20261021-claude · review → review · code review round 14: no definite crash or security bug found; the mount reader no longer pipes through head (SIGPIPE under pipefail); recorded and declined: the per-request check on a mounted host (a cache was removed in round 11 as heavier than the check), the log de-dup set (a few bytes per distinct problem), the caps being checked after apply (the first deploy cannot provision before its database exists; the failure is loud), a route allow-list from endpoint metadata (an explicit list is easier to review and is pinned against the widget's paths), PathBase plumbing (pinned end to end), the spec change (the product owner asked for it)
- 2026-10-21 12:19 · user · review → done · PR #245 merged into main @ 3173582 (synced by kanban-sync)
