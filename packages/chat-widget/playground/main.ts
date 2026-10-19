import { createApp, h } from "vue";
import { BiatecChat } from "../src/index";

createApp({ render: () => h("main", [h("h1", "Host page"), h("p", "The chat launcher is at the bottom right.")]) })
  .use(BiatecChat, {
    backendUrl: "http://localhost:5000",
    clientKey: "playground",
    locale: "en",
    theme: "auto",
    suggestions: "default",
  })
  .mount("#app");
