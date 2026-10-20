# Changelog

All notable changes to `@biatec/chat-widget`. Versions follow [semver](https://semver.org/); before 1.0 a minor
version may change the public API. Releases are tagged `chat-widget-v<version>`.

## 0.1.0 (unreleased, first npm publish)

- First public release: the Vue 3 plugin `BiatecChat` (ESM and UMD, Vue as a peer dependency), `BiatecChatInline`,
  `BiatecAiChip` and the host API `useBiatecChat()` (`open`, `close`, `ask`, `setPageContext`, `setTheme`,
  `setLocale`).
- New standalone entry `@biatec/chat-widget/standalone` for non-Vue hosts (React, Docusaurus, plain HTML), Vue
  bundled, as an ES module and a one-script-tag IIFE (`window.BiatecChat`): `mountBiatecChat()`, `unmount()`,
  `autoMount()`, `useBiatecChat()`.
- New option `hostDarkAttribute` (for example `data-theme=dark`) for `theme: 'host'` on sites that mark dark mode with
  an attribute instead of a class; the standalone build uses it by default.
- Published types are self-contained (no dependency on the unpublished `@biatec/chat-protocol`).
- `vue-i18n` and `lucide-vue-next` are bundled and are no longer runtime dependencies; `vue` is an optional peer
  (needed by the Vue plugin, not by the standalone build).
