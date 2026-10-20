# Biatec Chat: likely jury questions

Prepared from the hackers guide (`hackersGuide.md` in the event workspace, outside this repository; judging criteria, jury list) and
https://thefintechers.com/partners. Framing for the pitch only; [PRODUCT-DESCRIPTION.md](PRODUCT-DESCRIPTION.md) is the
specification. See also [presentation_pitch_prep.md](presentation_pitch_prep.md).

## Format and scoring

- **4 min live demo + 3 min jury Q&A**, on-site presenters only.
- Open with one line on what existed before: Biatec OIDC, MCP, CLAMM and Algo Safe existed; the chat, consent protocol
  and evidence log are new.
- Weights: Impact 25%, Technical execution 25%, Innovation 20% ("not a thin GPT wrapper"), Responsible AI and compliance
  15%, Business viability and pitch 15%. Each judge scores 1-5 per criterion right after the demo.
- Judges score only work after the `hackathon-start` marker, and a technical judge reviews the top teams' commit history.
  The first commit in this repo (`3781f25`, Mon 19 Oct 12:39) is after kickoff, so the whole repo was built during the
  event. **Before 13:30, add the `hackathon-start` tag and a "Built during the hackathon" section to the README.**

## Questions by juror

### ČNB, Jan Bridzik (ICT risk, AML/CFT supervision): probably the toughest

| Question | Answer |
|---|---|
| Are you a CASP under MiCA? Who does KYC/AML on these transfers? | Self-custody interface: we hold no keys and no funds. Licensing analysis is roadmap; don't claim compliance. |
| How do you stop the AI being used for laundering or sanctions evasion? | Limits, phone approval and an evidence trail. Sanctions screening isn't in scope yet; say so. |
| GDPR, retention, right to erasure? | Data is minimised to the OIDC `sub` plus addresses. Transcripts are kept 90 days, and the evidence for fund-moving actions 7 years. Erasure removes the message payloads, and the hash chain still verifies (spec, Data handling). |
| How does this map to DORA ICT risk? | Bedrock stays in an EU region (`eu-central-1`), a circuit breaker caps AI spend, and keys sit outside the chat backend. Be honest that there's no formal DORA mapping. |

### Mastercard, Ondřej Kováč (cyber security)

| Question | Answer |
|---|---|
| Walk me through prompt injection via a token name or on-chain note. | Outside content is wrapped as untrusted data. Tier and scope checks run on the server, and the model can only propose actions. |
| What if your backend is fully compromised? | Keys stay in Biatec OIDC, above-threshold actions still need the phone, and a changed transaction fails the hash check. Remaining risk: spending below the threshold, within the user's limits and the tenant soft cap. |
| How is this different from SCA / 3-D Secure? | PSD2 dynamic linking, with the approval tied to the AI's exact proposal by the group hash. Code-matching on the phone stops reflexive approval. |
| Stolen phone or SIM swap? | No keys on the phone, approvals are device-bound with FIDO2, and new devices wait 24 hours. |

### AWS, Vyasaraj Kulkarni, and Deloitte, Tomáš Bubeník (technology)

| Question | Answer |
|---|---|
| Why Bedrock, and which models? | Opus 4.5 for signed-in users, Sonnet 4.5 for anonymous ones, all in `eu-central-1`. |
| Why not AgentCore? | Considered (`docs/AWS-AGENTCORE-GUIDE.md`), but our own server-side gating needed the orchestrator in our BFF. Check that this matches the team's actual reason. |
| How do you control cost? | Prompt caching on the static system prompt, budgets per tenant and globally, and a circuit breaker that sends anonymous traffic to cached answers. |
| How does it scale and run in production? | ASP.NET BFF, Redis, Postgres, multi-tenant, with the tenant resolved from `Host`. |
| How did you build this much in 25 hours? | An agent-driven kanban (the guide encourages disclosing AI coding help). Show the board. |

### Komerční banka, Martin Gerneš (data platform), and Partners Banka, Jiří Raška (CTO)

| Question | Answer |
|---|---|
| Could my bank plug in tomorrow? What does integration cost? | White-label per tenant, using standards banks already know (CIBA, FAPI 2.0, RAR). A conformance suite exists (`packages/consent-protocol/conformance`), run against our mock. |
| Why your authenticator and not our own app? | A shared trusted device, OpenID Federation so brands can't be impersonated, and network effects. Expect pushback; banks guard their app real estate. |
| Fiat or only crypto? | Today it's Algorand assets and stablecoins. The consent protocol itself doesn't care which rail it runs on. |
| What data do you keep? Can it be exported to our audit systems? | Evidence export plus offline-verifiable receipts. |

### Elevator Ventures, Annalisa Mortellaro, and Deloitte, Kamil Olszynski (business)

| Question | Answer |
|---|---|
| Who pays, and how much? | Tenant licence, DEX and Safe fees, x402 analysis, and later consent-network fees. Have a number ready: there's no pricing in the spec yet. |
| Go-to-market: retail or B2B first? | Be specific. Biatec DEX users would be the first tenant. |
| Who are your competitors? | Bank chatbots and agent wallets (comparison table in the pitch prep). |
| What's live versus roadmap? | Be precise; see below. |

## Traps: questions that test honesty

| Question | Answer |
|---|---|
| Is that a real phone approval or a mock? | The consent side runs against **mock OIDC** until the real endpoints ship (`docs/hackathon-demo.md`). Say so out loud in the demo. |
| Can I see x402 working? | Not live. `x402.pay` is built but off behind a feature flag, and tested only against a mock facilitator; no testnet payment has been recorded (`docs/x402-pay.md`). |
| Is this just a GPT wrapper? | No. The model only proposes. Hash binding, approval enforced outside the chat, and an evidence chain anchored on-chain are all deterministic code. |
| Show me the tampered transaction being refused. | If you'll demo it, rehearse it. The group-hash mismatch refusal against the mock is buildable today. |

## Open points

- **Which track did we pick?** Judges will ask "how does this fit your track?". Likely Track 1 (agentic payments) or
  Track 3 (securing the agentic bank).
- **Status:** every board task is merged; the status table in `docs/hackathon-demo.md` says what each script step runs
  against. The phone approval works against the mock OIDC only, and the authenticator has not run on a device.
