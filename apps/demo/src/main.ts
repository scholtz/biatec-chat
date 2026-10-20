import "./tokens.css";
import "./demo.css";
import { createApp } from "vue";
import { BiatecChat } from "@biatec/chat-widget";
import App from "./App.vue";
import { BACKEND_URL, CLIENT_KEY } from "./config";
import { HOST_DARK_CLASS, applyPageTheme } from "./theme";

applyPageTheme();

// ?mode=workspace shows the workspace layout (section 6); anything else is the default floating widget.
const mode = new URLSearchParams(location.search).get("mode") === "workspace" ? "workspace" : "floating";

createApp(App, { mode })
  .use(BiatecChat, {
    backendUrl: BACKEND_URL,
    clientKey: CLIENT_KEY,
    locale: "en",
    theme: "host",
    hostDarkClass: HOST_DARK_CLASS,
    suggestions: "default",
    mode,
  })
  .mount("#app");
