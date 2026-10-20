# Signing in and sessions

## How to sign in

Select Sign in in the chat. A popup opens the Biatec OIDC login, where you sign in with your Google or Microsoft account and review what Biatec Chat asks for. When the popup closes, the chat continues where you were. The page is never reloaded, so the conversation is not lost.

## Do I need a new account?

No. Biatec OIDC uses your existing Google or Microsoft account. The first sign-in creates your self-custody wallet: Algorand, EVM and Bitcoin addresses whose keys are encrypted and stored in your own cloud storage.

## Why there is a second popup the first time I act

Signing in lets the assistant read your addresses and balances and prepare previews. Signing a transaction needs an extra permission called the `sign` scope. The first time an action needs a signature in a session, the same kind of popup asks you to grant it. You grant it once per session, and signing always stays within your spending limits.

## No login tokens in the browser

The browser never receives your access, refresh or ID token. The page only holds an opaque session cookie that scripts cannot read. The Biatec Chat backend keeps the tokens encrypted and deletes them when the session ends. So a malicious script on a web page has no token to steal.

## How long a session lasts

A session ends after 15 minutes without activity or 8 hours after sign-in, whichever comes first. While a preview is open, an approval is pending or a transaction is being signed, the widget keeps the session alive, so it cannot expire in the middle of an approval. The widget warns you when less than 20 minutes remain and offers to finish a pending multi-step action first. After 8 hours you sign in again.

## Signing out and revoking access

Sign out from the chat header. In the permissions dialog, Revoke all access cancels every open preview and signs you out at once. Logging out deletes the session's tokens from the backend.

## Why can't I sign in on some websites?

Signed-in use works on Biatec's own sites and on websites registered with Biatec Chat under their own domain. On other websites the assistant runs in guest mode only. When sign-in isn't available there, the assistant says so and links to the site's sign-in page (Biatec Web for Biatec), where you can continue signed in.
