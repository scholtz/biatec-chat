import type { SessionState } from "@biatec/chat-protocol";

/** What the status pill shows. The backend enforces the mode (T-065); the widget only reflects it. */
export type AgentModeDisplay = "analyze" | "prepare" | "paused";

/** Guest is always Analyze only. Signed in: the session's mode and pause flag, defaulting to Prepare for approval. */
export function agentModeOf(session: SessionState | null | undefined): AgentModeDisplay {
  if (!session || session.tier !== "Authenticated") return "analyze";
  if (session.paused) return "paused";
  return session.agentMode === "analyze" ? "analyze" : "prepare";
}

export const AGENT_MODE_LABEL: Record<AgentModeDisplay, string> = {
  analyze: "shell.modeAnalyze",
  prepare: "shell.modePrepare",
  paused: "shell.modePaused",
};

/** Short form of an address for the header: first 6 and last 4 characters. */
export function shortAddress(a: string | null | undefined): string {
  if (!a) return "";
  return a.length > 13 ? `${a.slice(0, 6)}…${a.slice(-4)}` : a;
}
