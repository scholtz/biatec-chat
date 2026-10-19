import { createApp, h, ref } from "vue";
import { BiatecChat, BiatecChatInline, useBiatecChat } from "../src/index";

// Host page that drives the widget through the host API, like Biatec DEX does (docs/integration-biatec-dex.md).
// Query parameters for screenshots: ?open, ?inline, ?mode=workspace, ?theme=light.
const q = new URLSearchParams(location.search);
const showInline = ref(q.has("inline"));
const chat = useBiatecChat();
if (q.has("open")) chat.open();
const btn = (label: string, onClick: () => void) => h("button", { type: "button", onClick, style: "font-size:14px;margin:4px" }, label);

createApp({
  render: () =>
    h("main", { style: "display:grid;grid-template-columns:1fr minmax(360px,420px);gap:24px;padding:24px" }, [
      h("section", [
        h("h1", "Host page"),
        h("p", "The chat launcher is at the bottom right. These buttons use useBiatecChat()."),
        btn("Open", () => chat.open()),
        btn("Close", () => chat.close()),
        btn("Ask: Swap 50 USDC to ALGO", () => void chat.ask("Swap 50 USDC to ALGO")),
        btn("Page context: ALGO/USDC", () => chat.setPageContext("Biatec DEX · testnet · pair ALGO/USDC")),
        btn("Clear page context", () => chat.setPageContext(undefined)),
        btn("Theme light", () => chat.setTheme("light")),
        btn("Theme dark", () => chat.setTheme("dark")),
        btn("Locale sk", () => chat.setLocale("sk")),
        btn(showInline.value ? "Remove inline panel" : "Show inline panel", () => (showInline.value = !showInline.value)),
      ]),
      showInline.value ? h(BiatecChatInline) : null,
    ]),
})
  .use(BiatecChat, {
    backendUrl: "http://localhost:5000",
    clientKey: "playground",
    locale: "en",
    theme: (q.get("theme") as "light" | null) ?? "host", // toggle the p-dark class on <html> in devtools to see it follow
    hostDarkClass: "p-dark",
    suggestions: "default",
    mode: (q.get("mode") as "workspace" | null) ?? "floating",
  })
  .mount("#app");
