# Phone approval with Biatec Authenticator

Biatec Authenticator is a mobile app for iOS and Android, comparable in role to Microsoft Authenticator. For high-value actions it shows exactly what you are approving and lets you approve or deny with a key that never leaves your phone.

## When is phone approval needed?

Biatec OIDC decides, not the chat. Phone approval is needed when:

- the action is valued above your approval threshold, 1000 EUR by default;
- you turned on "always ask" for everything, or for a particular app;
- it is your first action through an app, or the app was registered less than 24 hours ago (cooling-off);
- the chat's own safety cap asks for extra approval (see "Spending limits and safety caps").

The preview card predicts whether approval will be needed, but Biatec OIDC has the final word. The chat can ask for more approval, never less.

## How an approval works

1. You confirm the preview in the chat. The widget shows "Waiting for approval on your phone", a countdown and a short code.
2. Your phone gets a notification. It carries only a request number, no amounts.
3. Biatec Authenticator fetches the request directly from Biatec OIDC and shows a card built from the transaction itself: the verified name of the requesting app, amounts, assets and destination.
4. Check that the details match the chat. Type the code shown in the widget, then approve with your fingerprint or face.
5. The app signs a receipt with its device key, and the action proceeds with exactly the approved details.

An approval is valid for 5 minutes and for exactly one transaction.

## Why do I have to type a code?

The code (number matching) is shown only in the chat, never on the phone. Typing it proves the request on your phone is the one you started. A request you didn't start has no code to type, so you can't approve a fraudulent request by reflex. You have three attempts; the third wrong code denies the request.

## Denying a request

Deny cancels the action in the chat immediately and produces a signed denial receipt. Denials never punish you. If you deny three requests from the same app in a day, the authenticator offers to pause that app on that site for 24 hours.

## How do I know the request is really from my bank?

The authenticator shows an app's name, logo and domain only when its identity is verified through OpenID Federation, a chain of trust back to a recognised authority. Otherwise it shows "Unverified application" with a short fingerprint and a warning. You can block unverified apps entirely. Biatec Chat itself is always verified.

## Enrolling a phone

Enrolling starts with a fresh sign-in, including your Google or Microsoft multi-factor step. If you already have an active phone, approving from it activates the new one immediately. Your very first phone, or a phone added during recovery, becomes active after 24 hours, and every enrolled phone and your email receive a cancel link. After a phone becomes active you get a one-time recovery code; keep it safe, it activates a replacement phone immediately.

## What if I lose my phone?

The phone holds no wallet keys, no cloud-storage access and no long-lived tokens, only a device key. Losing it cannot drain your account. Enrol a new phone with your recovery code, or wait the 24-hour delay.

## What if I have no phone enrolled?

You can still use everything below the threshold, with a reduced cap (default 100 EUR per day) for new apps. Actions above the threshold can't be completed until a phone is active; the widget explains this and offers enrolment. The threshold is not lowered for users without a phone.

## Changing my approval settings

You can lower the threshold, turn on "always ask" or set per-app thresholds, but not raise it above the deployment's ceiling. Tightening applies immediately. Loosening (raising a threshold, turning off "always ask", lifting a pause) must itself be approved on an active phone, so someone who only has your Google or Microsoft login can't switch off phone approval.

## Availability

The approval flow and the authenticator app are built and tested against a test version of Biatec OIDC. The consent endpoints in the live Biatec OIDC are not available yet, so phone approval of above-threshold actions isn't live in production yet.
