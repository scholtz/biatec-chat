import type {
  ApiErrorBody, ConversationMeta, Impersonation, Me, Stats, Transcript, UserDetail, UserQuery, UsersPage,
} from "./types";

export class ApiError extends Error {
  readonly status: number;
  constructor(status: number, message: string) {
    super(message);
    this.name = "ApiError";
    this.status = status;
  }
}

let onUnauthorized: () => void = () => {};
/** The session store registers here so any 401 returns the app to the signed-out screen. */
export function setUnauthorizedHandler(fn: () => void) {
  onUnauthorized = fn;
}

function fallbackMessage(status: number): string {
  switch (status) {
    case 400: return "The request was not accepted.";
    case 401: return "You are signed out.";
    case 403: return "The request was refused.";
    case 404: return "Not found.";
    case 409: return "That impersonation has already ended.";
    case 429: return "Too many requests. Wait a moment and retry.";
    default: return status >= 500 ? "The server had a problem. Retry in a moment." : `Request failed (${status}).`;
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { Accept: "application/json" };
  const init: RequestInit = { method, credentials: "same-origin", headers };
  if (method !== "GET") {
    headers["X-Biatec-Admin"] = "1";
    headers["Content-Type"] = "application/json";
    if (body !== undefined) init.body = JSON.stringify(body);
  }
  let res: Response;
  try {
    res = await fetch(path, init);
  } catch {
    throw new ApiError(0, "Cannot reach the server. Check your connection and retry.");
  }
  if (res.status === 401) {
    onUnauthorized();
    throw new ApiError(401, fallbackMessage(401));
  }
  if (!res.ok) {
    let msg = "";
    try {
      const j = (await res.json()) as ApiErrorBody;
      if (j && typeof j.error === "string") msg = j.error;
    } catch { /* not JSON */ }
    throw new ApiError(res.status, msg || fallbackMessage(res.status));
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export function buildUsersQuery(q: UserQuery): string {
  const p = new URLSearchParams();
  if (q.q?.trim()) p.set("q", q.q.trim());
  if (q.tenant) p.set("tenant", q.tenant);
  p.set("sort", q.sort ?? "lastActivity");
  p.set("dir", q.dir ?? "desc");
  p.set("page", String(q.page ?? 1));
  p.set("pageSize", String(q.pageSize ?? 25));
  return p.toString();
}

/** `/view/conversations` returns `{ user, conversations }`; a bare array or `{ items }` is tolerated. */
export function normalizeConversations(raw: unknown): ConversationMeta[] {
  if (Array.isArray(raw)) return raw as ConversationMeta[];
  const o = raw as { conversations?: unknown; items?: unknown } | null;
  if (o && Array.isArray(o.conversations)) return o.conversations as ConversationMeta[];
  if (o && Array.isArray(o.items)) return o.items as ConversationMeta[];
  return [];
}

export const api = {
  me: () => request<Me>("GET", "api/me"),
  logout: () => request<void>("POST", "api/logout", {}),
  tenants: () => request<{ tenants: { id: string; host: string }[] }>("GET", "api/tenants"),
  stats: (days: number, tenant?: string | null) => {
    const p = new URLSearchParams({ days: String(days) });
    if (tenant) p.set("tenant", tenant);
    return request<Stats>("GET", `api/stats?${p}`);
  },
  users: (q: UserQuery) => request<UsersPage>("GET", `api/users?${buildUsersQuery(q)}`),
  user: (tenant: string, sub: string) =>
    request<UserDetail>("GET", `api/user?${new URLSearchParams({ tenant, sub })}`),
  startImpersonation: (tenantId: string, sub: string, reason: string) =>
    request<Impersonation>("POST", "api/impersonation", { tenantId, sub, reason }),
  stopImpersonation: () => request<void>("DELETE", "api/impersonation"),
  viewConversations: async () =>
    normalizeConversations(await request<unknown>("GET", "api/view/conversations")),
  viewMessages: (id: string) =>
    request<Transcript>("GET", `api/view/conversations/${encodeURIComponent(id)}/messages`),
};
