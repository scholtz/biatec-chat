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

### Cutting a release (automatic)

1. In a pull request: bump `version` in `packages/chat-widget/package.json` (semver; before 1.0 a minor may break the
   API) and add a `## <version>` section to [CHANGELOG.md](https://github.com/scholtz/biatec-chat/blob/main/packages/chat-widget/CHANGELOG.md). The PR runs the workflow as a dry run
   (build, typecheck, test, pack, list the tarball's files, `npm publish --dry-run`; the last step is skipped with a
   notice when the version is already on npm, which npm would reject, as in every PR that doesn't bump the version);
   for a PR into `main` its notice says whether merging would publish.
2. Merge the PR. The push to `main` publishes the new version to npm (with provenance, in the `npm` environment), then
   creates the tag **`chat-widget-v<version>`** on the merged commit and a GitHub release whose text is that version's
   CHANGELOG section. Nothing else to do.

**The version must be greater than the highest published release** (semver, prereleases not counted). A lower or
equal version that is not on npm fails the check with an error, already in the PR, so a revert or a typo can never
become `latest`. (A version that is already on npm is simply skipped.)

**Prereleases** (`x.y.z-<id>…`, for example `0.2.0-beta.1` or `1.0.0-rc.1`) publish to the npm dist-tag `<id>`
(`beta`, `rc`; `next` when `<id>` is not a word, as in `0.2.0-1`), never to `latest`, so `npm install
@biatec/chat-widget` keeps installing the last release; install one with `npm install @biatec/chat-widget@beta`. Its
GitHub release is marked as a prerelease. A prerelease of an already released `x.y.z` (`0.1.0-rc.1` after `0.1.0`)
fails the check like a downgrade.

A push to `main` that touches the widget, `chat-protocol` or the workflow publishes nothing when the version in
`package.json` is already on npm (notice) or did not change in that push (notice; a **warning** if that version is
not on npm, meaning an earlier release failed or was never made: publish it with the tag or a manual run below). Every
path checks npm before publishing, and the publish job checks again right before `npm publish` (npm's "cannot publish
over the previously published version" also counts as already published), so no version is published twice, even
when a tag and a merge for the same version run at once: the tag the workflow creates is skipped (and, pushed with
`GITHUB_TOKEN`, it does not even start a workflow), and so is a tag or manual run for a published version. npm never
accepts the same version twice, so a fix needs a new version. A run that failed before publishing (for example
because auth is not set up yet) can be re-run; if only the tag and release failed, re-run the failed jobs (an existing
tag or release is kept).

### Other ways to publish

Both only for a commit on `main`, both skip a version that is already on npm, and both follow the same version and
prerelease rules as above:

- **Tag:** push `chat-widget-v<version>`, exactly the version in `package.json`, pointing at a commit `main` contains:

  ```sh
  git tag chat-widget-v0.2.0 origin/main
  git push origin chat-widget-v0.2.0
  ```

  The workflow fails if the tag and `package.json` differ, or if the tagged commit is not on `main`. After publishing
  it creates the GitHub release. Use it for a version that is in `package.json` on `main` but was never published
  (the warning on a push to `main` points here, for example after a release whose publish failed).
- **Manual run:** Actions → **publish-widget** → Run workflow on `main`, untick **dry_run** (ticked by default, so an
  accidental run only packs). After publishing it creates the tag and the GitHub release. A non-dry manual run on any
  other branch fails; a dry run works on any branch.

### How the workflow authenticates

1. **npm trusted publishing (primary, no secret).** The `publish` job runs with `permissions: id-token: write` and
   npm ≥ 11.5.1 (Node 24), so npm exchanges the GitHub OIDC token for a short-lived publish credential and adds a
   provenance statement. Nothing is stored in GitHub.
2. **`NPM_TOKEN` (fallback only).** If no trusted publisher is configured on npmjs.com, npm falls back to the token in
   the environment secret `NPM_TOKEN` (below), passed as `NODE_AUTH_TOKEN`. When the secret is not set, it is empty
   and unused. (A first publish of a package that doesn't exist yet would always need it, because a trusted publisher
   can only be configured on an existing package; `@biatec/chat-widget` exists since 0.1.0.) A publish npm rejects
   for auth fails the run with an error pointing to this section; it is never silently skipped.

| | |
| --- | --- |
| Secret name | `NPM_TOKEN` |
| Where it lives | Repository **scholtz/biatec-chat** → Settings → Environments → **`npm`** → Environment secrets. It is an *environment* secret of the `npm` environment, because the `publish` job declares `environment: npm`. (Settings → Secrets and variables → Actions → Environment secrets shows the same list.) Not a repository secret |
| Who can add it | A repository admin (today: scholtz). Collaborators with write access cannot create environments or secrets |
| How to create the token | npmjs.com → your avatar → **Access Tokens** → **Generate New Token** → **Granular Access Token**: Packages and scopes **Read and write**, limited to the `@biatec` scope (or just `@biatec/chat-widget` once it exists); no organization permissions; **short expiry** (7 to 30 days); allowed IP ranges empty (GitHub runners). Copy it once into the environment secret |
| Needed when | Only until trusted publishing is configured. Delete the secret (and revoke the token on npmjs.com) afterwards |

The `npm` environment can also get required reviewers (Settings → Environments → npm → Required reviewers), so every
publish waits for approval. The tag and the GitHub release are made by a separate `release` job, the only one with
`contents: write`, with the workflow's own `GITHUB_TOKEN`; no other secret is involved.

### One-time setup

1. **npm organization `biatec`** (reserves the `@biatec` scope): done by the scope owner (scholtz).
2. **First publish**: done. `0.1.0` (dist-tag `latest`) and `0.0.0-stage` were published on 2026-10-20 by a manual
   run of the earlier version of this workflow, which made no tag or GitHub release for them (the current workflow
   skips published versions, so it won't either; push `chat-widget-v0.1.0` and create its release on GitHub by hand
   if you want them). `0.0.0-stage` is a prerelease and lower, so the version check ignores it. **The next release is
   simply a version bump** (above).
3. **Configure the trusted publisher** (if not done yet): npmjs.com → package `@biatec/chat-widget` → Settings → **Trusted Publisher** →
   GitHub Actions: Organization or user `scholtz`, Repository `biatec-chat`, Workflow filename `publish-widget.yml`,
   Environment `npm`. Then, under Publishing access, choose "Require two-factor authentication and disallow tokens",
   and delete the `NPM_TOKEN` secret if one was set. From then on, every release is the automatic path above.

## Licence

MIT
