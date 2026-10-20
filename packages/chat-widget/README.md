# @biatec/chat-widget

**Biatec AI**, the embeddable assistant of Biatec DEX and Biatec Web, as a widget for any site. Visitors ask about
Biatec and Algorand; signed-in users can prepare transfers, swaps, liquidity and Biatec Safe actions, which they
always review and confirm themselves (and, above their limit, approve on their phone).

The widget never receives an OAuth token, key or seed: it talks to a Biatec Chat backend with an HttpOnly session
cookie, and signing happens on the backend at Biatec OIDC. It renders into its own Shadow DOM root, so your CSS and
the widget's never mix.

- Vue 3 hosts: a plugin (`BiatecChat`), with Vue as a peer dependency. ESM and UMD builds.
- Any other host (React, Docusaurus, plain HTML): the standalone build `@biatec/chat-widget/standalone`, with Vue
  bundled. One `<script>` tag or one `mountBiatecChat()` call.

Full guide: [docs/integration-widget.md](https://github.com/scholtz/biatec-chat/blob/main/docs/integration-widget.md).
Docusaurus example: [docs/integration-biatec-clamm.md](https://github.com/scholtz/biatec-chat/blob/main/docs/integration-biatec-clamm.md).

## Install

```sh
npm i @biatec/chat-widget
```

## Vue 3

```ts
import { createApp } from 'vue'
import { BiatecChat } from '@biatec/chat-widget'

createApp(App)
  .use(BiatecChat, {
    backendUrl: 'https://chat.biatec.io',
    clientKey: 'biatec-web', // identifies the host, not a secret
    theme: 'host',           // follows .p-dark on <html> (PrimeVue)
  })
  .mount('#app')
```

Drive it from your UI with `useBiatecChat()`: `open()`, `close()`, `ask(prompt)`, `setPageContext(text)`,
`setTheme(theme)`, `setLocale(locale)`. `BiatecChatInline` renders the chat as a panel in your layout.

## Standalone (React, Docusaurus, plain HTML)

One script tag; the widget mounts when the page has loaded:

```html
<script
  src="https://cdn.jsdelivr.net/npm/@biatec/chat-widget@0.1.0/dist/standalone/biatec-chat.standalone.iife.js"
  data-backend-url="https://chat.biatec.io"
  data-client-key="your-client-key"
  defer
></script>
```

Attributes: `data-backend-url` and `data-client-key` (required), `data-locale`, `data-chat-theme`
(`auto | light | dark | host`), `data-mode` (`floating | workspace`), `data-host-dark-class`,
`data-host-dark-attribute`, `data-page-context`. The script also defines `window.BiatecChat` with the functions below.

From a bundler (call it in the browser only):

```ts
import { mountBiatecChat } from '@biatec/chat-widget/standalone'

const chat = mountBiatecChat({ backendUrl: 'https://chat.biatec.io', clientKey: 'your-client-key' })
chat.setPageContext('Docs · Getting started')
chat.ask('What is a concentrated liquidity pool?')
chat.unmount()
```

`mountBiatecChat(options)` or `mountBiatecChat(container, options)` returns the same host API as `useBiatecChat()`
plus `host` and `unmount()`. One widget per page: a second call while a widget is mounted (by this build, the
script tag or the Vue plugin) returns that widget, and if its options or container differ they are ignored with a
console warning (call `unmount()` first to change them). A stray `[data-biatec-chat-host]` element that no copy of
the package mounted makes it throw. Without a DOM
(server rendering) importing the module has no side effect and `mountBiatecChat` does nothing.

Standalone defaults differ from the plugin, to fit non-Vue sites:

| Option | Plugin default | Standalone default |
| --- | --- | --- |
| `theme` | `auto` | `host` when `<html>` has a `data-theme` attribute (Docusaurus, many static sites), else `auto` (prefers-color-scheme) |
| `hostDarkAttribute` | none (`hostDarkClass: 'p-dark'`) | `data-theme=dark` whenever the theme is `host` and neither `hostDarkClass` nor `hostDarkAttribute` is given |
| `locale` | `en` | `<html lang>`, else `en` |

Size: the IIFE is about 300 kB minified, under **110 kB gzip** (budget enforced by `test/standalone.test.ts`), with
Vue, vue-i18n, the icons and the CSS included.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `backendUrl` | required | Biatec Chat backend. Same-site with your page for the signed-in tier, otherwise anonymous only |
| `clientKey` | required | Host label registered on the backend tenant; not a secret |
| `locale` | `en` | `en` or `sk`; others fall back to English |
| `theme` | `auto` | `auto` (prefers-color-scheme), `light`, `dark`, `host` (follows your `<html>`) |
| `hostDarkClass` | `p-dark` | Class on `<html>` meaning dark for `theme: 'host'` |
| `hostDarkAttribute` | none | Attribute on `<html>` meaning dark, `name=value` or bare `name`; replaces `hostDarkClass` when set |
| `mode` | `floating` | `floating`, `workspace`, or `inline` (Vue `BiatecChatInline` only) |
| `suggestions` | `default` | Sample prompts, or your own list |
| `pageContext` | none | Opt-in description of the page; nothing is read from your page otherwise |

Restyle through CSS custom properties on `[data-biatec-chat-host]`, for example `--bc-offset-bottom`, `--bc-z`,
`--color-brand-primary`.

## Publishing

For maintainers of [scholtz/biatec-chat](https://github.com/scholtz/biatec-chat). The workflow is
[`.github/workflows/publish-widget.yml`](https://github.com/scholtz/biatec-chat/blob/main/.github/workflows/publish-widget.yml); CI/CD overview in
[docs/cicd.md](https://github.com/scholtz/biatec-chat/blob/main/docs/cicd.md).

### How the workflow authenticates

1. **npm trusted publishing (primary, no secret).** The `publish` job runs with `permissions: id-token: write` and
   npm ≥ 11.5.1 (Node 24), so npm exchanges the GitHub OIDC token for a short-lived publish credential and adds a
   provenance statement. Nothing is stored in GitHub.
2. **`NPM_TOKEN` (fallback only).** If no trusted publisher is configured on npmjs.com (for example before the very
   first publish), npm falls back to the token in the GitHub secret `NPM_TOKEN`, passed as `NODE_AUTH_TOKEN`. When the
   secret is not set, it is empty and unused.

| | |
| --- | --- |
| Secret name | `NPM_TOKEN` |
| Where it lives | Repository **scholtz/biatec-chat** → Settings → Environments → **`npm`** → Environment secrets. It is an *environment* secret of the `npm` environment, because the `publish` job declares `environment: npm`. (Settings → Secrets and variables → Actions → Environment secrets shows the same list.) Not a repository secret |
| Who can add it | A repository admin (today: scholtz). Collaborators with write access cannot create environments or secrets |
| How to create the token | npmjs.com → your avatar → **Access Tokens** → **Generate New Token** → **Granular Access Token**: Packages and scopes **Read and write**, limited to the `@biatec` scope (or just `@biatec/chat-widget` once it exists); no organization permissions; **short expiry** (7 to 30 days); allowed IP ranges empty (GitHub runners). Copy it once into the environment secret |
| Needed when | Only until trusted publishing is configured. Delete the secret (and revoke the token on npmjs.com) afterwards |

The `npm` environment can also get required reviewers (Settings → Environments → npm → Required reviewers), so every
publish waits for approval.

### One-time setup

1. **Create the npm organization `biatec`** on npmjs.com (avatar → Add Organization → name `biatec`, free plan for
   public packages). This reserves the `@biatec` scope. Done by the scope owner (scholtz).
2. **First publish, by hand** (trusted publishers can be configured only on a package that already exists). From a
   clean checkout of `main`, logged in with `npm login` as an owner of the `biatec` org:

   ```sh
   pnpm install --frozen-lockfile
   pnpm --filter @biatec/chat-protocol build
   cd packages/chat-widget
   pnpm run build && pnpm run test
   pnpm pack                                  # rewrites workspace: ranges; publish this exact tarball
   npm publish biatec-chat-widget-0.1.0.tgz --access public
   ```

   Alternatively set the `NPM_TOKEN` environment secret above and cut the release with the workflow (next section).
3. **Configure the trusted publisher**: npmjs.com → package `@biatec/chat-widget` → Settings → **Trusted Publisher** →
   GitHub Actions: Organization or user `scholtz`, Repository `biatec-chat`, Workflow filename `publish-widget.yml`,
   Environment `npm`. Then, under Publishing access, choose "Require two-factor authentication and disallow tokens",
   and delete the `NPM_TOKEN` secret if one was set.

### Cutting a release

1. In a pull request: bump `version` in `packages/chat-widget/package.json` (semver; before 1.0 a minor may break the
   API) and add the entry to [CHANGELOG.md](https://github.com/scholtz/biatec-chat/blob/main/packages/chat-widget/CHANGELOG.md). The PR runs the workflow as a dry run (pack and
   `npm publish --dry-run`, listing the tarball's files).
2. After merging, tag `main` with **`chat-widget-v<version>`**, exactly the version in `package.json`:

   ```sh
   git tag chat-widget-v0.1.0 origin/main
   git push origin chat-widget-v0.1.0
   ```

   The workflow fails if the tag and `package.json` differ, or if the tagged commit is not on `main`. Or run it by
   hand: Actions → **publish-widget** → Run workflow on `main`, untick **dry_run** (ticked by default, so an
   accidental run only packs). A non-dry manual run on any other branch fails; only code merged into `main` is
   ever published.
3. npm never accepts the same version twice: once a version is published, a fix needs a new version and tag. A run that failed before publishing can simply be re-run.

## Licence

MIT
