// Response shapes of docs/admin-dashboard.md ("HTTP API"). camelCase JSON, ISO-8601 timestamps.

export interface Impersonation {
  tenantId: string;
  sub: string;
  reason: string;
  startedAt: string;
  expiresAt: string;
}

export interface Me {
  email: string;
  tenantId: string;
  expiresAt: string;
  impersonation: Impersonation | null;
}

export interface StatsTotals {
  users: number;
  activeUsers7d: number;
  conversations: number;
  messages: number;
  liveSessions: number;
  previews: number;
  tenants: number;
}
export interface TierCount { tier: string; count: number }
export interface StatusCount { status: string; count: number }
export interface ModelTokens {
  modelId: string;
  messages: number;
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
}
export interface Tokens {
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
  byModel: ModelTokens[];
}
export interface DailyRow {
  date: string;
  newUsers: number;
  conversations: number;
  messages: number;
  inputTokens: number;
  outputTokens: number;
}
export interface TenantRow { id: string; host: string; users: number; conversations: number }
export interface Stats {
  generatedAt: string;
  days: number;
  tenant: string | null;
  totals: StatsTotals;
  sessionsByTier: TierCount[];
  previewsByStatus: StatusCount[];
  tokens: Tokens;
  daily: DailyRow[];
  tenants: TenantRow[];
}

export interface UserItem {
  tenantId: string;
  sub: string;
  firstSeenAt: string;
  lastSeenAt: string;
  signInCount: number;
  conversations: number;
  lastActivityAt: string | null;
}
export interface UsersPage {
  items: UserItem[];
  total: number;
  page: number;
  pageSize: number;
}

export type UserSort = "lastActivity" | "firstSeen" | "sub" | "conversations";
export type SortDir = "asc" | "desc";

export interface UserQuery {
  q?: string;
  tenant?: string;
  sort?: UserSort;
  dir?: SortDir;
  page?: number;
  pageSize?: number;
}

export interface UserDetailUser extends UserItem {
  messages: number;
  previews: number;
  activeSessions: number;
}
export interface ConversationMeta {
  id: string;
  createdAt: string;
  lastMessageAt: string | null;
  endedAt: string | null;
  messages: number;
  previews: number;
}
export interface UserDetail {
  user: UserDetailUser;
  conversations: ConversationMeta[];
}

export type MessageRole = "User" | "Assistant" | "Tool" | "System";
export interface TranscriptMessage {
  seq: number;
  role: MessageRole | string;
  content: string;
  createdAt: string;
  modelId: string | null;
  toolNames: string[];
}
export interface PreviewStep {
  seq: number;
  kind: string;
  status: string;
  network: string | null;
  txIds: string[];
}
export interface ActionPreview {
  id: string;
  capability: string;
  status: string;
  predictedValueEur: number | null;
  needsConsent: boolean;
  expiresAt: string | null;
  steps: PreviewStep[];
}
export interface Transcript {
  conversation: { id: string; createdAt: string; lastMessageAt: string | null; endedAt: string | null };
  messages: TranscriptMessage[];
  previews: ActionPreview[];
}

export interface ApiErrorBody { error?: string }
