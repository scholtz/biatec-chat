# x402 and agent payments

## What is x402?

x402 is an open way for a website or API to ask for payment over HTTP: the server answers "402 Payment Required" with what it accepts, the client pays, and the server delivers the content. It lets software agents pay each other for data or services in small amounts, without accounts or card details.

## How Biatec uses x402

Algo Safe includes an x402 facilitator that verifies and settles payments on Algorand, and an x402 client. Biatec Chat can use them in two ways:

- **Biatec AI as a buyer**: the assistant can pay for an x402-protected resource on your behalf with a USDC transfer, within your spending limits and, above the threshold, with phone approval. The preview shows the transfer decoded from the actual transaction, never just the seller's advertised price, so a seller can't charge more than shown or split a payment to avoid phone approval.
- **Biatec Chat as a seller**: premium capabilities, for example a deep portfolio analysis, can be offered as x402-protected endpoints that other agents pay for.

## Why it matters

This is the step from "a person chats with an assistant" to "agents pay agents under rules a person sets". Biatec Safe's signer groups and spending limits were designed for it.

## Availability

x402 payments in Biatec Chat are built but switched off behind a feature flag. They have been tested only against a test facilitator, and only USDC is supported, with a cap of 5 USDC per payment.
