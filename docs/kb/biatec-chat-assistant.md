# Biatec Chat assistant (Biatec AI)

Biatec Chat is the AI assistant shown to users as Biatec AI. It appears as a green button in the bottom-right corner of Biatec Web and Biatec DEX, and websites can embed it through the `@biatec/chat-widget` npm package.

## What Biatec AI can do for a guest (not signed in)

- Explain Biatec products, Algorand, blockchain basics, decentralised exchanges, liquidity, Biatec Safe, fees and how to get started.
- Search the Biatec knowledge base and cite the page it used.
- Show a 24-hour market snapshot of an asset pair from public Biatec DEX data: price, 24-hour change, high, low and volume. It is a snapshot with a time stamp, not a live price, and it comes without forecasts or recommendations.
- Offer a Sign in button when a request needs an account. The assistant explains in general terms what the action would do, and never escalates silently.

A guest session never sees account data and never builds transactions. The server enforces this regardless of what the AI model says.

## What Biatec AI can do for a signed-in user

Everything a guest can do, plus:

- List your addresses and show balances, as a holdings card with an allocation bar.
- Transfer ALGO or Algorand Standard Assets such as USDC and EURS to your own other address or to anyone else. Bitcoin addresses and balances can be shown, but sending Bitcoin is not available yet.
- Swap assets on Biatec DEX.
- Add liquidity to or remove liquidity from a Biatec DEX pool.
- Create a Biatec Safe, propose payments from it, approve proposals and execute them.
- Show your Biatec OIDC spending limits, and lower them where the site allows it.

Every action is shown as a preview card first and needs your Confirm, and larger actions need phone approval.

## What Biatec AI will not do

Biatec AI does not give investment advice, price forecasts or trading signals, and does not promise returns. It does not create assets, bridge between blockchains, rekey accounts or act on addresses you don't control. It never asks for a seed phrase, password or private key. See "What Biatec AI will not do".

## Languages

Biatec AI speaks English and Slovak, following the language of the page. Other page languages fall back to English.

## Layout options

- Floating (default): a panel above the launcher button.
- Inline: the chat fills a column of the page, for example next to the Biatec DEX order form.
- Workspace: full screen, with the Activity timeline next to the conversation.

On screens narrower than 640 px an open panel is always full screen. The header shows Guest or your signed-in email, a sign-in or sign-out control, and expand, minimise and close buttons.

## The status pill and Activity timeline

Under the header a status pill shows the agent mode: Analyze only, Prepare for approval or Paused. Selecting it opens the permissions dialog "What may Biatec AI do?" (see "Agent modes and permissions"). The Activity toggle opens a timeline of this session's analyses, previews, confirmations, approvals, receipts and mode changes, newest first, with who did each ("You" or "Biatec AI").

## How Biatec AI answers

Answers stream in as they are written, with short status lines such as "Checking your USDC balance" or "Simulating transaction group". The assistant uses the knowledge base for facts about Biatec, and tool results for live data such as balances. Content from outside the user, such as web pages, token names or on-chain notes, is treated as data and can never change what the assistant is allowed to do. The AI runs on Anthropic Claude models through Amazon Bedrock in the EU (Frankfurt) region.

## Accessibility

The widget follows WCAG 2.1 AA: keyboard navigation with a visible focus ring, screen-reader announcements for streamed messages, large touch targets on mobile, contrast-checked light and dark themes, and reduced motion when the system asks for it.
