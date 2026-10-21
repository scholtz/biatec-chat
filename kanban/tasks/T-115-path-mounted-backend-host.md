---
id: T-115
title: "Serve the backend under a path prefix of a tenant host (beta.dex.biatec.io/chat)"
status: in-progress
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

- [ ] With `Hosting:Mounts = [{Host: beta.dex.biatec.io, Prefix: /chat}]`, `GET /chat/health` and `POST /chat/chat/messages` on that
      host reach the same endpoints as `/health` and `/chat/messages` on an unmounted host (test).
- [ ] On a mounted host a path without the prefix is a 404, and `X-Forwarded-Prefix`/`X-Forwarded-Host` change nothing (test).
- [ ] On an unmounted host, including one whose path starts with `/chat/`, nothing changes (test).
- [ ] The popup `LoginUrl`, the elevation URL and the sign-in/limits card URLs contain the prefix for the mounted tenant and are
      unchanged for `chat.biatec.io` (tests).
- [ ] An invalid mount (host with a scheme/port, prefix `/`, `//x`, trailing `/`, query) fails startup naming the option (test).
- [ ] The widget with `backendUrl: "https://beta.dex.biatec.io/chat"` calls `.../chat/session`, `.../chat/chat/messages`,
      `.../chat/auth/popup` (test).
- [ ] The stage deploy upserts tenant `mount-beta-dex-biatec-io` from the overlay (host, redirect
      `https://beta.dex.biatec.io/chat/auth/callback`, origin `https://beta.dex.biatec.io`, client key `biatec-dex`), idempotently.
- [ ] `PRODUCT-DESCRIPTION.md`, `docs/integration-biatec-dex.md`, `docs/cicd.md`, `k8s/README.md` describe the mount, the
      ingress contract (full path forwarded, no rewrite) and the OIDC redirect URI to allow.
- [ ] CI is green and the stage deploy still succeeds.

## Implementation notes

- Prefix grammar: `/seg(/seg)*`, segments `[A-Za-z0-9._~-]+`, no trailing slash, not `/`. Host: lower-case DNS name, no
  scheme, port or path (`TenantHost.Normalize` is the single parser). Compare the prefix case-insensitively like
  `PathString.StartsWithSegments`.
- The middleware runs before `UseTenancy` and after `UseClientAddress`; `PathBase` is appended to any existing one.
- Health probes address the pod by IP, so they never match a mounted host and keep working on `/health`.
- Cookie attributes stay as they are (`__Host-`, `Path=/`).
- The deploy step reads `Hosting__Mounts__N__*` from the rendered ConfigMap `biatec-chat-backend` (yq, as the demo step
  does) so the overlay is the single source.

## Verification

`dotnet test backend/Biatec.Chat.slnx --filter FullyQualifiedName~Hosting`, `pnpm --filter @biatec/chat-widget test`,
`kubectl kustomize k8s/stage`, CI green, then after the BiatecDEX change:
`curl -i https://beta.dex.biatec.io/chat/health` (200) and `curl -i https://beta.dex.biatec.io/chat/chat/messages` (not the DEX app).

## Hand-off

Filled in by the agent when it moves the task to `review`.

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

Append-only. Format: `- YYYY-MM-DD HH:MM · <actor> · <from> → <to> · <note>`

- 2026-10-21 00:00 · scholtz · (new) → backlog · created at the product owner's request (chat on beta.dex.biatec.io/chat); the spec's "path-based reverse proxying is not supported" is changed deliberately
- 2026-10-21 00:05 · scholtz · backlog → in-progress · claimed with session-20261021-claude on branch task/T-115; issue #244
