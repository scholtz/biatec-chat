import type { SessionState, SignInStatus } from "@biatec/chat-protocol";
import { CLIENT_KEY_HEADER, CSRF_HEADER, type TransportConfig } from "../../transport/types";

/** Backend paths of the sign-in popup flow (spec: Sign-in handoff). */
export const AUTH_PATHS = {
  session: "/session",
  popup: "/auth/popup",
  elevatePopup: "/auth/elevate/popup",
  popupStatus: (id: string) => `/auth/popup/${encodeURIComponent(id)}`,
  logout: "/auth/logout",
} as const;

export interface PopupStart {
  authRequestId: string;
  loginUrl: string;
}

/**
 * Talks to the backend's session and popup endpoints with the HttpOnly cookie. It only ever sees the session state
 * and a popup status: tokens, OAuth parameters and codes never reach the widget.
 */
export class AuthClient {
  private readonly f: typeof fetch;
  constructor(private readonly cfg: TransportConfig) {
    this.f = cfg.fetch ?? ((...a) => fetch(...a));
  }

  private headers(): Record<string, string> {
    return { "Content-Type": "application/json", [CSRF_HEADER]: "1", [CLIENT_KEY_HEADER]: this.cfg.clientKey };
  }

  async session(): Promise<SessionState | null> {
    try {
      const res = await this.f(this.cfg.backendUrl + AUTH_PATHS.session, { credentials: "include", headers: this.headers() });
      return res.ok ? ((await res.json()) as SessionState) : null;
    } catch {
      return null;
    }
  }

  private async start(path: string, body?: unknown): Promise<PopupStart | null> {
    try {
      const res = await this.f(this.cfg.backendUrl + path, {
        method: "POST",
        credentials: "include",
        headers: this.headers(),
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (!res.ok) return null;
      const j = (await res.json()) as { authRequestId?: unknown; loginUrl?: unknown };
      if (typeof j.authRequestId !== "string" || typeof j.loginUrl !== "string") return null;
      // Only a web URL may be opened: never javascript: or data: URLs.
      if (!/^https?:\/\//i.test(j.loginUrl)) return null;
      return { authRequestId: j.authRequestId, loginUrl: j.loginUrl };
    } catch {
      return null;
    }
  }

  startLogin(): Promise<PopupStart | null> {
    return this.start(AUTH_PATHS.popup);
  }

  /** Incremental re-authorization for a scope the session lacks (for example `sign`). */
  startElevate(scope: string): Promise<PopupStart | null> {
    return this.start(AUTH_PATHS.elevatePopup, { scope });
  }

  /** "Failed" also covers an unknown or expired request; a network error returns null so the caller keeps polling. */
  async status(id: string): Promise<SignInStatus | null> {
    try {
      const res = await this.f(this.cfg.backendUrl + AUTH_PATHS.popupStatus(id), { credentials: "include", headers: this.headers() });
      if (res.status === 401 || res.status === 404) return "Failed";
      if (!res.ok) return null;
      return ((await res.json()) as { status?: SignInStatus }).status ?? null;
    } catch {
      return null;
    }
  }

  async logout(): Promise<boolean> {
    try {
      const res = await this.f(this.cfg.backendUrl + AUTH_PATHS.logout, { credentials: "include", headers: this.headers() });
      return res.ok;
    } catch {
      return false;
    }
  }
}
