import { reactive } from "vue";
import { api, ApiError, setUnauthorizedHandler } from "./api";
import type { Impersonation, Me } from "./types";

export const session = reactive<{
  status: "loading" | "in" | "out" | "error";
  me: Me | null;
  error: string;
}>({ status: "loading", me: null, error: "" });

function signedOut() {
  session.status = "out";
  session.me = null;
}

setUnauthorizedHandler(signedOut);

/** Loads /me. 401 means signed out (handled by the unauthorized hook, no retry loop). */
export async function refreshSession(): Promise<void> {
  try {
    session.me = await api.me();
    session.status = "in";
    session.error = "";
  } catch (e) {
    if (e instanceof ApiError && e.status === 401) {
      signedOut();
    } else if (session.status === "in") {
      // A refresh of a running session (the impersonation countdown) that hits a blip keeps the shell; the next tick tries again.
    } else {
      session.status = "error";
      session.error = e instanceof Error ? e.message : "Unexpected error.";
    }
  }
}

export function setImpersonation(imp: Impersonation | null) {
  if (session.me) session.me = { ...session.me, impersonation: imp };
}

export async function signOut(): Promise<void> {
  try {
    await api.logout();
  } catch { /* the cookie may already be gone */ }
  signedOut();
}
