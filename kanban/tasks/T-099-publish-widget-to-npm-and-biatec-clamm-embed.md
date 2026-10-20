---
id: T-099
title: Publish @biatec/chat-widget to npm and embed it in BiatecCLAMM
status: backlog
priority: P1
epic: "Deployment"
blocked_by: []
area: ["packages/chat-widget/package.json", "packages/chat-widget/vite.config.ts", "packages/chat-widget/vite.standalone.config.ts", "packages/chat-widget/src/standalone/**", "packages/chat-widget/scripts/**", "packages/chat-widget/README.md", "packages/chat-widget/CHANGELOG.md", "packages/chat-widget/test/standalone*.test.ts", ".github/workflows/publish-widget.yml", "docs/integration-widget.md", "docs/integration-biatec-clamm.md"]
parallel: yes
conflicts_with: []
issue: "#203"
branch: ""
owner: ""
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#components"
created: 2026-10-20
updated: 2026-10-20
---

# T-099 · Publish @biatec/chat-widget to npm and embed it in BiatecCLAMM

## Context

Spec, Components: the widget ships as "`@biatec/chat-widget` (npm) … injected into Biatec DEX, Biatec Web, and any Vue 3
host"; Widget: "Published to npm as `@biatec/chat-widget`, ESM and UMD builds, Vue 3 as a peer dependency." Today the
package is `0.0.0` and unpublished (`docs/integration-widget.md` says "install it from the workspace until it is"). T-068
and T-071 both left npm publishing out of scope.

The first non-DEX host is **BiatecCLAMM** (https://github.com/scholtz/BiatecCLAMM): the contracts and
`biatec-concentrated-liquidity-amm` library. Its only web UI is the **Docusaurus 3 site** in `docusaurus/` (React 18,
`url: https://beta.dex.biatec.io`, light/dark via `<html data-theme="dark|light">`, i18n folder). A React host can't
`app.use()` a Vue plugin, so the package also needs a framework-agnostic entry. We never edit BiatecCLAMM (AGENTS.md):
the host-side changes go into a hand-off doc for its maintainers, the way `docs/integration-biatec-dex.md` does for DEX.

## Goal

`npm i @biatec/chat-widget` works for any host: Vue 3 hosts use the existing plugin, and non-Vue hosts (BiatecCLAMM's
Docusaurus site first) get the widget from one script tag or one `mountBiatecChat()` call, styled to fit and following
the site's theme.

## Scope

- In: npm-ready `package.json` (version `0.1.0`, `repository`, `homepage`, `keywords`, `publishConfig.access: public`,
  `files`, `exports` incl. the new entry, `prepublishOnly` build + test); `@biatec/chat-protocol` types bundled or the
  package published too (it's a devDependency today; the published `.d.ts` must not import an unpublished package).
- In: a **standalone entry** (`@biatec/chat-widget/standalone`, ESM + IIFE/UMD with Vue bundled) exporting
  `mountBiatecChat(el | options)` / `unmount()` and the same `ask/open/close/setPageContext` host API as
  `useBiatecChat()`; optionally a `<biatec-chat>` custom element. No second copy of the component code: it wraps the
  existing plugin root.
- In: `theme: 'host'` support for hosts that toggle `data-theme="dark"` on `<html>` (Docusaurus), next to the existing
  `.p-dark` class watcher (for example `hostDarkAttribute: 'data-theme=dark'`); keep it a small option.
- In: `.github/workflows/publish-widget.yml`: on a `chat-widget-v*` tag or manual dispatch, build, test, `npm publish
  --provenance` with `NPM_TOKEN` from a GitHub environment secret; dry-run (`npm pack --dry-run`) on PRs touching the
  package. Document the secret in `docs/integration-widget.md` (not in `docs/cicd.md`, owned by T-071).
- In: `docs/integration-biatec-clamm.md`: exact Docusaurus changes for the maintainers (dependency, a
  `clientModules` file or `src/theme/Root.js` wrapper that calls `mountBiatecChat` in the browser only, `backendUrl`,
  `clientKey`, theme and locale mapping from Docusaurus i18n, `pageContext` per docs page, CSP/CORS and the tenant
  `allowed origin` for `beta.dex.biatec.io`), plus a copy-paste snippet that compiles.
- In: update `docs/integration-widget.md` (install from npm, standalone section, remove "not published").
- Out: changes in the BiatecCLAMM repository; the stage demo page (T-100); registering the BiatecCLAMM origin as a
  tenant in production (operator step, listed in the doc).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] `pnpm --filter @biatec/chat-widget build` emits ESM, UMD and the standalone bundle; `npm pack --dry-run` lists only
      `dist/`, `README.md`, `LICENSE`, `package.json`, and the packed tarball installs into an empty Vite + Vue app and
      an empty React app and both type-check.
- [ ] The published types don't reference `@biatec/chat-protocol` unless that package is published as well.
- [ ] A plain HTML page with one `<script>` tag of the standalone build shows the launcher, signs in, and follows a
      `data-theme` toggle on `<html>`; covered by a vitest/jsdom test.
- [ ] A local Docusaurus 3 scratch site following `docs/integration-biatec-clamm.md` step by step renders the widget on
      every page without hydration errors and with no SSR access to `window` (screenshot dark + light in the hand-off).
- [ ] `publish-widget.yml` passes `actionlint`; no token in the repo; publishing needs the tag or a manual run.
- [ ] Existing widget tests and `typecheck:playground` still pass.

## Implementation notes

- Vue as a **peer** for the plugin entry, **bundled** for the standalone entry; the standalone bundle has a size budget
  in the hand-off (gzip).
- Docusaurus renders on the server: the doc's snippet must guard with `ExecutionEnvironment.canUseDOM` or live in a
  `clientModules` file.
- The browser still never receives tokens; same-site rules for the authenticated tier (cookie transport) apply to
  `beta.dex.biatec.io` → state the needed backend host (`chat.beta.dex.biatec.io` or the public fallback) in the doc.
- Actual first publish needs the `NPM_TOKEN` secret and the `@biatec` npm scope owner (Scholtz); if missing, finish
  everything up to the dry run and list it as an open question.

## Verification

`pnpm --filter @biatec/chat-widget build && pnpm --filter @biatec/chat-widget test`; `npm pack --dry-run`; install the
tarball in scratch Vite-Vue, Vite-React and Docusaurus apps; `actionlint .github/workflows/publish-widget.yml`.

## Hand-off

## Review

## Activity log

- 2026-10-20 07:10 · coordinator · (new) → backlog · created on the user's request (npm publish + BiatecCLAMM embed); no existing task covered it (T-068, T-071 list npm publishing as out of scope)
- 2026-10-20 07:17 · LukassSoukup · taken · status stays backlog
