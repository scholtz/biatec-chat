# Agent modes and permissions

The status pill under the chat header shows what Biatec AI may currently do. Selecting it opens the dialog "What may Biatec AI do?".

## The modes

- **Analyze only**: answers, explanations, balances and market information. Fund-moving actions are hidden from the AI and refused by the server. Guests are always in this mode.
- **Prepare for approval** (the default once signed in): the assistant may prepare action previews. Every action still needs your Confirm in the widget and, above the threshold, approval on your phone.
- **Rule-based automation**: shown as not available yet. Version 1 requires a confirmation for every action.
- **Paused**: behaves like Analyze only and also disables Confirm on previews already open, until you resume.

## Changing the mode

Lowering the mode, pausing and revoking take effect immediately. Raising the mode or resuming needs a second step with an explicit "I understand" checkbox. Every change is recorded in the session's evidence.

## Revoke all access

Revoke all access cancels every open preview and signs you out.

## Spending limits in the dialog

The dialog also shows your Biatec OIDC spending limits and, on sites allowed to manage them, lets you lower them. See "Spending limits and safety caps".

## Enforced by the server

The mode belongs to your session on the backend and is checked on every tool call, exactly like the guest and signed-in tiers. The AI cannot switch modes by itself, and text in a web page or a token name cannot change the mode.
