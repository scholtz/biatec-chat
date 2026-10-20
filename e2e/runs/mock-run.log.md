# Demo e2e run log (mock Biatec OIDC, always-ask fallback)

Run at 2026-10-20T02:13:34.269Z. Mode: mock OIDC (T-047), no real network, no AWS.


Step 1 | Alice: "Create a Biatec Safe for me and Bob with 2-of-2 approvals." -> safe.create
  step-up: auth_req_id ciba_379ec5c96c9d2475362dd8425dab3a2b, widget shows binding code 998344
  phone card: 0.300000 ALGO (0.06 EUR), application 777, hash NEWHEQFH...
  phone approved (prompt 1 for Alice)
  transaction ids: 5PKSFP53F6...

Step 2 | Alice: "Fund the safe with 2000 EURS and 2000 USDC. (ALGO for the minimum balance first)" -> wallet.transfer (extra prompt)
  step-up: auth_req_id ciba_02633d7beea8081158ad6f47b2e7288a, widget shows binding code 799819
  phone card: 2.000000 ALGO (0.40 EUR), hash Q5ENEKPJ...
  phone approved (prompt 2 for Alice)
  transaction ids: 6I2YEE6IE2...

Step 2 | Alice: "Yes, create the opt-in proposal." -> safe.propose (extra prompt)
  step-up: auth_req_id ciba_72b484c9de86d01b1f7c2b3f8034c2e0, widget shows binding code 329036
  phone card: 0.000000 ALGO (0.00 EUR), application 777, hash 7Y7YCJ6C...
  phone approved (prompt 3 for Alice)
  transaction ids: PMSFKAQC25...

Step 2 | Alice: "Approve it as the first signer." -> safe.approve (extra prompt)
  step-up: auth_req_id ciba_0835fd9309a6a380e80959c30a755a53, widget shows binding code 274168
  phone card: 0.000000 ALGO (0.00 EUR), application 777, hash 7Y7YCJ6C...
  phone approved (prompt 4 for Alice)
  transaction ids: XOLVFK6PHR...

Step 3 | Bob: "Show me pending proposals in my safes. -> approve the setup proposal" -> safe.approve
  step-up: auth_req_id ciba_b06f75bdfe7adb3498eca256da095927, widget shows binding code 297162
  phone card: 0.000000 ALGO (0.00 EUR), application 777, hash Y47AUSEH...
  phone approved (prompt 1 for Bob)
  transaction ids: URZF4JMWUR...

Step 3 | Bob: "Execute it." -> safe.execute (extra prompt)
  step-up: auth_req_id ciba_ec8047faf78917d59c4ba7132fd1fb67, widget shows binding code 952988
  phone card: 0.000000 ALGO (0.00 EUR), application 777, hash Y47AUSEH...
  phone approved (prompt 2 for Bob)
  transaction ids: 2OGHK4W253...

Step 4 | Alice: "Now fund the safe." -> wallet.transfer
  step-up: auth_req_id ciba_778520105b5f69cd33916be305ab218a, widget shows binding code 312182
  phone card: 4000.000000 EURS (4000.00 EUR), hash DV5PSQN5...
  wrong code typed: refused, 2 attempts left
  phone approved (prompt 5 for Alice)
  bait and switch refused: group_hash_mismatch, consent not spent
  transaction ids: 7AIASOPY2Y..., MPZX5ASSGX...

Step 5 | Alice: "Top up the safe with ALGO for the proposal box." -> wallet.transfer (extra prompt)
  step-up: auth_req_id ciba_3d99c33f3b16ee171a39e98fab287ec0, widget shows binding code 160073
  phone card: 1.000000 ALGO (0.20 EUR), hash PSD6WM4A...
  phone approved (prompt 6 for Alice)
  transaction ids: OWXFTBLNUH...

Step 5 | Alice: "Propose adding all the safe's EURS and USDC as liquidity to the Biatec DEX EURS/USDC pool." -> safe.propose (extra prompt)
  step-up: auth_req_id ciba_7135b34623523adfc771140f192c1eb5, widget shows binding code 836466
  phone card: 0.100000 ALGO (0.02 EUR), application 777, hash DKTW5IFK...
  phone approved (prompt 7 for Alice)
  transaction ids: 3HZKTA3HMD...

Step 5 | Alice: "Approve it as the first of two signers." -> safe.approve
  step-up: auth_req_id ciba_b4cfd62bfac9bffc2349c82258ae02e7, widget shows binding code 317843
  phone card: 4000.000000 EURS (4000.00 EUR), application 600, hash EEFROMTR...
  phone approved (prompt 8 for Alice)
  transaction ids: RGL3EVLVRM...

Step 6 | Bob: "Show me pending proposals. -> approve Alice's liquidity proposal" -> safe.approve
  step-up: auth_req_id ciba_86aaf7f53f60bd65eda7678556468ec1, widget shows binding code 298258
  phone card: 4000.000000 EURS (4000.00 EUR), application 600, hash BGDFAYT6...
  phone approved (prompt 3 for Bob)
  transaction ids: MHYIK5WIO2...

Step 7 | Alice: "Execute the proposal." -> safe.execute (extra prompt)
  step-up: auth_req_id ciba_69087383b932eaf46fe6a3166ff67c19, widget shows binding code 043877
  phone card: 0.000000 ALGO (0.00 EUR), application 600, hash LLYD3FXV...
  phone approved (prompt 9 for Alice)
  transaction ids: KHJSROTKUL...

Step 8 | evidence exported for both sessions (55 and 19 entries)

Phone prompts: Alice 9, Bob 3 (7 of 12 signing calls are the "extra" prompts that disappear under testnet valuation).

## Verification
- 12 signing calls over steps 1-7 all ended signed
- phone prompts: Alice 9, Bob 3 (7 of them are the documented extra prompts)
- wrong code refused, swapped group refused with group_hash_mismatch, consent single use
- Alice: bundle of 55 entries verifies against the JWKS
- Alice: receipts, group hashes and transaction ids line up with the run
- Bob: bundle of 19 entries verifies against the JWKS
- Bob: receipts, group hashes and transaction ids line up with the run
- tampered payload, dropped entry and a foreign JWKS are rejected
