import type { AgentMode, AgentSettings } from "@biatec/chat-protocol";
import { CLIENT_KEY_HEADER, CSRF_HEADER, type TransportConfig } from "../../transport/types";

export const AGENT_PATHS = {
  agent: "/session/agent",
  /** Cancel decision on a preview (same body as confirm: the group hash the user saw). */
  cancel: (previewId: string) => `/chat/previews/${encodeURIComponent(previewId)}/cancel`,
} as const;

/** Typed refusals from confirm (and the chat stream) while the agent is restricted (T-065). */
export type RefusalCode = "agent_paused" | "agent_analyze_only";

/** Reads a refusal code from a confirm response (`status`) or an error body / stream event (`error`, `code`). */
export function refusalCode(body: unknown): RefusalCode | null {
  if (!body || typeof body !== "object") return null;
  const b = body as Record<string, unknown>;
  for (const v of [b.status, b.error, b.code]) if (v === "agent_paused" || v === "agent_analyze_only") return v;
  return null;
}

export class AgentApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string | null,
  ) {
    super(code ?? `HTTP ${status}`);
    this.name = "AgentApiError";
  }
}

export interface AgentApi {
  /** PUT /session/agent; the response is the state the server now enforces. */
  setAgent(mode: AgentMode, paused: boolean): Promise<AgentSettings>;
  /** True when the backend accepted the cancel. */
  cancelPreview(previewId: string, groupHash: string): Promise<boolean>;
}

/** Calls the backend with the HttpOnly session cookie and the CSRF headers; it never sees a token. */
export class AgentClient implements AgentApi {
  private readonly f: typeof fetch;
  constructor(private readonly cfg: TransportConfig) {
    this.f = cfg.fetch ?? ((...a) => fetch(...a));
  }

  private init(method: string, body: unknown): RequestInit {
    return {
      method,
      credentials: "include",
      headers: { "Content-Type": "application/json", [CSRF_HEADER]: "1", [CLIENT_KEY_HEADER]: this.cfg.clientKey },
      body: JSON.stringify(body),
    };
  }

  async setAgent(mode: AgentMode, paused: boolean): Promise<AgentSettings> {
    const res = await this.f(this.cfg.backendUrl + AGENT_PATHS.agent, this.init("PUT", { mode, paused }));
    const j = (await res.json().catch(() => null)) as Record<string, unknown> | null;
    if (!res.ok) throw new AgentApiError(res.status, typeof j?.error === "string" ? j.error : null);
    if ((j?.agentMode !== "analyze" && j?.agentMode !== "prepare") || typeof j.paused !== "boolean")
      throw new AgentApiError(res.status, "bad_response");
    return { agentMode: j.agentMode, paused: j.paused };
  }

  async cancelPreview(previewId: string, groupHash: string): Promise<boolean> {
    try {
      const res = await this.f(this.cfg.backendUrl + AGENT_PATHS.cancel(previewId), this.init("POST", { groupHash }));
      return res.ok;
    } catch {
      return false;
    }
  }
}
