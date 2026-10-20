# Integrating the chat widget

How a host site embeds `@biatec/chat-widget`. The specification is [PRODUCT-DESCRIPTION.md](../PRODUCT-DESCRIPTION.md) and
wins on any conflict. What works today is marked; see [architecture.md](architecture.md) for the full status.

**Status of the widget on `main`:** built and tested: the plugin and standalone build, the Biatec AI design system in a
Shadow DOM root, `theme: 'host'`, the three modes, `BiatecChatInline` and `useBiatecChat()`, the cookie probe with the public
fallback transport, the streaming chat UI with the sign-in popup, action preview, consent, receipt and data cards, the
permissions dialog and activity timeline, the export view, and English and Slovak. For Biatec DEX specifically, see [integration-biatec-dex.md](integration-biatec-dex.md);
for the BiatecCLAMM Docusaurus site (a React host), see [integration-biatec-clamm.md](integration-biatec-clamm.md). The package
is `@biatec/chat-widget`, published on npm since 0.1.0 (see [Publishing to npm](#publishing-to-npm)).

## 1. Install the plugin

Vue 3 is a peer dependency of the plugin (`^3.4.0`, marked optional because the standalone build bundles its own Vue).
Hosts that are not Vue apps use the [standalone build](#standalone-build-react-docusaurus-plain-html) instead.

```sh
npm i @biatec/chat-widget
# inside this monorepo
pnpm add @biatec/chat-widget@workspace:*
```

Register it with one `use` call. The widget injects its own CSS into its shadow root, so no stylesheet import is needed
(`@biatec/chat-widget/style.css` is exported only for hosts that mount `BiatecChatRoot` themselves):

```ts
import { createApp } from 'vue'
import { BiatecChat } from '@biatec/chat-widget'

createApp(App)
  .use(BiatecChat, {
    backendUrl: 'https://chat.biatec.io',
    clientKey: 'biatec-web',          // identifies the host, not a secret
    locale: 'en',
    theme: 'auto',
    suggestions: 'default',
  })
  .mount('#app')
```

The plugin mounts its own small Vue app into a `<div data-biatec-chat-host>` appended to `document.body`, so it never touches the
host's component tree or root element. A second install (hot reload, two apps) reuses the existing widget instead of adding
another. Run the playground with `pnpm --filter @biatec/chat-widget dev`.

### Options

| Option | Required | Default | Meaning |
| --- | --- | --- | --- |
| `backendUrl` | yes | | Base URL of your chat backend. Trailing slashes are removed. Must be same-site with the host page to get the authenticated tier |
| `clientKey` | yes | | A label for the host, not a secret. The backend checks it against the tenant's registered keys on the cookie transport |
| `locale` | no | `en` | Message language: `en` or `sk`; other values fall back to English |
| `theme` | no | `auto` | `auto`, `light`, `dark` or `host` (follows the host's dark class on `<html>`) |
| `hostDarkClass` | no | `p-dark` | The class `theme: 'host'` watches (PrimeVue's `darkModeSelector`, as in Biatec DEX and Biatec Web) |
| `hostDarkAttribute` | no | none | An attribute on `<html>` meaning dark for `theme: 'host'`: `name=value` (`data-theme=dark`, Docusaurus/Infima) or a bare `name` (present means dark). When set it replaces `hostDarkClass`. The standalone build defaults to `data-theme=dark` |
| `mode` | no | `floating` | `floating` (launcher bottom-right), `inline` (no launcher; mount `BiatecChatInline` in a host column) or `workspace` (launcher, panel opens full screen). See [Modes and the host API](#modes-and-the-host-api) |
| `suggestions` | no | `default` | `default` or your own list of sample prompts |
| `pageContext` | no | none | Opt-in string describing the page (for example the pool being shown). Nothing is read from the host page unless you set it. The assistant treats it as untrusted data |
| `autoMount` | no | `true` | Set `false` to provide the options only, with no floating widget; mount `BiatecChatInline` where you want the chat |

The plugin throws at install if `backendUrl` or `clientKey` is missing.

### Styling

The widget's look is the Biatec AI design system ([design/README.md](design/README.md)); dark is the default. The widget
renders into a Shadow DOM root on its host element, so host CSS such as Tailwind preflight or a PrimeVue theme cannot reach it,
and its styles (`packages/chat-widget/src/styles.css`) are injected there. Restyle it only through CSS custom properties on the host
element: `--color-*`, `--font-*`, `--radius-*`, `--space-*`, `--shadow-*`, and `--bc-z` for the stacking order (default 1000,
below PrimeVue overlays). The widget never loads web fonts; it uses Inter when the host provides it.

Position the floating widget with three more custom properties on the host element:

| Property | Default | Effect |
| --- | --- | --- |
| `--bc-offset-right` | `24px` | Distance of the launcher, minimized bar and floating panel from the right edge |
| `--bc-offset-bottom` | `24px` | Distance from the bottom edge, for example to clear a "Back to top" button |
| `--bc-offset-top` | `0px` | Height of a fixed top navbar. The open floating panel is at most `100vh - offset-top - offset-bottom - 24px` tall (and never taller than 700 px), so its top stays 24 px below the navbar on short viewports |

```css
[data-biatec-chat-host] {
  --bc-offset-top: 60px;     /* the host's fixed navbar */
  --bc-offset-bottom: 4.5rem;
}
```

With the defaults the floating panel is `min(700px, 100vh - 48px)`, as before. The offsets do not apply below 640 px wide (the
open panel is full screen there) or to the `workspace` and `inline` frames.

### Modes and the host API

The widget has three modes, chosen with the `mode` option (`'floating' | 'inline' | 'workspace'`; any other value falls back to
`floating`). `ChatMode`, `ChatTheme`, `BiatecChatOptions`, `BiatecChatApi` and `AskResult` are exported as types.

| Mode | What the user sees | Notes |
| --- | --- | --- |
| `floating` (default) | Launcher bottom-right; it opens a floating panel that can be minimized to a bar, expanded to the workspace frame and closed | Below 640 px wide an open panel is full screen |
| `workspace` | The same launcher, but the panel opens full screen with the Activity aside shown | Closing returns to the launcher |
| `inline` | No launcher and no floating panel. A panel appears only where you mount `BiatecChatInline` | Never goes full screen and does not close on Esc; its header has no Expand |

While a `BiatecChatInline` is mounted, the floating launcher and panel hide in every mode, and the inline panel shares the floating
widget's conversation and session. Unmounting it brings the launcher back (not in `mode: 'inline'`).

#### `BiatecChatInline`

A component that renders the widget as a panel in your layout, for example a column next to an order form. It is rendered into its
own Shadow DOM root, like the floating widget, so host CSS and widget CSS never mix. It takes two optional props:

| Prop | Meaning |
| --- | --- |
| `options` | `BiatecChatOptions`. Options are taken in this order: the options of an already-mounted floating widget, then this prop, then the plugin's options (see below) |
| `services` | Transport and auth overrides for tests |

```vue
<script setup lang="ts">
import { BiatecChatInline } from '@biatec/chat-widget'
</script>

<template>
  <div class="trade-page" style="display:grid;grid-template-columns:1fr minmax(360px,420px);gap:24px">
    <OrderForm />
    <BiatecChatInline />   <!-- height and width come from your column -->
  </div>
</template>
```

Install the plugin with `mode: 'inline'` so that nothing floats and the inline panel is the only UI:

```ts
createApp(App)
  .use(BiatecChat, { backendUrl: 'https://chat.biatec.io', clientKey: 'biatec-dex', mode: 'inline' })
  .mount('#app')
```

Options are resolved in this order: an already-mounted floating widget's options win, then the `options` prop, then the options the
plugin provides. With none of them, the component logs a warning and renders nothing. The probe for an existing session
(`GET /session`) runs when the inline panel mounts.

With `autoMount: false` the plugin only provides options, so `BiatecChatInline` is the only UI. `BiatecChatRoot` is exported but is
not a supported host mount point: it takes already-resolved internal options, not `BiatecChatOptions`. Mounting the floating
widget yourself is a possible follow-up.

#### `useBiatecChat()`

`useBiatecChat()` returns the host API. It is not bound to a component, so call it from any module or handler, including before the
widget is mounted. It always returns the same object. The calls go to the most recently mounted widget.

| Call | Does |
| --- | --- |
| `open()` | Opens (or restores from the minimized bar) the panel and moves focus to the composer. The first open starts the session probe; nothing touches the network on page load. If called before a widget is mounted, the first widget opens as soon as it mounts |
| `close()` | Closes the panel back to the launcher and cancels an early `open()` |
| `ask(prompt): Promise<AskResult>` | Opens the panel and sends `prompt` as a normal, visible user message. Resolves to an `AskResult` |
| `setPageContext(text?)` | Sets the opt-in page context sent with later messages. `undefined`, `null` or a blank string clears it, and also overrides the `pageContext` plugin option |
| `setTheme(theme)` | `'auto' \| 'light' \| 'dark' \| 'host'`, applied without remounting. An unknown value logs a warning and is ignored |
| `setLocale(locale)` | Switches the widget language without remounting (`en`, `sk`). Unknown locales fall back to English. An empty or non-string value is ignored |

`ask` resolves to one of:

| Result | Meaning |
| --- | --- |
| `sent` | The prompt is now a visible user message in the conversation. Only this result means something was sent |
| `busy` | A reply is still streaming, or another `ask` is starting. Nothing was sent and the panel shows a short notice |
| `empty` | The prompt was blank or not a string |
| `unavailable` | No widget is mounted, or `mode: 'inline'` and no `BiatecChatInline` is mounted. The prompt could not be shown, so it was not sent (a console warning is logged) |

```ts
import { useBiatecChat } from '@biatec/chat-widget'

const chat = useBiatecChat()

// Page context is untrusted data; it is attached to each message at send time, so it never leaves the page before
// the user has opened the panel and a message is sent.
chat.setPageContext('Biatec DEX · testnet · pair EURS/USDC')

const result = await chat.ask('Add liquidity to the Biatec DEX EURS/USDC pool')
if (result === 'busy') {
  // The assistant is still answering the previous question; offer a retry instead of queueing.
} else if (result === 'unavailable') {
  // Mount BiatecChatInline (inline mode) or install the plugin first.
}

chat.setTheme('host')   // follow the host's dark class on <html> (see hostDarkClass)
chat.setLocale('sk')    // Slovak; unknown locales fall back to English
chat.open()
chat.close()
```

`ask` never executes anything: actions still end in a preview the user reviews and confirms. The playground
(`packages/chat-widget/playground/main.ts`) drives every call from buttons; run it with `pnpm --filter @biatec/chat-widget dev`.

#### Known limitations

- An inline panel mounted before the floating root has its own session, not a shared one.
- An early `open()` in `mode: 'inline'` (before a `BiatecChatInline` is mounted) is dropped; the inline panel is always shown while mounted.
- When a floating widget already exists, the `options` prop of `BiatecChatInline` is ignored in favour of the floating widget's options.
- An `ask` made during the first session probe still sends if the user closes the panel meanwhile.
- Esc closes the Activity aside even when the aside has no content yet.
- Theme and viewport-size watching is duplicated between the floating and the inline root (a small cost, no visible effect).
- `ask` is not queued: while a reply streams it returns `busy`.
- `setPageContext` takes a plain string and is sent as the product field of the chat request's host context; there is no structured form yet.

### Standalone build (React, Docusaurus, plain HTML)

A host that is not a Vue app cannot `app.use()` the plugin. `@biatec/chat-widget/standalone` bundles Vue and mounts the
same widget root (no second copy of the component code) with one call or one script tag. It is an ES module
(`dist/standalone/biatec-chat.standalone.js`, for bundlers) and a minified IIFE
(`dist/standalone/biatec-chat.standalone.iife.js`, global `window.BiatecChat`, also the package's `unpkg`/`jsdelivr`
file). The IIFE is about 300 kB minified, under a 110 kB gzip budget enforced by `test/standalone.test.ts`.

One script tag (it mounts on `DOMContentLoaded`, or at once if the page has loaded):

```html
<script
  src="https://cdn.jsdelivr.net/npm/@biatec/chat-widget@0.1.0/dist/standalone/biatec-chat.standalone.iife.js"
  data-backend-url="https://chat.biatec.io"
  data-client-key="example-web"
  defer
></script>
```

The options are `data-*` attributes: `data-backend-url` and `data-client-key` (required), `data-locale`,
`data-chat-theme` (not `data-theme`, which many sites style), `data-mode`, `data-host-dark-class`,
`data-host-dark-attribute`, `data-page-context`. Without `data-backend-url` the tag only defines `window.BiatecChat`.
A missing `data-client-key` is reported on the console, never thrown into the page.

From a bundler, in the browser only:

```ts
import { mountBiatecChat, useBiatecChat } from '@biatec/chat-widget/standalone'

const chat = mountBiatecChat({ backendUrl: 'https://chat.biatec.io', clientKey: 'example-web' })
// or mountBiatecChat('#chat-slot', options): the host element is appended to that element instead of <body>
chat.setPageContext('Docs · Getting started')
await chat.ask('What is Algorand?')
chat.unmount()
```

| Export | Does |
| --- | --- |
| `mountBiatecChat(options)` / `mountBiatecChat(container, options)` | Mounts the floating widget and returns a `BiatecChatHandle`: the host API of `useBiatecChat()` plus `host` (the `<div data-biatec-chat-host>`) and `unmount()`. One widget per page: a second call while a widget is mounted (by this build, the script tag or the Vue plugin) returns that widget, and different options or a different container are ignored with a console warning (`unmount()` first to change them). Throws on a missing `backendUrl` or `clientKey`, an unknown container selector, or a stray `[data-biatec-chat-host]` element no copy of the package mounted. Without a DOM (server rendering) it does nothing and returns a handle with `host: null` |
| `unmount()` | Removes the widget; a later `mountBiatecChat` mounts a fresh one |
| `useBiatecChat()` | The same host API object as the Vue entry |
| `autoMount(script)` | What the IIFE runs on its own tag; exported for tests and custom loaders |

Defaults differ from the plugin for non-Vue sites: `theme: 'host'` when `<html>` has a `data-theme` attribute
(Docusaurus), otherwise `auto` (prefers-color-scheme); a `host` theme without `hostDarkClass` or `hostDarkAttribute`
watches `data-theme=dark`; and `locale` comes from `<html lang>`. The widget is shared through a global symbol, so the
Vue plugin, the standalone module and a second copy of the script tag never mount two widgets. Importing the module has no side
effect without a DOM, so it is SSR-safe; still load it with a dynamic `import()` in the browser so the server bundle
does not carry it (see the Docusaurus client module in [integration-biatec-clamm.md](integration-biatec-clamm.md)).
`mode: 'inline'` has no use here (there is no `BiatecChatInline` outside Vue).

### Accessibility

The launcher is a keyboard-accessible button, the stylesheet honours `prefers-reduced-motion`, and the panel is a labelled dialog that
moves focus into the panel on open. Dialogs trap focus and return it on close, and streamed messages are announced through a live
region. Automated checks: `packages/chat-widget/test/i18n-a11y.test.ts`, `test/dex-host-a11y.test.ts` and axe on the demo site
(`apps/demo/test/demo.test.ts`); no manual screen-reader audit is recorded.

## 2. Choose how the backend is hosted

The session cookie is host-only (`__Host-biatec_session`, `Path=/`, no `Domain`) and must be first-party to your page. The widget
never relies on third-party cookies. That gives three modes:

| Mode | Backend host | Tier | Notes |
| --- | --- | --- | --- |
| Biatec sites | `chat.biatec.io`, pages on `web.biatec.io` and `dex.biatec.io` | Authenticated | Same-site but cross-origin: credentialed CORS with the exact origin echoed. The apex `biatec.io` is not an allowed origin |
| Third party | Your own subdomain, for example `chat.example-bank.com`, pointed at the Biatec Chat backend | Authenticated | The backend is multi-tenant by `Host` header. `backendUrl` must be same-site with your page. Path-based reverse proxying is not supported |
| Unregistered or cross-site host | Any backend's public endpoint | Anonymous only | `POST {backendUrl}/public/chat` with continuation tokens ([public-chat-contract.md](public-chat-contract.md)); the widget falls back to it when the cookie does not come back |

## 3. Register your host as a tenant (third-party mode)

The tenant onboarding API is built (`backend/Biatec.Chat.Api/Onboarding/`). All routes are cookie-less, rate limited per remote
IP and capped at 32 KiB bodies.

1. Ask for the DNS challenges for your host and origins:

   ```http
   POST /onboarding/challenges
   Content-Type: application/json

   {
     "host": "chat.example-bank.com",
     "signInPageUrl": "https://www.example-bank.com/login",
     "allowedOrigins": ["https://www.example-bank.com"],
     "clientKeys": ["example-web"]
   }
   ```

   The response lists the `TXT` records to publish (names start with `_biatec-chat-challenge.`, values with
   `biatec-chat-verification=`): one for the backend host and one for each origin that is not the host or a name beneath it.
   A sibling or apex name is never inferred from a subdomain proof.
2. Publish the records, point the CNAME of the host at the backend, then call `POST /onboarding/tenants` with the same body.
3. Every allowed origin must be on the same registrable domain as the backend host. Other domains are rejected with an explanation,
   because the cookie could never be first-party there.
4. Today `POST /onboarding/tenants` answers `202 pending-operator-approval` and stores nothing, because Biatec OIDC cannot register
   confidential clients dynamically yet. An operator adds the client to Biatec OIDC's static list, puts its secret in the secret
   store and calls `POST /onboarding/operator/tenants` (bearer operator key). The DNS proofs are re-checked first. Details and the
   fallback table are in [required-changes-biatec-oidc.md](required-changes-biatec-oidc.md).

TLS for tenant hostnames is not automated: the ingresses in `k8s/` cover only Biatec's own hosts (cert-manager, `letsencrypt-dns`), and
onboarding does not add a tenant host to them, so an operator adds the host and its certificate.

## 4. What the backend enforces for you

Integrators do not need to implement these, but should know them:

- **No tokens in the page.** The widget only ever holds the opaque cookie. Sign-in runs in a popup the backend controls.
- **CSRF.** Every state-changing request needs the `X-Biatec-Chat` header, an `Origin` in your allow-list and a registered
  `X-Biatec-Client-Key` (the `clientKey`). Credentialed CORS echoes the exact origin and never uses a wildcard.
- **Sessions.** 15 minutes idle, 8 hours absolute. The widget calls `GET /session` when the panel first opens and
  `POST /session/keepalive` while an action is pending.
- **Scopes.** Sign-in asks for `openid profile email sign`, because Biatec OIDC's sign endpoint needs the token's `email` and `sign`
  claims. If Biatec OIDC does not grant `sign`, it is requested when an action first needs it, through a second popup. Only `sign` and `manage-limits` can ever be requested that way.
- **Tier.** Anonymous visitors can learn about Biatec and Algorand but get no wallet tools. The backend rejects any wallet call from
  them, whatever the model says.

## 5. Checklist before going live

- [ ] Backend host is a subdomain on the same registrable domain as your pages.
- [ ] DNS TXT proofs published for the host and every origin; tenant row created (operator step today).
- [ ] `clientKey` listed on the tenant and passed in the plugin options.
- [ ] Your Content Security Policy allows `connect-src` and popup navigation to the backend host.
- [ ] You pass `pageContext` only with data you are happy to send to the backend and the model.

## Publishing to npm

`.github/workflows/publish-widget.yml` publishes the package automatically when a version bump of
`packages/chat-widget/package.json` is merged to `main` (then tags it `chat-widget-v<version>` and creates a GitHub
release), and also on a `chat-widget-v<version>` tag or a manual run with `dry_run` unticked, only for a commit on
`main` (other refs fail) and never for a version already on npm; it dry-runs on pull requests. It uses npm trusted publishing (GitHub OIDC, no secret); the
GitHub secret **`NPM_TOKEN`**, an environment secret of the **`npm`** environment of scholtz/biatec-chat, is only a
fallback until the trusted publisher is configured. The secret, who adds it, how to create the token, the one-time
setup (npm org `biatec`, first manual publish, trusted publisher) and how to cut a release are in
[packages/chat-widget/README.md, "Publishing"](../packages/chat-widget/README.md#publishing); the secret is also listed
in [cicd.md](cicd.md#secrets).
