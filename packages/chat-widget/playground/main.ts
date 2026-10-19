import { createApp, h } from "vue";
import { BiatecChat } from "../src/index";

createApp({ render: () => h("main", [h("h1", "Host page"), h("p", "The chat launcher is at the bottom right.")]) })
  .use(BiatecChat, {
    backendUrl: "http://localhost:5000",
    clientKey: "playground",
    locale: "en",
    theme: "host", // toggle the p-dark class on <html> in devtools to see it follow
    hostDarkClass: "p-dark",
    suggestions: "default",
  })
  .mount("#app");
