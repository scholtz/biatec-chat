# Consent rollout: from the backend's fallbacks to server enforcement

Status on 2026-11-14 (T-243): **nothing here has been switched.** The backend verifies every consent receipt itself (T-238, `Consent:VerifyReceipts`,
on by default). The settings below that move the decision to Biatec OIDC are prepared in the stage overlay at the values that keep today's behaviour, and
the real Biatec OIDC does not yet enforce consent (issues 25 to 28 of `ludovit-scholtz/AlgorandGoogleDriveAccount` are open; the state of each row is in
[required-changes-biatec-oidc.md](required-changes-biatec-oidc.md), "Status on Biatec OIDC"). Do not start part 2 until part 1 is true on stage and the
conformance run of part 1 is recorded. Production (`k8s/main`, `k8s/base`) is not touched by this procedure; enabling it is the owner's separate decision.

The specification is `PRODUCT-DESCRIPTION.md` and `packages/consent-protocol/spec/biatec-consent-protocol.md`; the architecture record is issue 23 of the
Biatec OIDC repository.

## 1. Prerequisites on Biatec OIDC (stage)

All of these are settings or deployments in the Biatec OIDC repository; this repository does not change them.

1. **Deployed.** The consent issues 24 to 28 (account key, client identity, group description and valuation, settings store and policy, step-up on the
   signing endpoint) and the device channel (29) are deployed on `stage.oidc.biatec.io`. Check each one's state before you continue; an open issue is a stop.
   The step-up on `POST /wallet/{network}/{address}/sign` is the one that matters most: without it nothing below can work.
2. **`Consent__Enforcement=AgentClients`** in the stage Biatec OIDC configuration. Clients marked `ConsentEnforced` are then subject to the policy; the
   other clients keep today's behaviour. (`Off` is the safe state and the rollback.)
3. **The Biatec Chat stage client is marked `ConsentEnforced`** in Biatec OIDC (the client whose id is `OIDC_CLIENT_ID` of the `Stage` GitHub environment).
4. **Its domain is proven.** The backend serves `GET /.well-known/biatec-client.json` on each tenant host (`Tenancy/ClientProofEndpoints.cs`; the body is
   `{"client_ids":["<the tenant's OidcClientId>"]}`). The client's `ClientUri` in Biatec OIDC must point at that host (`https://stage.chat.biatec.io`).
   Check from a shell: `curl -s https://stage.chat.biatec.io/.well-known/biatec-client.json`, and that the client id in it is the one registered at Biatec
   OIDC. Until Biatec OIDC has fetched it the phone says "Unproven application".
5. **Secrets present.** `oidc-client-secret` in `biatec-chat-secrets` is the stage client's secret (confidential client, the CIBA token grant needs it); the
   `Stage` environment secrets are unchanged otherwise. Nothing is written into a file or a CI variable dump for this.
6. **The backend verifies receipts.** `Consent:VerifyReceipts` is not set to `false` anywhere (a startup warning says so when it is). The backend then
   resumes a signing only with a receipt whose signature, attestation and binding it checked itself against what it asked for.
7. **A phone is enrolled.** The test person has an enrolled authenticator with an active device (24 hours old, or activated by another device). If the
   authenticator build should use the device channel, it was built with `--dart-define=DEVICE_CHANNEL=true` (T-244; default off, the app polls) and
   `CONSENT_SETTINGS_API=true` (the settings screens; row 20 to 22 of the required-changes doc). Without these the flow still works, only without the
   instant wake and the settings screens that steps 11 to 13 of the script use.
8. **Conformance kit against stage, recorded.** From your own shell (nothing goes in a file; the report redacts tokens and codes):

   ```
   export CONFORMANCE_OIDC_URL=https://stage.oidc.biatec.io CONFORMANCE_ISSUER=https://stage.oidc.biatec.io
   export CONFORMANCE_CLIENT_ID=<stage client id> CONFORMANCE_ADDRESS=<test wallet address on a test network> CONFORMANCE_NETWORK=algorand-testnet
   read -rs CONFORMANCE_CLIENT_SECRET; read -rs CONFORMANCE_USER_TOKEN; export CONFORMANCE_CLIENT_SECRET CONFORMANCE_USER_TOKEN
   pnpm --filter @biatec/consent-protocol conformance
   ```

   Read `packages/consent-protocol/conformance/report/run-<time>-stage.oidc.biatec.io.json` (git-ignored): the server URL, the commit of this repository and
   pass, fail and skip per case. Cases that need the mock's helpers (the ones that play the phone) are reported as skipped with the reason and prove nothing;
   the phone half is covered by the manual script of part 3. Attach the report to the task; failures are a stop.

## 2. Backend settings to change, in order, each with its rollback

The settings are keys of the `biatec-chat-backend` ConfigMap, in `k8s/stage/backend-config.patch.yaml`, where they are already written out at the
current values (so a change is a one-line, reviewed diff). Deploy through the normal stage workflow (`docs/cicd.md`); `kubectl rollout restart
deployment/biatec-chat-backend -n biatec-chat-stage` applies a ConfigMap change by hand. The backend reads configuration at start-up.

| Order | Setting (environment key) | Today | Switch to | What changes | Rollback |
| --- | --- | --- | --- | --- | --- |
| 1 | `Caps__OidcHonoursRequireConsent` (`Caps:OidcHonoursRequireConsent`) | `false` | `true` | A tenant soft cap or a Safe cap that would refuse now sends the user to the phone (the signing request carries `requireConsent`), because Biatec OIDC honours it. With `false` the cap is a hard stop ("can't be signed until the window frees up"). | Set `false` and restart. Pending approvals end as cancelled; nothing is signed that was not approved. |
| 2 | `Caps__NoDevice__Enabled` (`Caps:NoDevice:Enabled`) | `true` | `false` | The backend stops applying its own reduced daily cap (`Caps:NoDevice:DailyCapEur`, default as in code) and its refusal for users without an active device; Biatec OIDC enforces the rule itself (`403 consent_unavailable`, `daily_cap_exceeded`). | Set `true` and restart. The fallback applies again at once. |

Rules for the order:

- **Step 1 before step 2.** While the backend's own no-device rule is on, a user without a phone is refused by the backend before Biatec OIDC is asked; that
  is the safe state. Turn it off only when the stage script of part 3 has passed with step 1 on, in particular the "user with no device" scenario (scenario 8,
  which then must show Biatec OIDC's refusal and the enrollment hint instead of the backend's).
- Change one setting per deploy and run the whole script (or at least scenarios 1 to 3 and 8) after each.
- `Consent__VerifyReceipts` stays at its default `true` throughout. It is not part of the rollout; setting it `false` only diagnoses a broken deployment and logs a warning.
- Nothing in this repository sets `Consent__Enforcement`; that is Biatec OIDC's switch (part 1).

## 3. Stage test script

Who: one person who has a stage chat session signed in with a wallet on a test network (or a mainnet account with a few cents, if the stage
Biatec OIDC has no test-network price table: the card then says the value is unknown and the phone is asked), and an enrolled phone with the authenticator
in the foreground. Thresholds: the default is 1000 EUR; "above the threshold" below means a transfer valued above it.

Expected results are given for the **widget**, the **phone** and the **evidence export** (the "Download evidence" link of the widget, `GET /session/evidence`;
check the signed bundle with `packages/evidence-verifier`). A "receipt verified" means the bundle holds a `consent_receipt` event and no
`consent_receipt_rejected` or `consent_denied_unverified` event for the step.

| # | Scenario | Widget | Phone | Evidence export |
| --- | --- | --- | --- | --- |
| 1 | A swap under the threshold | Preview, confirm, signed and sent; no wait for the phone | Nothing | `signing_requested` with `requireConsent: false`, `signing_outcome` signed; no consent receipt |
| 2 | A transfer above the threshold | The card says the phone must approve and shows a 6-digit code | A request with the transfers, the client's proven domain `stage.chat.biatec.io`; type the code, approve | The widget says signed; `consent_receipt` (decision approve) verified, then `signing_outcome` signed; the receipt's `group_hash` is the previewed group's |
| 3 | Deny | Shows the user declined; nothing signed | Deny on the phone | A verified `consent_receipt` with decision deny and the step cancelled; no signing event |
| 4 | Three wrong codes | After the third the request is closed, the widget says the approval failed; nothing signed | Request locked after the third wrong code | `consent_receipt` with decision `code_failed` verified (or `consent_denied_unverified` if it could not be fetched, which is a defect to report); step cancelled |
| 5 | Let it expire (do nothing for the request's lifetime, 5 minutes) | The widget says it expired; nothing signed | The request disappears or says expired | Step ended as expired; no signing event; elevated token never used |
| 6 | Approve, then change the group (only possible if your stage build offers a way to alter the group after approval; otherwise record "not run": the conformance kit proves it with `consent_hash_mismatch` only against the mock) | Refused: nothing signed, the message names a changed request | Already approved | No `signing_outcome` signed; a refusal recorded (Biatec OIDC answers `403 consent_hash_mismatch`); the consent stays usable only for the approved group |
| 7 | Two tabs confirm the same preview | One of them signs; the other is told the action was already confirmed or is in progress | One request only | Exactly one `signing_outcome` signed for the step (one signature) |
| 8 | A user with no device and a transfer above the threshold (use a second test user without an enrolled phone) | A clear refusal with the enrollment hint ("set up the authenticator"); nothing signed | Nothing | `signing_refused` with the no-device reason (the backend's while `Caps__NoDevice__Enabled=true`; once it is `false` Biatec OIDC's `403 consent_unavailable` with reason `no_active_device`, which the backend maps to the same hint) |
| 9 | First use of the client under the threshold (a user who never used this client) | The phone is asked once | The card says "New service" and shows the domain `stage.chat.biatec.io` (not "Unproven application") | A verified `consent_receipt`; later actions under the threshold sign without the phone |
| 10 | Raise the threshold on the phone to 5000, repeat the transfer above 1000 | Signed without waiting for the phone | The change itself needs the approval key (biometric); no request for the transfer | `signing_outcome` signed, `requireConsent: false`; no consent receipt for it |
| 11 | A swap on an application the user never trusted (a Safe or pool call) | The card says the value is unknown, phone asked | The request names the untrusted application; tick "Don't ask again" and approve | Verified `consent_receipt` for it |
| 12 | The same swap again, under the threshold | Signed without the phone | Nothing | `requireConsent: false`; no receipt |
| 13 | Lower the threshold to 0 | Every action asks (repeat scenario 1: now the phone is asked) | A request for each action | A verified receipt per action |

After the script, restore the phone's threshold to its default. Record the outcome of every row (pass, fail, notes) in the task's Hand-off. A row that
differs from the expectation is a stop for the next switch. Scenarios that cannot be created on a given stage (for example a user with no device) are
recorded as "not run" with the reason, never as passed.

## 4. Watch for 24 hours after each switch, and the emergency rollback

Watch (the security events and metrics of issue 35 of the Biatec OIDC repository, once deployed; until then the backend's own logs and evidence):

- **Biatec OIDC security events** (logger category `Biatec.Security`, issue 35; the names are the planned ones and are checked against the runbook
  `BiatecOIDC/docs/consent-runbook.md` once it exists): `policy_decided` (how often the phone is asked against allowed), `consent_receipt` by outcome
  (approve, deny, `code_failed`, `wrong_code`), `consent_refused` (hash mismatch, wrong client, expired, replay), `consent_token_misuse`,
  `consent_reservation_stranded`, `device_proof_refused`, `client_paused`, `client_domain_proof_changed`, `consent_notify` failures. Any
  `consent_reservation_stranded` or `consent_token_misuse` is an incident; a spike of `consent_refused` or `device_proof_refused` is an incident; a
  handful of expired or denied requests is normal. Until issue 35 is deployed, only the evidence and logs of this backend are available: say so in the record.
- **Backend:** the `consent_receipt_rejected` and `consent_denied_unverified` evidence events and the `signing_refused` events with a `reason`; the startup
  warning "Consent:VerifyReceipts is false"; HTTP 429 and 403 answers of the signing path in the logs; 5xx rate of `/chat/confirm`.
- **Users:** the number of confirms that end as refused because of a cap or a missing device compared with the day before; support messages that say a
  user "cannot sign" (the hint to enroll the phone is the intended answer for users without a device).
- **Phone side:** the approval rate (approved against denied and expired); a falling approval rate after step 1 means the phone request is not reaching
  people (device channel off, or the push not arriving): check `DEVICE_CHANNEL` of the authenticator build and Biatec OIDC's hub.
- Check at 1 hour, 6 hours and 24 hours, and record the three checks in the task.

Emergency rollback (in this order; each step is independent, stop when the problem is gone):

1. In `k8s/stage/backend-config.patch.yaml` set `Caps__NoDevice__Enabled: "true"` and `Caps__OidcHonoursRequireConsent: "false"`, deploy (or edit the
   ConfigMap and `kubectl rollout restart deployment/biatec-chat-backend -n biatec-chat-stage`). The backend is back at the fallbacks of today: no user
   without a device signs anything that needs the phone, and a cap is a hard stop.
2. If Biatec OIDC itself misbehaves, set `Consent__Enforcement=Off` there (Biatec OIDC owners). The client is then treated as before; the backend, with step 1
   done, does not depend on it.
3. Pause the client for everyone by unmarking `ConsentEnforced` in Biatec OIDC, if a single client is the problem.
4. Do not set `Consent__VerifyReceipts=false` as a remedy: it removes the backend's own check of what the phone approved. It exists only to diagnose.
5. Record what happened in the task and open an issue; a rollback ends the rollout until the cause is understood.

An action that was approved on the phone but not yet signed when a rollback happens ends as cancelled (the consent is spent once or never used); the user
confirms again after the rollback.
