/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Backend the widget talks to; vite.config.ts defaults it (local backend in dev, stage in a build). */
  readonly VITE_BIATEC_CHAT_URL: string;
  /** Client key registered for this origin on the tenant (a label, not a secret). */
  readonly VITE_BIATEC_CHAT_CLIENT_KEY: string;
  /** Optional PrimeVue 5 licence key for dex.html; without it PrimeVue shows its licence banner. */
  readonly PRIMEVUE_LICENSE?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
