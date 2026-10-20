// Mirrors the order of BiatecDEX src/main.ts: app.css, theme before mount, PrimeVue (Aura, `.p-dark`), toasts, i18n,
// and then the widget plugin exactly as step 1 of docs/integration-biatec-dex.md writes it.
import "./dex.css";
import "primeicons/primeicons.css";
import { createApp } from "vue";
import PrimeVue from "primevue/config";
import ToastService from "primevue/toastservice";
import Aura from "@primeuix/themes/aura";
import { BiatecChat } from '@biatec/chat-widget'
import DexApp from "./DexApp.vue";
import { i18n } from "@/i18n";
import { getCurrentLocale } from '@/i18n'
import { useTheme } from "@/theme";

useTheme();

const app = createApp(DexApp);
app.use(PrimeVue, {
  // PrimeVue 5 shows an "Invalid PrimeUI License" banner (bottom right) without a key; it does not affect the widget.
  license: import.meta.env.PRIMEVUE_LICENSE,
  theme: { preset: Aura, options: { prefix: "p", darkModeSelector: ".p-dark" } },
});
app.use(ToastService);
app.use(i18n);

app.use(BiatecChat, {
  backendUrl: import.meta.env.VITE_BIATEC_CHAT_URL || 'https://chat.biatec.io',
  // The doc's production key is 'biatec-dex'; the local dev tenant accepts "dev" (default from playground/vite.config.ts).
  clientKey: import.meta.env.VITE_BIATEC_CHAT_CLIENT_KEY || 'biatec-dex',
  locale: getCurrentLocale(),
  theme: 'host',               // follows .p-dark on <html>
  mode: 'floating',
})

app.mount("#app");
