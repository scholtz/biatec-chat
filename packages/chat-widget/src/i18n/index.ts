import { createI18n } from "vue-i18n";
import ui from "./en/ui.json";
import cards from "./en/cards.json";
import chat from "./en/chat.json";
import agent from "./en/agent.json";
import activity from "./en/activity.json";
import safe from "./en/safe.json";
import skUi from "./sk/ui.json";
import skCards from "./sk/cards.json";
import skChat from "./sk/chat.json";
import skAgent from "./sk/agent.json";
import skActivity from "./sk/activity.json";
import skSafe from "./sk/safe.json";
import exportView from "./en/export.json";
import skExportView from "./sk/export.json";

/** One catalog file per feature (ui, shell, chat, cards, agent, data) so parallel UI tasks never edit the same file. */
export const en = { ...ui, ...cards, ...chat, ...agent, ...activity, ...safe, ...exportView };
/** Slovak catalog with exactly the keys of `en` (a test enforces the parity). */
export const sk = { ...skUi, ...skCards, ...skChat, ...skAgent, ...skActivity, ...skSafe, ...skExportView };

export const i18n = createI18n({
  legacy: false,
  locale: "en",
  fallbackLocale: "en",
  messages: { en, sk },
  missingWarn: false,
  fallbackWarn: false,
});

export const t = (key: string, named?: Record<string, unknown>): string =>
  named ? i18n.global.t(key, named) : i18n.global.t(key);

/** Switches the widget language; unknown locales keep falling back to English. */
export function setLocale(locale: string): void {
  i18n.global.locale.value = locale.split("-")[0]! as never;
}

/** The catalog locale that resolves for a requested one: the language itself when it has messages, else English. */
export function resolvedLocale(locale: string): string {
  const base = locale.split("-")[0]!;
  return (i18n.global.availableLocales as string[]).includes(base) ? base : "en";
}
