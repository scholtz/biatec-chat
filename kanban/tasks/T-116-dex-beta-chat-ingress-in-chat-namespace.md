---
id: T-116
title: "Route beta.dex.biatec.io/chat with an Ingress in the chat namespace (the ExternalName hop times out)"
status: review
priority: P1
epic: "Deploy & docs"
blocked_by: [T-115]
area: ["k8s/stage/**", "k8s/stage/dex-beta-ingress-test/**", "docs/integration-biatec-dex.md", "docs/cicd.md", "k8s/README.md", "PRODUCT-DESCRIPTION.md"]
parallel: yes
conflicts_with: []
issue: "#247"
branch: "task/T-116"
owner: "session-20261021-claude"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#embedding-modes"
created: 2026-10-21
updated: 2026-10-21
---

# T-116 · Route beta.dex.biatec.io/chat with an Ingress in the chat namespace (the ExternalName hop times out)

## Context

T-115 mounted the backend under `https://beta.dex.biatec.io/chat` and BiatecDEX#82/#83 added the DEX side: an `ExternalName`
Service in namespace `biatec` pointing at `biatec-chat-backend.biatec-chat-stage.svc.cluster.local` and a second Ingress
`biatec-dex-beta-chat` in front of it. Verified in a throwaway kind cluster (real ingress-nginx), it routed correctly. **On the
real stage cluster it does not:** after the DEX stage deploy (2026-10-21 11:14 UTC), `GET https://beta.dex.biatec.io/chat/session/probe`
returns **504 Gateway Time-out after exactly 5.05 s** (nginx's `proxy_connect_timeout`), while the DEX app on the same host is fine
(`/` and `/chatty` serve the SPA) and the same controller reaches the chat pods through `stage.chat.biatec.io` (`/health` is 200 in 0.09 s).
So the rule matches and the request is forwarded; the controller cannot open a connection to the upstream of the
`ExternalName` Service. For an `ExternalName` upstream ingress-nginx resolves the name itself and connects to the Service's
**ClusterIP**; for a normal Service it connects to the **pod IPs** straight from the endpoints. Whatever blocks the first
(cluster DNS for the controller, a hostNetwork controller, a network policy or CNI rule on ClusterIPs) is a cluster property
this repository cannot see.

## Goal

`https://beta.dex.biatec.io/chat/...` reaches the backend without depending on anything the `ExternalName` hop needs, by taking
the same path `stage.chat.biatec.io` takes.

## Scope

- In:
  - An Ingress `biatec-chat-dex-beta` in the stage overlay (namespace `biatec-chat-stage`) for host `beta.dex.biatec.io`, path
    `/chat` and below with the full path kept (identity `rewrite-target`), straight to Service `biatec-chat-backend`. Ingresses for one host
    in different namespaces are merged by ingress-nginx; the DEX ingress keeps owning the host's certificate (no `tls`, no
    cert-manager annotation here), and the DEX catch-all keeps everything else.
  - The path is written so that it sorts before the DEX-side `/(chat(?:/.*)?)$` of the broken route (ingress-nginx orders a host's
    locations by path string, descending, and a duplicate path across ingresses would be won by the older one), so the new route wins
    even while the old objects still exist in the cluster.
  - A reproducible check against a real ingress-nginx in kind with the DEX ingress and the *old broken* DEX-side route present in
    another namespace (`k8s/stage/dex-beta-ingress-test/run.sh`), CRLF/Windows-safe like the DEX one.
  - Docs: `integration-biatec-dex.md` §7, `cicd.md`, `k8s/README.md`, the spec bullet (the DEX ingress contract is now "the DEX
    ingress keeps the host and its certificate; the chat repo routes `/chat`").
- Out:
  - The BiatecDEX change (remove the `ExternalName` Service and the `biatec-dex-beta-chat` Ingress from its manifest and its
    check), tracked in a BiatecDEX issue. `kubectl apply` never deletes, so a cluster admin removes the two objects once
    (`kubectl -n biatec delete ingress biatec-dex-beta-chat; kubectl -n biatec delete service biatec-chat-backend`); the DEX deploy token has no `delete`.
  - Finding why the `ExternalName` hop times out (needs the controller log on the cluster).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] In kind with a real ingress-nginx, the DEX ingress in namespace `biatec`, the old `ExternalName` route (pointing nowhere) in `biatec`, and
      the new Ingress in `biatec-chat-stage`: `/chat/session/probe`, `/chat/chat/messages`, `/chat/auth/...`, `/chat`, `/chat/` reach the chat
      backend with the full path, query, `Host` and `Origin`/`Cookie`; `/`, `/markets`, `/chatty`, `/chat-x` reach the DEX app; SSE is not buffered.
- [ ] The stage overlay renders with the new Ingress and the production overlay without it.
- [ ] After the stage deploy: `curl -i https://beta.dex.biatec.io/chat/session/probe` is 200 with `Set-Cookie: __Host-biatec_session=...; path=/; secure; httponly; samesite=lax`.
- [ ] CI is green.

## Implementation notes

- ingress-nginx: two Ingresses for one host in different namespaces merge into one server; same host and path in both: the
  older wins, hence the distinct path. Both being regex paths with `rewrite-target`, the sort puts `/(chat(?:/.*)?)/?$` before
  `/(chat(?:/.*)?)$` (`/` is above `$`).
- `ingressClassName: nginx` like the other stage ingresses.

## Verification

`k8s/stage/dex-beta-ingress-test/run.sh`, `kubectl kustomize k8s/stage | grep -B2 -A12 biatec-chat-dex-beta`, then the curl above.

## Hand-off

- **Summary:** Ingress `biatec-chat-dex-beta` (ns `biatec-chat-stage`, host `beta.dex.biatec.io`, path `/(chat(?:/.*)?)/?$`, identity rewrite) straight to the backend Service; added to the stage overlay only.
- **Files changed:** `k8s/stage/dex-beta-ingress.yaml`, `k8s/stage/kustomization.yaml`, `k8s/stage/dex-beta-ingress-test/{run.sh,fakes.yaml}`, docs.
- **How it was verified:** `dex-beta-ingress-test/run.sh` in kind with real ingress-nginx v1.12.1, DEX catch-all and the old broken ExternalName route (older) present: ALL CHECKS PASSED. Stage overlay renders the Ingress, main does not.
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:** why the ExternalName hop times out (controller log line for `beta.dex.biatec.io` on the cluster).

## Review

Round 1 (high): fixed explicit readiness failure in run.sh, softened the unverified-cause wording in docs. Declined: case-insensitive match of `/Chat` (same as the DEX-side rule; no DEX route starts with /chat in any case), `proxy-body-size` (equal to the backend's own host), HTTP status in the fake checks (the fake always answers 200; the test asserts routing), duplicate prose (each doc has a different reader).

## Activity log

Append-only. Format: `- YYYY-MM-DD HH:MM · <actor> · <from> → <to> · <note>`

- 2026-10-21 13:20 · scholtz · (new) → backlog · created after the live check of T-115 / BiatecDEX#83: /chat returns 504 after 5 s through the ExternalName hop
- 2026-10-21 13:25 · scholtz · backlog → in-progress · taken, branch task/T-116, issue #247
- 2026-10-21 13:50 · scholtz · in-progress → review · PR #248; post-deploy curl check pending the stage deploy
