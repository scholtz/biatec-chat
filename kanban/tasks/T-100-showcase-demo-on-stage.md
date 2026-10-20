---
id: T-100
title: Showcase demo site deployed to stage
status: in-progress
priority: P1
epic: "Deployment"
blocked_by: []
area: ["apps/demo/**", "k8s/base/demo.yaml", "k8s/base/kustomization.yaml", "k8s/stage/demo-ingress.yaml", "k8s/stage/kustomization.yaml", "k8s/main/kustomization.yaml", ".github/workflows/deploy-stage.yml", ".github/workflows/deploy.yml", ".github/workflows/ci.yml", "pnpm-workspace.yaml", "docs/hackathon-demo.md", "docs/demo-site.md"]
parallel: yes
conflicts_with: []
issue: "#204"
branch: "task/T-100"
owner: "worker-T-100"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#demonstration-script"
created: 2026-10-20
updated: 2026-10-20
---

# T-100 · Showcase demo site deployed to stage

## Context

Stage runs the backend and builders at `stage.chat.biatec.io` (T-071, `k8s/stage`), but there is no page a judge or
partner can open to see the product: the playgrounds (`packages/chat-widget/playground/index.html`, `dex.html` from
T-068) only run under `vite dev`, and T-059 is a test, not a showcase. The demo must show **everything that works today**
and stay inside our guidelines: architecture and security invariants (AGENTS.md "Security invariants",
`docs/architecture.md`, `docs/threat-model.md`) and the Biatec AI design system (`docs/design/README.md`,
`docs/design/biatec-ai-widget/`, `packages/chat-widget/AGENTS.md`). Demo script: PRODUCT-DESCRIPTION.md
"Demonstration Script" and `docs/hackathon-demo.md`.

## Goal

A public demo site on stage (for example `https://demo.stage.chat.biatec.io`, or `/demo` on the stage host) that walks a
visitor through every working capability with the real widget against the stage backend, deployed automatically by the
stage pipeline.

## Scope

- In: `apps/demo`: a small Vite + Vue 3 static site using `@biatec/chat-widget` from the workspace, built to the design
  system's tokens. Sections, each with a short "what you're seeing" note and a one-click prompt via
  `useBiatecChat().ask()`:
  1. Guest tier: product and blockchain Q&A from the knowledge base, suggestions, EN/SK switch, light/dark/host theme.
  2. Sign in (Biatec OIDC BFF, no tokens in the browser), session tier, agent modes analyze/prepare and pause.
  3. Data cards that are built: holdings, market snapshot (if T-069 is merged), Safe card.
  4. Actions: transfer, swap, add/remove liquidity, Safe create/propose/approve/execute: preview → review → Confirm →
     receipt, consent on the phone above threshold (Algorand **testnet** only).
  5. Evidence: export and offline verification (`packages/evidence-verifier`), export/erase of own conversations.
  6. Modes: floating, inline (`BiatecChatInline`) and workspace; a DEX-styled page reusing the T-068 look.
  7. "How it works": the architecture diagram and the security invariants, linking the docs on GitHub.
  Features not merged by build time are listed as "coming next", never faked or simulated.
- In: container (nginx static, non-root, read-only root FS like the other manifests), `k8s/base/demo.yaml`, stage
  ingress with TLS via cert-manager, and the demo image built and rolled out in `deploy-stage.yml`/`deploy.yml` with the
  same immutable tag scheme; `ci.yml` builds it on PRs.
- In: the demo origin registered for the stage tenant (allowed origin + `clientKey`) by the deploy's tenant upsert or a
  documented operator step; same-site cookie transport must give the authenticated tier on stage.
- In: `docs/demo-site.md` (URL, what each section shows, demo identities/testnet setup, reset steps) and a link from
  `docs/hackathon-demo.md`.
- Out: npm publishing and the BiatecCLAMM embed (T-099); production deployment of the demo (promotion can follow later);
  new product capabilities.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] `pnpm --filter demo build` produces a static site; `pnpm --filter demo dev` runs it against a local backend.
- [ ] A push to `main` deploys the demo to stage and the URL serves it over HTTPS; `kubectl rollout status` waits on it.
- [ ] On stage: guest Q&A answers, sign-in reaches the authenticated tier, and at least one testnet action goes
      preview → Confirm → receipt with an exportable evidence bundle that verifies offline.
- [ ] No token, secret or client secret in the built assets (grep the `dist/`); CSP set by nginx allows only the stage
      backend.
- [ ] Look follows the Biatec AI tokens in dark and light, and works at 375 px; axe check has no serious violations.
- [ ] Every section either works on stage or is labelled "coming next"; `docs/demo-site.md` lists which and why.
- [ ] Workflows pass `actionlint`; `kubectl kustomize k8s/stage` and `k8s/main` render.

## Implementation notes

- Cookie transport needs the demo and backend same-site: `demo.stage.chat.biatec.io` + `stage.chat.biatec.io` share
  `chat.biatec.io`; check the backend's cookie and CORS settings rather than adding a proxy.
- Reuse the playground's DEX page assets where possible instead of copying them; don't change the widget package (T-099
  owns its `package.json` and build).
- Bedrock cost: the demo prompts are fine on the default model, but do not add any automated load against stage.
- Code freeze is **Tuesday 20 October 2026, 13:30**: ship the guest + sign-in + one action path first, then add sections.

## Verification

`pnpm --filter demo build`; `docker build` of the demo image and run it locally against the dev backend; `actionlint`;
`kubectl kustomize k8s/stage`; after merge, open the stage URL and walk sections 1-7, screenshots in the hand-off.

## Hand-off

## Review

## Activity log

- 2026-10-20 07:10 · coordinator · (new) → backlog · created on the user's request (showcase demo on stage); no existing task covered it (T-059 is a test, T-068 a dev-only playground)
- 2026-10-20 07:17 · LukassSoukup · taken · status stays backlog
- 2026-10-20 07:17 · coordinator · backlog → in-progress · dispatched to worker
