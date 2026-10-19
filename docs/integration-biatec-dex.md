# Embedding Biatec AI in Biatec DEX

How the chat widget (`@biatec/chat-widget`, shown to users as **Biatec AI**) goes into the Biatec DEX frontend,
[`scholtz/BiatecDEX`](https://github.com/scholtz/BiatecDEX). The specification is
[PRODUCT-DESCRIPTION.md](../PRODUCT-DESCRIPTION.md); the general integration guide is
[integration-widget.md](integration-widget.md); the look is in [design/README.md](design/README.md).

This repository never edits BiatecDEX. This file is the hand-off: the changes listed under "Host changes" are made in
the BiatecDEX repository by its maintainers, against the widget API this repo provides.

**Status:** planned. The host API (`theme: 'host'`, `mode`, `useBiatecChat()`), Shadow DOM isolation and the
Biatec AI look are tracked by tasks T-062 to T-068 on the [kanban board](../kanban/BOARD.md). The BiatecDEX-like
playground page (T-068) is how this repo checks the integration without the real DEX.

## What BiatecDEX looks like to the widget

Read from `scholtz/BiatecDEX` `main` on 2026-10-19:

| Aspect | BiatecDEX | What the widget does |
| --- | --- | --- |
| Framework | Vue 3, Vite, Pinia, vue-router, PrimeVue 5 (Aura preset, prefix `p`), Tailwind 4 with `tailwindcss-primeui`, primeicons | Mounts its own Vue app into a Shadow DOM root, so Tailwind preflight and PrimeVue styles cannot reach it |
| Dark mode | `useTheme()` toggles the class `p-dark` on `<html>`; dark is the default; stored in `localStorage['biatec-theme']` | `theme: 'host'` watches that class with a `MutationObserver` and switches `data-theme` live |
| Tokens | `src/assets/app.css`: `--brand #2DD4A7` (dark) / `#0EA371` (light), `--page-bg #070B14`, frosted `--surface-card`, `--radius .85rem`, `--radius-lg 1.15rem` | The Biatec AI tokens were derived from these values, so no override is needed. Widget tokens live inside the shadow root and never collide with the DEX's `--radius-lg` or `--shadow-lg` |
| Font | Stack starting with `Inter` (Inter not loaded; Roboto loaded from Google Fonts) | Declares the same stack; never loads fonts itself |
| i18n | `vue-i18n`, locales en, sk, pl, hu, it, ru, zh, ko, de, es; `getCurrentLocale()` and `setLocale()` in `src/i18n` | Ships en and sk; other locales fall back to English. `setLocale()` follows the DEX language switch |
| Wallet | `@txnlab/use-wallet-vue` and the ARC-76 `algorand-authentication-component-vue` session | **Not used by the widget.** Biatec AI signs in with Biatec OIDC in its own popup and signs through the backend. The two sessions are independent; the widget never reads the DEX wallet |
| Overlays | PrimeVue dialogs, toasts and overlays use automatic z-index from 1100 up | The floating widget defaults to `z-index: 1000`, below PrimeVue overlays, configurable with `--bc-z` |

## Host changes in BiatecDEX

### 1. Register the plugin (`src/main.ts`)

After `app.use(PrimeVue, …)`:

```ts
import { BiatecChat } from '@biatec/chat-widget'
import { getCurrentLocale } from '@/i18n'

app.use(BiatecChat, {
  backendUrl: 'https://chat.biatec.io',
  clientKey: 'biatec-dex',     // must be listed on the Biatec tenant
  locale: getCurrentLocale(),
  theme: 'host',               // follows .p-dark on <html>
  mode: 'floating',
})
```

No stylesheet import is needed: the styles are injected into the shadow root.

### 2. Keep language in sync (`src/i18n/index.ts`)

```ts
import { useBiatecChat } from '@biatec/chat-widget'

export const setLocale = async (locale: SupportedLocale) => {
  // existing code …
  useBiatecChat().setLocale(locale)   // unknown locales fall back to English inside the widget
}
```

### 3. Page context (trading and liquidity routes)

Pass what the user is looking at, so suggestions and answers fit the page. It is untrusted data to the backend and is
sent only after the user opens the panel:

```ts
useBiatecChat().setPageContext(`Biatec DEX · ${network} · pair ${base}/${quote} · pool app ${poolAppId}`)
```

Clear it (`setPageContext(undefined)`) when leaving those routes.

### 4. "Ask Biatec AI" entry points

A ghost link under the order card (`src/components/TradingComponents/MarketOrder.vue`) and the add-liquidity form
(`src/components/LiquidityComponents/AddLiquidity.vue`), using the Biatec AI chip at 16 px next to the label:

```ts
useBiatecChat().ask(`Swap ${amount} ${quote} to ${base} on Biatec DEX`)
useBiatecChat().ask(`Add liquidity to the Biatec DEX ${base}/${quote} pool`)
```

`ask` opens the panel and sends the prompt as a visible user message. Nothing is executed: the assistant answers with
an action preview the user still has to review and confirm (and, if not signed in, a sign-in card).

### 5. Optional inline mode

On wide trading layouts the widget can sit in a column next to the order card instead of floating:

```vue
<BiatecChatInline class="w-full" />   <!-- minmax(360px, 420px) column; same session as the floating widget -->
```

Only one instance renders at a time: while the inline panel is mounted the floating launcher hides.

### 6. Content Security Policy and hosting

- `connect-src https://chat.biatec.io`, and popups to `https://chat.biatec.io` and `https://oidc.biatec.io` allowed.
- `dex.biatec.io` (and any staging origin) is in the Biatec tenant's `allowedOrigins`, and `biatec-dex` in its
  `clientKeys`. Without that the widget falls back to the anonymous public transport (see integration-widget.md).

## Checks before shipping in BiatecDEX

- [ ] Toggle light/dark in the DEX header: the widget follows without reload.
- [ ] Switch the DEX language to Slovak, then to German: the widget shows Slovak, then English.
- [ ] Open a PrimeVue dialog while the widget is open: the dialog is on top and the widget's focus trap releases.
- [ ] Mobile width (< 640 px): the open widget is full screen, the launcher does not cover the DEX bottom controls.
- [ ] "Ask Biatec AI" on the order card opens the panel with the prompt, and no action runs without Confirm.
- [ ] Network tab: no OAuth token or code reaches DEX scripts; the DEX wallet session is untouched by Biatec AI sign-in.
