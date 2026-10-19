// Seeded test users, clients and tokens. Everything here is public test data and protects nothing.
export interface SeedClient {
  id: string;
  secret: string;
  entityId: string;
  verified: boolean;
  displayName?: string;
  domain?: string;
  fingerprint?: string;
  allowManageLimits: boolean;
}

export interface SeedUser {
  sub: string;
  email: string;
  thresholdEur: number;
  /** Client ids the user set "always ask" for. */
  alwaysAsk: string[];
  /** Client ids the user has already used (rule 4 first-use). */
  usedClients: string[];
  limits: { per_transaction_eur: string; per_day_eur: string };
  /** Seeded active device kid, if any (its public key is the vector device key). */
  activeDevice: boolean;
}

export const clients: SeedClient[] = [
  { id: "biatec-chat", secret: "biatec-chat-secret", entityId: "https://chat.biatec.io", verified: true, displayName: "Biatec Chat", domain: "chat.biatec.io", allowManageLimits: true },
  { id: "bank-example", secret: "bank-example-secret", entityId: "https://bank.example", verified: true, displayName: "Example Bank", domain: "bank.example", allowManageLimits: false },
  { id: "unverified-app", secret: "unverified-app-secret", entityId: "client-3f9a1c", verified: false, fingerprint: "3F9A-1C77", allowManageLimits: false },
];

export const users: SeedUser[] = [
  // Has an active device and has used Biatec Chat before: only value above 1000 EUR needs the phone.
  { sub: "pw_alice", email: "alice@example.com", thresholdEur: 1000, alwaysAsk: [], usedClients: ["biatec-chat"], limits: { per_transaction_eur: "1000", per_day_eur: "3000" }, activeDevice: true },
  // Has an active device and set "always ask" for Biatec Chat.
  { sub: "pw_carol", email: "carol@example.com", thresholdEur: 1000, alwaysAsk: ["biatec-chat"], usedClients: ["biatec-chat"], limits: { per_transaction_eur: "1000", per_day_eur: "3000" }, activeDevice: true },
  // No device: cannot complete anything that needs phone approval (rule 8).
  { sub: "pw_bob", email: "bob@example.com", thresholdEur: 1000, alwaysAsk: [], usedClients: ["biatec-chat"], limits: { per_transaction_eur: "1000", per_day_eur: "3000" }, activeDevice: false },
];

/** Static bearer tokens. `login-*` tokens stand for a fresh full login (needed for device enrollment). */
export const staticTokens: Record<string, { sub: string; clientId: string; scope: string; kind: "access" | "login" }> = {
  "alice-chat-sign": { sub: "pw_alice", clientId: "biatec-chat", scope: "openid sign", kind: "access" },
  "alice-chat-limits": { sub: "pw_alice", clientId: "biatec-chat", scope: "openid sign manage-limits", kind: "access" },
  "alice-bank-sign": { sub: "pw_alice", clientId: "bank-example", scope: "openid sign", kind: "access" },
  "alice-unverified-sign": { sub: "pw_alice", clientId: "unverified-app", scope: "openid sign", kind: "access" },
  "carol-chat-sign": { sub: "pw_carol", clientId: "biatec-chat", scope: "openid sign", kind: "access" },
  "bob-chat-sign": { sub: "pw_bob", clientId: "biatec-chat", scope: "openid sign", kind: "access" },
  "alice-chat-nosign": { sub: "pw_alice", clientId: "biatec-chat", scope: "openid profile", kind: "access" },
  "login-alice": { sub: "pw_alice", clientId: "biatec-authenticator", scope: "openid", kind: "login" },
  "login-bob": { sub: "pw_bob", clientId: "biatec-authenticator", scope: "openid", kind: "login" },
};

