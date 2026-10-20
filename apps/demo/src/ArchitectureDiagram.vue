<script setup lang="ts">
// The docs/architecture.md system diagram, reduced to what a visitor needs: three tiers and what crosses each line.
// Plain HTML (no image, no external script), so it follows the theme, reflows at 375 px and reads well in a screen reader.
const SERVICES = [
  { name: "Amazon Bedrock", what: "Claude 4.5 in eu-central-1, guardrails on model and tool output" },
  { name: "Biatec MCP", what: "Balances and transaction building, called with the user's token" },
  { name: "Builder sidecar", what: "Biatec DEX (CLAMM) and algo-safe groups" },
  { name: "Biatec OIDC", what: "Sign-in, spending limits, signing; phone approval above the threshold" },
];
</script>

<template>
  <figure class="arch">
    <ol class="arch-tiers">
      <li class="arch-tier">
        <p class="arch-title">Your browser</p>
        <p class="arch-what">This page and the widget in a shadow root. Holds only an HttpOnly session cookie, never a token or key.</p>
      </li>
      <li class="arch-link" aria-hidden="true"><span>HTTPS · cookie · CSRF header · allowed origin</span></li>
      <li class="arch-tier arch-core">
        <p class="arch-title">Biatec Chat backend (BFF)</p>
        <p class="arch-what">
          Session and token vault, tier and agent-mode gate, action previews bound to a group hash, Confirm, hash-chained
          evidence anchored on Algorand.
        </p>
      </li>
      <li class="arch-link" aria-hidden="true"><span>server to server</span></li>
      <li class="arch-tier">
        <ul class="arch-services">
          <li v-for="s in SERVICES" :key="s.name">
            <p class="arch-title">{{ s.name }}</p>
            <p class="arch-what">{{ s.what }}</p>
          </li>
        </ul>
      </li>
      <li class="arch-link" aria-hidden="true"><span>signed group</span></li>
      <li class="arch-tier">
        <p class="arch-title">Algorand testnet</p>
        <p class="arch-what">Transactions, and the evidence anchor every ten minutes.</p>
      </li>
    </ol>
    <figcaption>
      Browser → backend → Bedrock, Biatec MCP, builder sidecar and Biatec OIDC → Algorand. Full diagram in
      docs/architecture.md.
    </figcaption>
  </figure>
</template>
