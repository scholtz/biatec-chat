import { createI18n } from "vue-i18n";
import ui from "./en/ui.json";
import cards from "./en/cards.json";
import chat from "./en/chat.json";
import agent from "./en/agent.json";
import activity from "./en/activity.json";

/** One catalog file per feature (ui, shell, chat, cards, agent, data) so parallel UI tasks never edit the same file. */
export const en = { ...ui, ...cards, ...chat, ...agent, ...activity };

export const i18n = createI18n({
  legacy: false,
  locale: "en",
  fallbackLocale: "en",
  messages: { en },
  missingWarn: false,
  fallbackWarn: false,
});

export const t = (key: string, named?: Record<string, unknown>): string =>
  named ? i18n.global.t(key, named) : i18n.global.t(key);

/** Switches the widget language; unknown locales keep falling back to English. */
export function setLocale(locale: string): void {
  i18n.global.locale.value = locale.split("-")[0]! as never;
}
