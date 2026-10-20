// Stand-in for BiatecDEX `src/i18n/index.ts`: vue-i18n, a locale stored in localStorage, `getCurrentLocale()` and
// `setLocale()`. The DEX ships ten locales; this page has three, enough to show a supported, a not-yet-shipped and an
// unsupported widget language. The `setLocale` body below is step 2 of docs/integration-biatec-dex.md.
import { createI18n } from "vue-i18n";
import { useBiatecChat } from '@biatec/chat-widget'

export type SupportedLocale = "en" | "sk" | "de";
export const SUPPORTED_LOCALES: readonly SupportedLocale[] = ["en", "sk", "de"];
const STORAGE_KEY = "biatec-dex-playground-locale";

const messages = {
  en: {
    nav: { trade: "Trade", liquidity: "Liquidity", portfolio: "Portfolio" },
    header: { theme: "Toggle light or dark", language: "Language", wide: "Wide layout" },
    pair: { last: "Last price", high: "24h high", low: "24h low", volume: "24h volume" },
    order: {
      title: "Market order",
      buy: "Buy",
      sell: "Sell",
      amount: "Amount",
      spend: "You spend",
      receive: "You receive (estimate)",
      submit: "Review order",
      ask: "Ask Biatec AI about this order",
      settings: "Order settings",
    },
    dialog: {
      title: "Order settings",
      body: "This is a PrimeVue Dialog. It must render above the Biatec AI panel, and Tab must stay inside it.",
      slippage: "Max slippage",
      close: "Close",
      save: "Save",
    },
    toast: {
      saved: "Settings saved",
      busy: "Biatec AI is still answering; your question was not sent.",
      unavailable: "Biatec AI is not available on this page.",
      empty: "Enter an amount first.",
    },
  },
  sk: {
    nav: { trade: "Obchod", liquidity: "Likvidita", portfolio: "Portfólio" },
    header: { theme: "Prepnúť svetlý alebo tmavý režim", language: "Jazyk", wide: "Široké rozloženie" },
    pair: { last: "Posledná cena", high: "24h maximum", low: "24h minimum", volume: "24h objem" },
    order: {
      title: "Trhový príkaz",
      buy: "Kúpiť",
      sell: "Predať",
      amount: "Množstvo",
      spend: "Zaplatíte",
      receive: "Dostanete (odhad)",
      submit: "Skontrolovať príkaz",
      ask: "Opýtať sa Biatec AI na tento príkaz",
      settings: "Nastavenia príkazu",
    },
    dialog: {
      title: "Nastavenia príkazu",
      body: "Toto je PrimeVue Dialog. Musí byť nad panelom Biatec AI a Tab musí zostať v ňom.",
      slippage: "Max. sklz",
      close: "Zavrieť",
      save: "Uložiť",
    },
    toast: {
      saved: "Nastavenia uložené",
      busy: "Biatec AI ešte odpovedá; otázka nebola odoslaná.",
      unavailable: "Biatec AI nie je na tejto stránke dostupný.",
      empty: "Najprv zadajte množstvo.",
    },
  },
  de: {
    nav: { trade: "Handel", liquidity: "Liquidität", portfolio: "Portfolio" },
    header: { theme: "Hell oder dunkel", language: "Sprache", wide: "Breites Layout" },
    pair: { last: "Letzter Preis", high: "24h Hoch", low: "24h Tief", volume: "24h Volumen" },
    order: {
      title: "Market-Order",
      buy: "Kaufen",
      sell: "Verkaufen",
      amount: "Menge",
      spend: "Sie zahlen",
      receive: "Sie erhalten (Schätzung)",
      submit: "Order prüfen",
      ask: "Biatec AI zu dieser Order fragen",
      settings: "Order-Einstellungen",
    },
    dialog: {
      title: "Order-Einstellungen",
      body: "Dies ist ein PrimeVue Dialog. Er muss über dem Biatec-AI-Panel liegen, und Tab muss in ihm bleiben.",
      slippage: "Max. Slippage",
      close: "Schließen",
      save: "Speichern",
    },
    toast: {
      saved: "Einstellungen gespeichert",
      busy: "Biatec AI antwortet noch; die Frage wurde nicht gesendet.",
      unavailable: "Biatec AI ist auf dieser Seite nicht verfügbar.",
      empty: "Bitte zuerst eine Menge eingeben.",
    },
  },
};

function isSupported(l: unknown): l is SupportedLocale {
  return typeof l === "string" && (SUPPORTED_LOCALES as readonly string[]).includes(l);
}

function readStored(): SupportedLocale {
  const fromUrl = new URLSearchParams(globalThis.location?.search ?? "").get("lang");
  if (isSupported(fromUrl)) return fromUrl;
  try {
    const stored = globalThis.localStorage?.getItem(STORAGE_KEY);
    if (isSupported(stored)) return stored;
  } catch {
    // storage blocked: default below
  }
  return "en";
}

export const i18n = createI18n({ legacy: false, locale: readStored(), fallbackLocale: "en", messages });

export const getCurrentLocale = (): SupportedLocale => i18n.global.locale.value as SupportedLocale;

export const setLocale = async (locale: SupportedLocale) => {
  // existing code …
  i18n.global.locale.value = locale;
  document.documentElement.lang = locale;
  try {
    localStorage.setItem(STORAGE_KEY, locale);
  } catch {
    // storage blocked: the choice lasts for this page only
  }
  useBiatecChat().setLocale(locale)   // unknown locales fall back to English inside the widget
}
