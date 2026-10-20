# Spending limits and safety caps

Several independent layers limit how much can leave your wallet. The most important ones are enforced by Biatec OIDC, outside the chat, so even a compromised chat backend or website cannot bypass them.

## Your Biatec OIDC spending limits

You can set daily, weekly and monthly spending limits on your Biatec OIDC wallet. Biatec OIDC checks every signing request against them, whichever app asks. Ask the assistant "What are my current spending limits?" to see them and how much headroom remains; every preview also shows which limit an action counts against.

- Lowering a limit takes effect immediately, so you can always reduce your exposure, even without a phone.
- Raising a limit is treated like a high-value action and needs approval in Biatec Authenticator.
- Changing limits from the chat is available only on sites whose app Biatec has allowlisted for limit management.

Spending limits cover your own addresses. Funds inside a Biatec Safe are governed by the safe's own signer-group rules and approvals.

## The phone approval threshold

Any single action valued above 1000 EUR by default needs approval on your phone. Values are computed by Biatec OIDC from the transaction itself, at the same rates used for spending limits, never from what the chat or a seller claims. See "Phone approval with Biatec Authenticator".

## The chat's hourly safety cap

On top of your limits, Biatec Chat tracks your below-threshold spending per site over a rolling hour. The default cap is 1500 EUR per hour. Once an action would exceed it, the chat asks Biatec OIDC to require phone approval for it. This stops many small payments from adding up unnoticed.

## Safe execution rate cap

Executing approved Biatec Safe proposals is limited to 10 per hour per user on each site.

## What if a website I use is compromised?

The widget runs inside the host page, so a compromised page could try to drive it. The real protections are outside the page: your Biatec OIDC spending limits, the phone approval threshold with code matching, the hourly safety cap, and the option to require phone approval for every action from that site. The remaining risk is below-threshold spending within those caps, the same as any site you sign into.

## Pause and revoke

In the permissions dialog you can pause Biatec AI, which blocks all actions and disables Confirm on open previews, or revoke all access, which cancels open previews and signs you out. Both apply immediately.
