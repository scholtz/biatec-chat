# Demo site

A public showcase of Biatec AI: the real `@biatec/chat-widget` on a small static page, talking to the **stage**
backend, Algorand **testnet** only. Source in [`apps/demo`](../apps/demo), deployed by the stage pipeline.

| | |
|---|---|
| URL | <https://demo.stage.chat.biatec.io> (DEX-styled page: `/dex.html`, workspace mode: `/?mode=workspace`) |
| Backend | `https://stage.chat.biatec.io` (tenant `biatec-chat`, client key `biatec-demo`) |
| Image | `scholtz2/biatec-chat-demo:<version>-beta` and the moving `stage` tag, same version as the backend |
| Kubernetes | `k8s/base/demo.yaml` (Deployment + Service), `k8s/stage/demo-ingress.yaml`; deleted again in `k8s/main` |

## Known blocker at the time of writing

The widget's cookie transport posts chat messages to `/chat` (`packages/chat-widget/src/transport/types.ts`,
`PATHS.cookieChat`), but the backend serves `POST /chat/messages` (`backend/Biatec.Chat.Api/Chat/ChatEndpoints.cs`).
Every host where the session cookie works, this site included, therefore gets `404` and "Something went wrong" on the
first message. Verified locally: with the path changed to `/chat/messages` the same request returns `200` and streams.
The fix belongs to the widget package and is tracked as its own task; until it is on stage, every chat prompt on this
site fails, and the post-deploy checks below will show it.

## What each section shows

Every one-click prompt is sent with `useBiatecChat().ask()` as an ordinary, visible user message to the real backend.
Nothing on the page is simulated; what does not run on stage yet is labelled **Coming next** and has no prompt
(`apps/demo/src/sections.ts`, checked by `apps/demo/test/demo.test.ts`). The labels below are as of the code freeze
(20 October 2026) and still need the post-deploy checks further down.

| # | Section | Status on stage | Notes |
|---|---|---|---|
| 1 | Guest tier: knowledge-base Q&A, suggestions, sign-in card for wallet questions, EN/SK, widget theme follow page / light / dark | Works | Anonymous tier, Sonnet 4.5 |
| 2 | Sign in (popup at stage Biatec OIDC, BFF, no token in the browser), session tier, agent modes Analyze only / Prepare for approval / Pause | Works, sign in first | Needs the stage OIDC client (`OIDC_CLIENT_ID`) to allow `https://stage.chat.biatec.io/auth/callback` |
| 2 | Rule-based automation | Coming next | Shown disabled by the widget itself: the spec requires a confirmation per action |
| 3 | Holdings card, Safe card | Works, sign in first | `wallet.balance`, `safe.list` |
| 3 | Market snapshot card | Coming next | `market.snapshot` (T-069) was still in progress at build time |
| 4 | Transfer, swap, add/remove liquidity, Safe create / proposals / approve / execute: preview → review → Confirm → receipt | Works, sign in first | Testnet only; the account needs testnet ALGO (and the assets of the pool) |
| 4 | Phone approval in Biatec Authenticator above the threshold | Coming next | Biatec OIDC has no consent endpoints yet ([required-changes-biatec-oidc.md](required-changes-biatec-oidc.md)); shown against the mock OIDC in [hackathon-demo.md](hackathon-demo.md), not on stage |
| 5 | Download evidence from a receipt, export / erase own conversations (Activity panel), offline verification | Works, sign in first | Verification uses `packages/evidence-verifier` and the stage JWKS |
| 6 | Floating (default), inline (`BiatecChatInline`), workspace (`?mode=workspace`), DEX-styled host page | Works | The DEX page is the T-068 playground page reused unchanged; its order form and price are illustrative and labelled so |
| 7 | How it works: architecture and security invariants, links to the docs | Static | |

If a post-deploy check fails, change that item's `status` in `apps/demo/src/sections.ts` to `"next"` (with a `note`
saying why, and no `prompt`) and this table with it, rather than leaving a button that does not work.

## How it is wired

- **Same site, separate origin.** `demo.stage.chat.biatec.io` and `stage.chat.biatec.io` share the registrable domain
  `biatec.io`, so the backend's `__Host-` session cookie (`SameSite=Lax`, no `Domain`) is sent on the widget's
  credentialed requests and the cookie probe gives the authenticated transport. No proxy: the page calls the backend
  directly with CORS. `*.stage.chat.biatec.io` already resolves to the cluster ingress and cert-manager issues the
  certificate, so the host needed no new DNS record or cloud resource.
- **Tenant registration.** `deploy-stage.yml` passes `demo_url: https://demo.stage.chat.biatec.io` to `deploy.yml`,
  which after the tenant upsert appends that origin to `AllowedOrigins` and `biatec-demo` to `ClientKeys` of tenant
  `biatec-chat` when they are missing (idempotent; nothing else on the row changes, and it refuses an origin that is
  not under the backend's parent domain). Production gets no `demo_url`, so its tenant is untouched.
- **CSP.** nginx sends `connect-src https://stage.chat.biatec.io` and nothing else for network access,
  `script-src 'self'` (no inline script), `style-src 'self' 'unsafe-inline'` (the widget injects its stylesheet into
  its shadow root as a `<style>` element, PrimeVue does the same on the DEX page), `frame-ancestors 'none'`,
  `base-uri 'none'`, `form-action 'none'`. The backend origin is baked in at image build time (`BACKEND_URL` build
  argument, default stage), together with the bundle's backend URL.
- **No secrets.** The bundle holds the backend URL and the client key, which is a label, not a secret. The image build
  fails if `dist/` contains anything that looks like a token, client secret, private key or AWS key.
- **Container.** `nginxinc/nginx-unprivileged`, uid 101, port 8080, read-only root filesystem with `/tmp` as an
  `emptyDir`, all capabilities dropped, no service-account token. One replica; `/healthz` for the probes.
- **Pipeline.** `ci.yml` builds and tests the site (`pnpm --filter demo build`, `pnpm --filter demo test`) and builds
  the image on every PR. `deploy-stage.yml` builds and pushes it with the same `<version>-beta` / `stage` tags as the
  other images, and `deploy.yml` waits on `kubectl rollout status deployment/biatec-chat-demo`. `promote-production.yml`
  does not touch it.

## Run it locally

```sh
docker compose up -d redis postgres
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5000 dotnet run --project backend/Biatec.Chat.Api
pnpm install
pnpm --filter @biatec/chat-protocol build && pnpm --filter @biatec/chat-widget build   # the demo uses the widget's dist
pnpm --filter demo dev            # http://localhost:5173, dev tenant "localhost", client key "dev"
```

Port 5173 is fixed because the dev tenant only allows that origin (stop the widget playground first). Other targets:
`VITE_BIATEC_CHAT_URL=... VITE_BIATEC_CHAT_CLIENT_KEY=... pnpm --filter demo build`. The container:
`docker build -f apps/demo/Dockerfile -t biatec-chat-demo .` then
`docker run --rm --read-only --tmpfs /tmp -p 8080:8080 biatec-chat-demo`.

## Demo identities and testnet

- Use a Biatec OIDC **stage** identity (stage.oidc.biatec.io) with an Algorand address. For section 4, fund it with
  testnet ALGO from the Algorand testnet dispenser, and with the pool's assets for swap and liquidity.
- Keep the account on "always ask" with no spending limit, as in [hackathon-demo.md](hackathon-demo.md), or signing on
  testnet may be refused by Biatec OIDC.
- The Bedrock limit (50 requests per minute) is shared by the whole team: one presenter at a time, and no scripted load
  against stage.

## Reset

- Conversations: Activity panel → Erase my data (removes the message text; signed receipts and evidence hashes stay,
  as the panel explains), or simply sign out.
- Agent mode: the status pill → Prepare for approval or Resume.
- A stuck preview: Cancel on its card, or let it expire.
- Tenant: the deploy re-adds the demo origin if it was removed; to remove the demo from stage, delete the
  `biatec-chat-demo` Deployment, Service and Ingress and remove the origin and key from the tenant row.

## Post-deploy checks (stage)

Run after the merge to `main` and a green `deploy-stage` run, in a fresh browser profile:

1. `curl -sI https://demo.stage.chat.biatec.io/` → `200`, a valid Let's Encrypt certificate, and a
   `Content-Security-Policy` header whose `connect-src` is exactly `https://stage.chat.biatec.io`.
2. `kubectl -n biatec-chat-stage get deploy/biatec-chat-demo ingress/biatec-chat-demo certificate` → available,
   certificate `Ready`.
3. Tenant: `kubectl -n biatec-chat-stage exec statefulset/biatec-chat-postgres -- psql -U biatec -d biatec_chat -c
   'select "AllowedOrigins", "ClientKeys" from "Tenants" where "Id" = '"'"'biatec-chat'"'"';'` lists
   `https://demo.stage.chat.biatec.io` and `biatec-demo`.
4. Open the URL, DevTools console: no CSP violation. Network: `GET https://stage.chat.biatec.io/session/probe` twice,
   the second answering `cookieReturned: true`.
5. Section 1: one prompt answers from the knowledge base; "What is my ALGO balance?" shows the sign-in card. Switch
   the widget to Slovenčina and to Light; toggle the page theme with "Follow page".
6. Section 2: sign in in the popup; the header shows the email and address; the status pill opens the permissions
   dialog. DevTools → Application: no token in local or session storage, only the backend's cookie.
7. Section 4: one action end to end on testnet (the transfer of 0.1 ALGO to your own address is the cheapest): preview
   → review → Confirm → receipt with an explorer link.
8. Section 5: Download evidence on that receipt, then offline:
   `curl -sO https://stage.chat.biatec.io/.well-known/evidence-jwks.json && pnpm --filter evidence-verifier build &&
   node packages/evidence-verifier/cli.mjs verify <bundle>.json --jwks evidence-jwks.json` → verified.
9. Section 6: show and remove the inline panel, reload in workspace mode, open `/dex.html`.
10. At 375 px (DevTools device toolbar): no horizontal scroll, the widget opens full screen.
11. Take screenshots of sections 1 to 7 for the hand-off. Any check that fails: relabel as described above.
