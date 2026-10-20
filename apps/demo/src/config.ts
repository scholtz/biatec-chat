// Build-time settings (vite.config.ts fills the defaults). Neither is a secret: the backend URL is public and the
// client key only labels this host for the tenant (backend CsrfMiddleware); the browser never holds a token.
export const BACKEND_URL = import.meta.env.VITE_BIATEC_CHAT_URL;
export const CLIENT_KEY = import.meta.env.VITE_BIATEC_CHAT_CLIENT_KEY;

/** Where the "read the docs" links point. */
export const REPO_URL = "https://github.com/scholtz/biatec-chat";
export const docUrl = (path: string) => `${REPO_URL}/blob/main/${path}`;
