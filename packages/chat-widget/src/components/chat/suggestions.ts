import type { SessionTier } from "@biatec/chat-protocol";
import { t } from "../../i18n";

export type PromptIcon = "book" | "layers" | "shield" | "sparkles" | "swap" | "droplets" | "wallet" | "gauge";

export interface SamplePrompt {
  label: string;
  prompt: string;
  minTier: SessionTier;
  icon: PromptIcon;
}

/** The 2 x 2 grid shows at most four cards. */
export const MAX_PROMPTS = 4;

const DEFAULTS: { key: string; minTier: SessionTier; icon: PromptIcon }[] = [
  { key: "Blockchain", minTier: "Anonymous", icon: "book" },
  { key: "Dex", minTier: "Anonymous", icon: "layers" },
  { key: "Safe", minTier: "Anonymous", icon: "shield" },
  { key: "Capabilities", minTier: "Anonymous", icon: "sparkles" },
  { key: "Transfer", minTier: "Authenticated", icon: "wallet" },
  { key: "Liquidity", minTier: "Authenticated", icon: "droplets" },
  { key: "Swap", minTier: "Authenticated", icon: "swap" },
  { key: "Limits", minTier: "Authenticated", icon: "gauge" },
];

/**
 * The spec's sample prompts, four per audience for the 2 x 2 grid. Guests see what works without an account;
 * a signed-in session sees the wallet prompts. Texts live in the English catalog (chat.prompt<Key>Label/Text).
 */
export function defaultPrompts(): SamplePrompt[] {
  return DEFAULTS.map((d) => ({
    label: t(`chat.prompt${d.key}Label`),
    prompt: t(`chat.prompt${d.key}Text`),
    minTier: d.minTier,
    icon: d.icon,
  }));
}

function custom(list: string[]): SamplePrompt[] {
  return list.slice(0, MAX_PROMPTS).map((p) => ({ label: p, prompt: p, minTier: "Anonymous" as const, icon: "sparkles" as const }));
}

/**
 * Prompts for the tier. A host-supplied list replaces the defaults, is shown to every tier and is capped at four.
 * With the option 'default', the tenant's own prompts (from the session) win when set, else the built-in ones.
 */
export function promptsFor(tier: SessionTier, option: "default" | string[], tenant?: readonly string[] | null): SamplePrompt[] {
  if (Array.isArray(option)) return custom(option);
  if (tenant && tenant.length > 0) return custom([...tenant]);
  const want: SessionTier = tier === "Authenticated" ? "Authenticated" : "Anonymous";
  return defaultPrompts().filter((p) => p.minTier === want);
}
