# Administrator dashboard

A web dashboard for the people listed in the GitHub secret `ADMINS`. They sign in through Biatec OIDC and get overview
statistics, a searchable user list, user detail and a read-only **view as user** (impersonation). It is served by the
backend itself at `https://<backend host>/admin/` (a Vue 3 app, `apps/admin`, built into the backend image).

This is separate from the bearer-key operator API ([admin-api.md](admin-api.md)), which stays the way to change tenant
policy and knowledge-base pages from scripts. The dashboard is read-only apart from starting and stopping an impersonation.

## Who is an admin

`Admin:Admins` (environment variable `Admin__Admins`) is a list of emails separated by `;`:

```
alice@example.com;bob@example.com
```

- Comparison is case-insensitive and ignores surrounding whitespace. Empty entries and entries without an `@` are ignored
  (and counted in a startup warning).
- No admin configured means the dashboard does not exist: every `/admin/**` route answers 404.
- The list is read at start. Changing the secret needs a deploy (a restart). An admin removed from the list loses access
  at the next restart; their sessions are re-checked against the list on **every** request, so a restart is enough, no
  session sweep is needed.
- The email comes from the validated ID token. With `Admin:RequireVerifiedEmail` (default `true`) the token must also
  carry `email_verified: true`; set it to `false` only if Biatec OIDC does not issue that claim, and only after reading
  the note below.

> **Trust.** Admin access is exactly as strong as the proof of an email address at Biatec OIDC. If anyone can register
> an account at Biatec OIDC with an address they do not control, they could become an admin by claiming an admin email.
> Keep `RequireVerifiedEmail` on unless Biatec OIDC verifies emails some other way.

### Limits

Signing in again does not end the earlier session (the browser does not send the `SameSite=Strict` cookie on the cross-site return from Biatec OIDC); a session ends by **Sign out**, its idle timeout or its absolute lifetime.
The totals (users, conversations, messages, previews, tenants) are all time; the daily series, tokens and per-model table cover the selected range.

Lists show at most 200 conversations per user (the newest) and a transcript the newest 500 messages; the user's metadata always carries the true counts.

### Scope

`Admin:Admins` is **platform-wide**, not per tenant: an admin sees the statistics, users and (through impersonation) conversations
of every tenant on the deployment. The host an admin signs in on only decides which tenant's OIDC client and redirect URI
perform the sign-in, and the session stays bound to it (it is refused on any other host).

### Setting the GitHub secret

1. Repository (or environment) secret `ADMINS`: `a@x.com;b@y.com`.
2. `deploy.yml` writes it into the cluster secret `biatec-chat-secrets` (key `admins`); `k8s/base/backend.yaml` maps it to
   `Admin__Admins` (optional: when the secret has no value the dashboard is off).
3. Redeploy. Local development: `dotnet user-secrets set Admin:Admins "me@example.com"` in `backend/Biatec.Chat.Api`.

## Sign-in

```
browser ──GET /admin/auth/login──────────────▶ backend  (tenant = Host header)
        ◀──302 Biatec OIDC authorize (PKCE S256, scope "openid profile email", state, nonce)
browser ──────────────────────────────────────▶ Biatec OIDC (user signs in)
        ◀──302 /auth/callback?code&state
browser ──GET /auth/callback──────────────────▶ backend: purpose "admin"
backend ──code + PKCE verifier───────────────▶ Biatec OIDC token endpoint
backend: validates the ID token (signature, iss, aud, nonce, exp), checks the email against Admin:Admins,
         DISCARDS every token, stores an admin session in Redis
        ◀──302 /admin/  + Set-Cookie: __Host-biatec_admin=<opaque>
```

- It reuses the tenant's registered OIDC client and redirect URI `/auth/callback`; nothing changes at Biatec OIDC. The
  tenant is the one the host name resolves to (for example `chat.biatec.io`).
- **The browser never receives an access, refresh or ID token, and the backend keeps none for admins.** The ID token is
  read once to learn the email and subject, then dropped. The scope has no `sign`, no `offline_access`, so no refresh token is requested; Biatec OIDC has no revocation call in this client, and the access token it issues lapses on its own.
- The login state is bound to a short-lived `__Host-biatec_admin_login` cookie (login CSRF defence), is single use and
  expires after 10 minutes. An unknown, replayed or foreign state is refused.
- A failed sign-in redirects to `/admin/?error=<code>` with one of `not_admin`, `email_not_verified` (checked first, so someone who cannot prove an email learns nothing about the list), `login_failed`,
  `access_denied`. The page shows a plain message; nothing else is revealed.

### Admin session

| Property | Value |
|---|---|
| Cookie | `__Host-biatec_admin`, `HttpOnly; Secure; SameSite=Strict; Path=/`, value = 32 random bytes, opaque |
| Server side | Redis, key = SHA-256 of the cookie, value encrypted with the vault key provider |
| Lifetime | `Admin:SessionHours` absolute (default 8), `Admin:SessionIdleMinutes` idle (default 30) |
| Re-checked each request | session exists and is unexpired; the email is still in `Admin:Admins`; the host's tenant is the session's tenant |
| State-changing calls | need header `X-Biatec-Admin: 1` and an `Origin` equal to the backend's own origin |
| Cross-origin | no CORS headers on `/admin/**` at all (the tenant CORS allow-list does not apply), `Sec-Fetch-Site` must be `same-origin` |

All responses under `/admin` carry `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, `Referrer-Policy:
no-referrer` and a strict CSP (`default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'`).
Rate limit per remote IP: `Admin:RateLimitPermits` per `Admin:RateLimitWindowSeconds` for the API, in a bucket of its own (not shared with the operator API), and at most 20 per window for `/admin/auth/login`.

## What an admin sees

- **Overview** (`/admin/#/`): users, active users (7 days), conversations, messages, live sessions by tier, action previews
  by outcome, model token usage (input, output, cache read and write) in total and per model, a daily series for the last
  7, 30 or 90 days (statistics are cached for 30 seconds), and one row per tenant.
- **Users** (`/admin/#/users`): every signed-in user across tenants: subject, tenant, first and last seen, sign-in
  count, conversations. Search (subject), filter by tenant, sort, page. Metadata only: **message content is
  never in the list or the statistics.**
- **User** (`/admin/#/user?tenant=…&sub=…`): the same metadata plus the user's conversations (ids, times, counts). To read
  what is in a conversation the admin has to impersonate.
- **Impersonation** (`/admin/#/view`): see below.

**Users are identified by their OIDC subject, not by email.** The product stores no email or other contact data
(PRODUCT-DESCRIPTION.md: personal data is minimized to the OIDC `sub`), so the dashboard has none either. Look a user up
in Biatec OIDC to map a subject to a person. An impersonation that runs out (or whose admin session idles out) has no stop entry: its start entry carries `expiresAt`. A start that raced a sign-out and never took effect is closed with a stop entry whose `how` is `aborted`. A user row is also removed by the retention job once the user has no conversation left and has not been seen for the record window (`Retention:Records`, 7 years). Users who signed in before the dashboard existed are listed too (a migration
creates their row from their conversations; their sign-in count starts at 0). A user that erased their data and keeps chatting reappears in the list only at their next sign-in. A user that was erased (`DELETE /session/user-data`) disappears from every view: erasure removes the dashboard's
user row and clears the subject from their conversations.

## Impersonation: "view as user"

Impersonation in Biatec Chat is **read-only**. An admin sees what the user sees (conversations, transcripts, action
previews and their status); they never act as the user. This follows from the security invariants: only the backend holds
tokens, signing goes from the backend straight to Biatec OIDC with the user's own consent on their own device, and no
admin session has a user's tokens or the ability to approve anything.

- Starting needs the user (`tenantId`, `sub`) and a **reason** (8 to 500 characters). The reason goes into the signed audit chain, which is kept whole until the record window (7 years) has passed and cannot be edited or partly erased, so it must be a ticket or case reference, **never the user's personal data**. It lasts `Admin:ImpersonationMinutes`
  (default 30; it never outlives the admin session) and is one at a time per admin: starting another ends the first.
- While active, a banner shows who is being viewed, the reason and the time left, with a **Stop** button.
- The view endpoints take **no user in the request**: the impersonated user comes from the admin session, so a request
  cannot be redirected to a different user, and a conversation id that does not belong to that user is a 404.
- **No write path exists** in the view: the dashboard has no send, confirm, cancel, approve or sign endpoint at all.
- **Audit:** start, stop (also replacement by another impersonation, and logout), and every conversation opened are appended to the signed
  **admin evidence chain** ([admin-api.md](admin-api.md), `GET /onboarding/operator/evidence`), with the admin's email,
  the user's tenant and the random id of their dashboard row (neither the subject nor anything derived from it: the chain is permanent, and erasing the user deletes the row, so the id then names no one), the reason and, for a conversation, its id. Admin
  sign-in and sign-out are recorded too. The chain names the acting admin by email (like operators by name): admins are staff, and an audit trail has to say who did it. The chain is verifiable offline like conversation evidence.
- Message text is stored after redaction (PRODUCT-DESCRIPTION.md, Session Recording And Evidence); the view shows exactly
  that. Raw tool payloads and unsigned transaction groups are not exposed: tool calls are shown by name only.

## HTTP API

Everything below is JSON, `camelCase`, `Cache-Control: no-store`. A missing or invalid session is `401 {"error": …}`; a
non-admin is also `401` (never reveals the allow-list). Non-GET calls need the headers above.

| Method and path | Purpose |
|---|---|
| `GET /admin/auth/login` | Start sign-in (browser navigation). |
| `GET /admin/api/me` | `{ email, tenantId, expiresAt, impersonation }` (`email` is the admin's). `impersonation` is `null` or `{ tenantId, sub, reason, startedAt, expiresAt }`. |
| `POST /admin/api/logout` | End the admin session and clear the cookie. |
| `GET /admin/api/tenants` | `{ tenants: [ { id, host } ] }` for the filters. |
| `GET /admin/api/stats?days=30&tenant=` | Statistics (`days` 7 to 365). |
| `GET /admin/api/users?q=&tenant=&sort=&dir=&page=&pageSize=` | User list. `sort`: `lastActivity` (default), `firstSeen`, `sub`, `conversations`. `q` matches the subject. `dir`: `desc` (default) or `asc`. `pageSize` 1 to 100 (default 25). |
| `GET /admin/api/user?tenant=&sub=` | User detail with conversations (metadata). |
| `POST /admin/api/impersonation` | Body `{ tenantId, sub, reason }`. Starts an impersonation; returns the `impersonation` object. |
| `DELETE /admin/api/impersonation` | Stop. |
| `GET /admin/api/view/conversations` | `{ user: { tenantId, sub }, conversations: [ … ] }` for the impersonated user. |
| `GET /admin/api/view/conversations/{id}/messages` | Transcript and action previews of one of their conversations. |

### Response shapes

```jsonc
// GET /admin/api/stats
{
  "generatedAt": "2026-10-23T09:00:00Z", "days": 30, "tenant": null,
  // totals count all time; tokens, daily and byModel cover the selected `days` only
  "totals": { "users": 120, "activeUsers7d": 31, "conversations": 410, "messages": 5120,
              "liveSessions": 14, "previews": 88, "tenants": 2 },
  "sessionsByTier": [ { "tier": "Authenticated", "count": 9 }, { "tier": "Anonymous", "count": 5 } ],
  "previewsByStatus": [ { "status": "Completed", "count": 70 }, { "status": "Cancelled", "count": 12 } ],
  "tokens": { "input": 1200000, "output": 340000, "cacheRead": 5000000, "cacheWrite": 90000,
              "byModel": [ { "modelId": "eu.anthropic.claude-sonnet-4-5-20250929-v1:0", "messages": 1900,
                             "input": 1000000, "output": 300000, "cacheRead": 4000000, "cacheWrite": 80000 } ] },
  "daily": [ { "date": "2026-10-22", "newUsers": 3, "conversations": 20, "messages": 240,
               "inputTokens": 60000, "outputTokens": 17000 } ],           // one row per day, zero filled
  "tenants": [ { "id": "biatec-chat", "host": "chat.biatec.io", "users": 100, "conversations": 380 } ]
}

// GET /admin/api/users
{ "items": [ { "tenantId": "biatec-chat", "sub": "8f2…",
               "firstSeenAt": "…", "lastSeenAt": "…", "signInCount": 4,
               "conversations": 6, "lastActivityAt": "…" } ],
  "total": 120, "page": 1, "pageSize": 25 }

// GET /admin/api/user?tenant=&sub=
{ "user": { …same fields as an item… , "messages": 310, "previews": 7, "activeSessions": 1 },
  "conversations": [ { "id": "…", "createdAt": "…", "lastMessageAt": "…", "endedAt": null, "messages": 42, "previews": 2 } ] }

// GET /admin/api/view/conversations/{id}/messages
{ "conversation": { "id": "…", "createdAt": "…", "lastMessageAt": "…", "endedAt": null },
  "truncated": false,                                    // true when older messages are left out: the newest 500 are returned
  "messages": [ { "seq": 1, "role": "User", "content": "…", "createdAt": "…", "modelId": null, "toolNames": [], "toolResultCount": 0 } ],
  "previews": [ { "id": "…", "capability": "wallet.transfer", "status": "Completed", "predictedValueEur": 12.5,
                  "needsConsent": false, "expiresAt": "…",
                  "steps": [ { "seq": 1, "kind": "Sign", "status": "Confirmed", "network": "algorand-mainnet", "txIds": ["…"] } ] } ] }
```

Errors: `400 {"error"}` for bad input, `401` no/invalid session, `403` missing CSRF header, wrong origin or cross-site,
`404` unknown user/conversation or no impersonation active for a view endpoint, `409` impersonation already ended.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Admin:Admins` | empty | Admin emails separated by `;`. Empty: the dashboard is off. |
| `Admin:RequireVerifiedEmail` | `true` | Demand `email_verified: true` in the ID token. |
| `Admin:SessionHours` | `8` | Absolute session lifetime. |
| `Admin:SessionIdleMinutes` | `30` | Idle timeout. |
| `Admin:ImpersonationMinutes` | `30` | Length of one impersonation. |
| `Admin:UiDirectory` | `<app>/admin-ui` | Where the built dashboard lives (the container image puts it there). |

## Development

```bash
docker compose up -d redis postgres
cd backend/Biatec.Chat.Api && dotnet user-secrets set Admin:Admins "me@example.com" && dotnet run
pnpm --filter @biatec/admin dev        # Vite on :5174, proxies /admin/api and /admin/auth to the backend
pnpm --filter @biatec/admin test
dotnet test backend --filter Admin
```

The backend image builds the app in a Node stage (`backend/Dockerfile`) and copies `dist/` to `admin-ui/`.

## Threat model notes

| Threat | Control |
|---|---|
| Someone signs in with an email that is not an admin | allow-list check after ID token validation; `401` everywhere; sessions re-checked per request |
| Login CSRF / forced login | state bound to a `__Host-` login cookie, single use, 10 minute TTL, PKCE |
| Cross-site or same-site-other-origin request using the admin cookie | `SameSite=Strict`, no CORS, `Sec-Fetch-Site: same-origin`, custom header, exact `Origin` |
| Token theft from the browser | no token ever reaches the browser; the backend keeps none for admins |
| Cookie theft | `HttpOnly`, `Secure`, `__Host-`, opaque, server-side revocation (logout, expiry) |
| Admin abuse of impersonation | reason required, time-boxed, read-only, every start, stop and conversation view in the signed admin evidence chain |
| Subject in URLs (`/admin/api/user?sub=`, search) reaches proxy logs | The subject is a pseudonymous OIDC identifier, not contact data; the audit chain stores neither it nor a hash of it, only the random row id |
| Dashboard reads data of an erased user | erasure clears the subject from conversations and deletes the user row |
| XSS in the dashboard | Vue escaping, no `v-html`, CSP `script-src 'self'`, transcripts rendered as text |
