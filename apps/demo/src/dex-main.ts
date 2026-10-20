// The BiatecDEX-like host page from the widget playground (packages/chat-widget/playground/dex, T-068), reused as is,
// mounted the way its own main.ts does it, with this site's backend and client key.
import "../../../packages/chat-widget/playground/dex/dex.css";
import "primeicons/primeicons.css";
import "./dex-banner.css";
import { createApp } from "vue";
import PrimeVue from "primevue/config";
import ToastService from "primevue/toastservice";
import Aura from "@primeuix/themes/aura";
import { BiatecChat } from "@biatec/chat-widget";
import DexApp from "../../../packages/chat-widget/playground/dex/DexApp.vue";
import { i18n, getCurrentLocale } from "@/i18n";
import { useTheme } from "@/theme";
import { BACKEND_URL, CLIENT_KEY } from "./config";

useTheme();

const app = createApp(DexApp);
app.use(PrimeVue, {
  // Without a key PrimeVue 5 shows its "Invalid PrimeUI License" banner; it does not affect the widget.
  license: import.meta.env.PRIMEVUE_LICENSE,
  theme: { preset: Aura, options: { prefix: "p", darkModeSelector: ".p-dark" } },
});
app.use(ToastService);
app.use(i18n);
app.use(BiatecChat, {
  backendUrl: BACKEND_URL,
  clientKey: CLIENT_KEY,
  locale: getCurrentLocale(),
  theme: "host", // follows .p-dark on <html>, like Biatec DEX
  mode: "floating",
});
app.mount("#app");
