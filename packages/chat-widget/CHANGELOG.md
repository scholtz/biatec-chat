# Changelog

All notable changes to `@biatec/chat-widget`. Versions follow [semver](https://semver.org/); before 1.0 a minor
version may change the public API. Releases are tagged `chat-widget-v<version>`.

## Unreleased

- While the widget waits for a sign-in, scope elevation or sign-out in the popup, the header shows a Cancel button. The widget no longer treats a closed popup (`popup.closed`) as a cancel, so the identity provider can send `Cross-Origin-Opener-Policy: same-origin` on its pages; a wait ends by the status poll, the timeout or Cancel. Cancel is not reported as a failure: the widget returns to idle with a neutral announcement, and a sign-in or sign-out that completed just before the press is reported as it really ended.

## 0.1.0 (2026-10-20, first npm publish)

- First public release: the Vue 3 plugin `BiatecChat` (ESM and UMD, Vue as a peer dependency), `BiatecChatInline`,
  `BiatecAiChip` and the host API `useBiatecChat()` (`open`, `close`, `ask`, `setPageContext`, `setTheme`,
  `setLocale`).
- New standalone entry `@biatec/chat-widget/standalone` for non-Vue hosts (React, Docusaurus, plain HTML), Vue
  bundled, as an ES module and a one-script-tag IIFE (`window.BiatecChat`): `mountBiatecChat()`, `unmount()`,
  `autoMount()`, `useBiatecChat()`.
- New option `hostDarkAttribute` (for example `data-theme=dark`) for `theme: 'host'` on sites that mark dark mode with
  an attribute instead of a class; the standalone build uses it by default.
- New host CSS variable `--bc-offset-top` (default `0px`): the open floating panel stays below a fixed top navbar on
  short viewports (its height is capped at `100vh - offset-top - offset-bottom - 24px`).
- Published types are self-contained (no dependency on the unpublished `@biatec/chat-protocol`).
- `vue-i18n` and `lucide-vue-next` are bundled and are no longer runtime dependencies; `vue` is an optional peer
  (needed by the Vue plugin, not by the standalone build).
