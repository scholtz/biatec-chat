# Frequently asked questions

## Is Biatec AI free?

Yes, asking questions is free. Actions cost only blockchain network fees and, for swaps, the pool's trading fee, all shown in the preview.

## Do I need an account to use Biatec AI?

No. Anyone can ask questions and get market snapshots. To check balances or act on funds, sign in with Google or Microsoft through Biatec OIDC.

## Does Biatec hold my money?

No. Biatec is self-custody: your keys are encrypted in your own cloud storage through Biatec OIDC, and actions run from your own wallet. Biatec Chat holds no keys and no funds.

## Can the AI move my money by itself?

No. The AI can only propose. Every action needs your Confirm on a preview, signing stays within your spending limits, and larger actions need approval on your phone. The server enforces this whatever the AI says.

## What happens if I close the chat during an action?

The session stays alive while a preview, approval or signing is pending. Come back inside the countdown and continue. If the preview expired, ask again and confirm the fresh one.

## Why did my preview expire?

Algorand transactions are valid for about 45 minutes and Bitcoin previews for 30 minutes. Confirm is disabled under 10 minutes so there is time to approve and sign. Ask again for a fresh preview.

## Why was phone approval requested for a small amount?

Phone approval is also required for your first action through an app, during a new app's first 24 hours, when you chose "always ask", or when the chat's hourly safety cap (1500 EUR per hour by default) would be exceeded.

## My phone shows a request I didn't start. What do I do?

Deny it. Without the code shown in your chat, it can't be approved. After three denials in a day, you can pause that app for 24 hours.

## I lost my phone. Is my money at risk?

No. The phone holds no wallet keys. Enrol a new phone with your recovery code, or wait the 24-hour activation delay.

## Why does my address need ALGO before receiving USDC?

Algorand accounts need a minimum ALGO balance and must opt in to each asset they receive. Fund the address with some ALGO; the assistant adds the opt-in step.

## Can I send funds to someone else?

Yes. Transfers can go to any address, which is always shown in full on the preview. Blockchain transfers can't be reversed, so check it carefully.

## Can Biatec AI tell me what to invest in?

No. It explains products, risks and facts, but doesn't give investment advice, forecasts or signals.

## Which languages does Biatec AI speak?

English and Slovak.

## Which AI model does Biatec Chat use?

Anthropic Claude models through Amazon Bedrock in the EU (Frankfurt) region.

## Can I download what happened?

Yes. Each completed action has a Download evidence link, and signed-in users can export their sessions as a signed, independently verifiable bundle.

## Can I delete my conversations?

Signed-in users can ask for deletion; message contents are removed immediately while fingerprints, receipts and transaction IDs remain. Guest conversations are deleted automatically after 30 days.

## Can I add Biatec AI to my website?

Yes, with the `@biatec/chat-widget` npm package. Guest mode works anywhere; signed-in mode needs a Biatec Chat backend on a subdomain of your own domain. See "Embedding Biatec Chat on your website".

## Is Biatec a regulated crypto-asset service provider?

Biatec Chat is a self-custody interface: it holds no keys and no customer funds. Biatec AI can't give legal or regulatory advice; for licensing questions contact Biatec directly.
