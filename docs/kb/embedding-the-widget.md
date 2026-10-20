# Embedding Biatec Chat on your website

Site owners can add Biatec AI to any website with the `@biatec/chat-widget` npm package.

## Two builds

- **Vue 3 plugin**: `npm i @biatec/chat-widget`, then `app.use(BiatecChat, { backendUrl, clientKey })`.
- **Standalone build**: for React, Docusaurus or plain HTML sites; it bundles its own Vue.

The widget renders in its own Shadow DOM, so your page's CSS and the widget's CSS don't interfere. You can restyle it with CSS custom properties.

## Main options

- `backendUrl`: your Biatec Chat backend address.
- `clientKey`: a label for your site (not a secret).
- `locale`: `en` or `sk`.
- `theme`: `auto`, `light`, `dark`, or `host` to follow your site's dark mode.
- `mode`: `floating` (launcher in the corner), `inline` (inside a column of your page) or `workspace` (full screen).
- `suggestions`: the default sample prompts or your own.
- `pageContext`: optional text describing the page, such as the pool being shown. Nothing is read from your page unless you set it, and the assistant treats it as untrusted data.

Sites can also open the chat with a prepared question, for example a "Ask Biatec AI" link next to a swap form.

## Guest mode works anywhere

Any website can run the guest experience against a Biatec Chat backend's public endpoint, without a cookie. Guests can ask questions and get market snapshots.

## Signed-in mode needs your own domain

To let users sign in and act, the backend must be same-site with your page: you run it on a subdomain of your own domain (for example chat.example.com for www.example.com) as a registered tenant. This is because the widget never relies on third-party cookies. Biatec's own sites use chat.biatec.io.

## Biatec's own sites

Biatec AI is embedded in Biatec Web, Biatec DEX and the BiatecCLAMM documentation site.

## Accessibility and languages

The widget meets WCAG 2.1 AA and ships in English and Slovak, following your site's language switch.
