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
  /** Hours since the client registered, when that is less than 24 (rule 4: the cooling-off of a new client). Default: long ago. */
  registeredHoursAgo?: number;
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
  /** Seeded active device kid, if any (its approval key is the vector device key, its presence key the vector presence key). */
  activeDevice: boolean;
  /** Client ids this user has paused (rule 10: the 24 hour pause that only the user lifts). */
  pausedClients?: string[];
  /** The user blocks clients that have not proven their domain (403 unproven_client_blocked). */
  blockUnprovenClients?: boolean;
}

export const clients: SeedClient[] = [
  { id: "biatec-chat", secret: "biatec-chat-secret", entityId: "https://chat.biatec.io", verified: true, displayName: "Biatec Chat", domain: "chat.biatec.io", allowManageLimits: true },
  { id: "bank-example", secret: "bank-example-secret", entityId: "https://bank.example", verified: true, displayName: "Example Bank", domain: "bank.example", allowManageLimits: false },
  // Registered one hour ago: for 24 hours after registering every request asks the phone, whatever the value (rule 4, cooling-off).
  { id: "new-app", secret: "new-app-secret", entityId: "https://new.example", verified: true, displayName: "New App", domain: "new.example", allowManageLimits: false, registeredHoursAgo: 1 },
  { id: "unverified-app", secret: "unverified-app-secret", entityId: "client-3f9a1c", verified: false, fingerprint: "3F9A-1C77", allowManageLimits: false },
];

export const users: SeedUser[] = [
  // Has an active device and has used Biatec Chat before: only value above 1000 EUR needs the phone.
  { sub: "pw_alice", email: "alice@example.com", thresholdEur: 1000, alwaysAsk: [], usedClients: ["biatec-chat", "new-app"], limits: { per_transaction_eur: "1000", per_day_eur: "3000" }, activeDevice: true },
  // Has an active device and set "always ask" for Biatec Chat.
  { sub: "pw_carol", email: "carol@example.com", thresholdEur: 1000, alwaysAsk: ["biatec-chat"], usedClients: ["biatec-chat"], limits: { per_transaction_eur: "1000", per_day_eur: "3000" }, activeDevice: true },
  // No device: cannot complete anything that needs phone approval (rule 8).
  { sub: "pw_bob", email: "bob@example.com", thresholdEur: 1000, alwaysAsk: [], usedClients: ["biatec-chat"], limits: { per_transaction_eur: "1000", per_day_eur: "3000" }, activeDevice: false },
  // Has an active device and paused Biatec Chat (rule 10): every signing call of that client is refused with 403 client_paused.
  { sub: "pw_dave", email: "dave@example.com", thresholdEur: 1000, alwaysAsk: [], usedClients: ["biatec-chat"], limits: { per_transaction_eur: "1000", per_day_eur: "3000" }, activeDevice: true, pausedClients: ["biatec-chat"] },
  // No device and has never signed with any client: inside the first day with Biatec Chat, so the reduced cap of 100 EUR applies (rule 4).
  { sub: "pw_erin", email: "erin@example.com", thresholdEur: 1000, alwaysAsk: [], usedClients: [], limits: { per_transaction_eur: "1000", per_day_eur: "3000" }, activeDevice: false },
  // Has an active device and raised the threshold to 5000 EUR (the user's own, no ceiling).
  { sub: "pw_frank", email: "frank@example.com", thresholdEur: 5000, alwaysAsk: [], usedClients: ["biatec-chat"], limits: { per_transaction_eur: "10000", per_day_eur: "30000" }, activeDevice: true },
  // Has an active device and blocks clients without a proven domain.
  { sub: "pw_gina", email: "gina@example.com", thresholdEur: 1000, alwaysAsk: [], usedClients: ["biatec-chat", "unverified-app"], limits: { per_transaction_eur: "1000", per_day_eur: "3000" }, activeDevice: true, blockUnprovenClients: true },
];

/** Static bearer tokens. `login-*` tokens stand for a fresh full login (needed for device enrollment). */
export const staticTokens: Record<string, { sub: string; clientId: string; scope: string; kind: "access" | "login" }> = {
  "alice-chat-sign": { sub: "pw_alice", clientId: "biatec-chat", scope: "openid sign", kind: "access" },
  "alice-chat-limits": { sub: "pw_alice", clientId: "biatec-chat", scope: "openid sign manage-limits", kind: "access" },
  "alice-bank-sign": { sub: "pw_alice", clientId: "bank-example", scope: "openid sign", kind: "access" },
  "gina-chat-sign": { sub: "pw_gina", clientId: "biatec-chat", scope: "openid sign", kind: "access" },
  "gina-unverified-sign": { sub: "pw_gina", clientId: "unverified-app", scope: "openid sign", kind: "access" },
  "alice-new-sign": { sub: "pw_alice", clientId: "new-app", scope: "openid sign", kind: "access" },
  "alice-unverified-sign": { sub: "pw_alice", clientId: "unverified-app", scope: "openid sign", kind: "access" },
  "carol-chat-sign": { sub: "pw_carol", clientId: "biatec-chat", scope: "openid sign", kind: "access" },
  "dave-chat-sign": { sub: "pw_dave", clientId: "biatec-chat", scope: "openid sign", kind: "access" },
  "erin-chat-sign": { sub: "pw_erin", clientId: "biatec-chat", scope: "openid sign", kind: "access" },
  "frank-chat-sign": { sub: "pw_frank", clientId: "biatec-chat", scope: "openid sign", kind: "access" },
  "bob-chat-sign": { sub: "pw_bob", clientId: "biatec-chat", scope: "openid sign", kind: "access" },
  "alice-chat-nosign": { sub: "pw_alice", clientId: "biatec-chat", scope: "openid profile", kind: "access" },
  "login-alice": { sub: "pw_alice", clientId: "biatec-authenticator", scope: "openid", kind: "login" },
  "login-bob": { sub: "pw_bob", clientId: "biatec-authenticator", scope: "openid", kind: "login" },
};

