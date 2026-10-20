/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Backend URL for the DEX page (docs/integration-biatec-dex.md, step 1); the playground config defaults it. */
  readonly VITE_BIATEC_CHAT_URL?: string;
  /** Client key for the DEX page; the playground config defaults it to the local dev tenant's "dev". */
  readonly VITE_BIATEC_CHAT_CLIENT_KEY?: string;
  /** Optional PrimeVue 5 licence key; without it PrimeVue shows its licence banner. */
  readonly PRIMEVUE_LICENSE?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
