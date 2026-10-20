# Biatec Safe (shared accounts with approvals)

A Biatec Safe, built on the open-source Algo Safe, is a shared smart account on Algorand. A group of signers controls it with an M-of-N rule, for example 2-of-2 or 2-of-3: a payment from the safe runs only after M members approved it. It suits teams, families, small organisations and AI agents that should spend only under human-set rules.

## The flow: propose, approve, execute

1. **Propose.** A member proposes a transaction: a payment, an asset transfer, or a Biatec DEX add-liquidity transaction group. The proposal stores the exact transaction.
2. **Approve.** Other members approve it from their own sessions, on their own devices. Each approval names the exact version of the proposal, so if the proposal changes, earlier approvals no longer count.
3. **Execute.** Once enough members approved, anyone can execute it, and the safe runs the stored transaction as one atomic group.

## Using a safe in chat

Signed-in users can ask, for example:

- "Create a Biatec Safe for my team with 2-of-3 approvals." The assistant collects the members' addresses and shows a preview.
- "Fund the safe with 100 USDC."
- "Show me pending proposals in my safes." You see each proposal's payload, approval progress and expiry.
- "Approve it." and "Execute the proposal."

## Funding a new safe

A new safe needs ALGO for its minimum balance and must opt in to every asset it will hold. The assistant explains this and prepares the steps: an ALGO payment from you to the safe, then a proposal for the safe to opt in to the assets, which members approve and execute, then the asset transfers. Each step has its own preview.

## Changing members and rules

Adding or changing a signer group is itself a proposal that follows propose, approve, execute, so administrative changes need the same approvals as payments.

## Phone approval for safes

Approving a proposal is what authorises the safe's spending, so Biatec OIDC values your approval at the proposal's value, read from the blockchain. If that value is above your threshold, your phone shows the proposal's amounts and destination before you can approve. Executing an already-approved proposal is not valued again, because every approver already consented to the exact transaction. Creating a safe and creating a proposal move no funds and are valued at zero; funding is a separate transfer.

## Limits on safes

A safe's funds are governed by its own on-chain signer-group limits, not by members' personal spending limits. Executions are limited to 10 per hour per user on each site.

## x402 agent payments

Algo Safe includes support for x402 payments, which let software agents pay each other under limits a human sets. See "x402 and agent payments".
