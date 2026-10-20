<script setup lang="ts">
import { computed, ref } from "vue";
import { BiatecAiChip, BiatecChatInline, useBiatecChat, type AskResult, type ChatTheme } from "@biatec/chat-widget";
import {
  ArrowUpRight,
  CircleCheck,
  Clock,
  LogIn,
  MessageSquare,
  Moon,
  ShieldCheck,
  Sun,
} from "lucide-vue-next";
import { SECTIONS, STATUS_LABEL, type Item, type Status } from "./sections";
import { BACKEND_URL, REPO_URL, docUrl } from "./config";
import { pageTheme, togglePageTheme } from "./theme";
import ArchitectureDiagram from "./ArchitectureDiagram.vue";

const chat = useBiatecChat();
const props = defineProps<{ mode: "floating" | "workspace" }>();

const backendHost = new URL(BACKEND_URL).host;
const onStage = backendHost === "stage.chat.biatec.io";

const STATUS_ICON = { live: CircleCheck, "signed-in": LogIn, next: Clock } as const;

/** One polite live-region message per section: what happened to the last one-click prompt. */
const notices = ref<Record<string, string>>({});
const ASK_NOTICE: Record<AskResult, string> = {
  sent: "Sent to Biatec AI. The answer is in the chat panel.",
  busy: "Biatec AI is still answering the previous message. Try again when it finishes.",
  empty: "Nothing to send.",
  unavailable: "The chat is not ready yet. Reload the page and try again.",
};
async function ask(sectionId: string, item: Item) {
  if (!item.prompt) return;
  notices.value[sectionId] = "";
  try {
    notices.value[sectionId] = ASK_NOTICE[await chat.ask(item.prompt)];
  } catch {
    // ask() rejects when the widget cannot reach the backend at all.
    notices.value[sectionId] = ASK_NOTICE.unavailable;
  }
}

const widgetTheme = ref<ChatTheme>("host");
const WIDGET_THEMES: { value: ChatTheme; label: string }[] = [
  { value: "host", label: "Follow page" },
  { value: "light", label: "Light" },
  { value: "dark", label: "Dark" },
];
function setWidgetTheme(t: ChatTheme) {
  widgetTheme.value = t;
  chat.setTheme(t);
}

const widgetLocale = ref("en");
function setWidgetLocale(l: string) {
  widgetLocale.value = l;
  chat.setLocale(l);
}

const showInline = ref(false);
const otherModeHref = computed(() => (props.mode === "workspace" ? "./" : "./?mode=workspace"));

const statusClass = (s: Status) => `status status-${s}`;
</script>

<template>
  <a class="skip" href="#main">Skip to content</a>
  <header class="topbar">
    <div class="wrap topbar-inner">
      <a class="brand" href="./">
        <BiatecAiChip class="brand-chip" aria-hidden="true" />
        <span class="brand-name">Biatec AI</span>
        <span class="tag">Demo</span>
      </a>
      <div class="topbar-actions">
        <span class="network" title="Every action on this site runs on Algorand testnet">Testnet</span>
        <button
          type="button"
          class="icon-btn"
          :aria-label="pageTheme === 'dark' ? 'Switch the page to light' : 'Switch the page to dark'"
          :title="pageTheme === 'dark' ? 'Switch the page to light' : 'Switch the page to dark'"
          @click="togglePageTheme"
        >
          <Sun v-if="pageTheme === 'dark'" :size="20" aria-hidden="true" />
          <Moon v-else :size="20" aria-hidden="true" />
        </button>
        <a class="btn btn-ghost topbar-repo" :href="REPO_URL" rel="noopener" target="_blank">
          GitHub <ArrowUpRight :size="16" aria-hidden="true" />
        </a>
      </div>
    </div>
  </header>

  <main id="main" class="wrap">
    <section class="hero" aria-labelledby="hero-title">
      <p class="kicker">Live on {{ onStage ? "stage" : backendHost }}</p>
      <h1 id="hero-title">An assistant that can act on chain, and never holds your keys</h1>
      <p class="lead">
        This page embeds the real Biatec AI widget against the {{ onStage ? "stage" : "configured" }} backend
        <code>{{ backendHost }}</code>. Each section below says what you are seeing and has one-click prompts that go to
        the chat as ordinary messages. Anything that is not on stage yet is labelled <strong>Coming next</strong>; nothing
        here is simulated.
      </p>
      <div class="hero-actions">
        <button type="button" class="btn btn-primary" @click="chat.open()">
          <MessageSquare :size="20" aria-hidden="true" /> Open Biatec AI
        </button>
        <a class="btn btn-secondary" href="#how">How it works</a>
      </div>
      <ul class="legend" aria-label="Status labels">
        <li v-for="s in (['live', 'signed-in', 'next'] as const)" :key="s" :class="statusClass(s)">
          <component :is="STATUS_ICON[s]" :size="16" aria-hidden="true" />{{ STATUS_LABEL[s] }}
        </li>
      </ul>
    </section>

    <nav class="toc" aria-label="Sections">
      <ol>
        <li v-for="(s, i) in SECTIONS" :key="s.id"><a :href="`#${s.id}`">{{ i + 1 }}. {{ s.title }}</a></li>
        <li><a href="#modes">{{ SECTIONS.length + 1 }}. Widget modes</a></li>
        <li><a href="#how">{{ SECTIONS.length + 2 }}. How it works</a></li>
      </ol>
    </nav>

    <section v-for="(s, i) in SECTIONS" :id="s.id" :key="s.id" class="card" :aria-labelledby="`${s.id}-title`">
      <h2 :id="`${s.id}-title`"><span class="num">{{ i + 1 }}</span>{{ s.title }}</h2>
      <p class="seeing"><strong>What you're seeing.</strong> {{ s.seeing }}</p>

      <div v-if="s.id === 'guest'" class="controls">
        <div class="control" role="group" aria-label="Widget language">
          <span class="control-label">Widget language</span>
          <button
            v-for="l in ['en', 'sk']"
            :key="l"
            type="button"
            class="seg"
            :aria-pressed="widgetLocale === l"
            @click="setWidgetLocale(l)"
          >
            {{ l === "en" ? "English" : "Slovenčina" }}
          </button>
        </div>
        <div class="control" role="group" aria-label="Widget theme">
          <span class="control-label">Widget theme</span>
          <button
            v-for="t in WIDGET_THEMES"
            :key="t.value"
            type="button"
            class="seg"
            :aria-pressed="widgetTheme === t.value"
            @click="setWidgetTheme(t.value)"
          >
            {{ t.label }}
          </button>
        </div>
      </div>

      <ul class="items">
        <li v-for="item in s.items" :key="item.label" class="item">
          <div class="item-head">
            <span class="item-label">{{ item.label }}</span>
            <span :class="statusClass(item.status)">
              <component :is="STATUS_ICON[item.status]" :size="16" aria-hidden="true" />{{ STATUS_LABEL[item.status] }}
            </span>
          </div>
          <p v-if="item.note" class="item-note">{{ item.note }}</p>
          <button
            v-if="item.prompt && item.status !== 'next'"
            type="button"
            class="prompt"
            @click="ask(s.id, item)"
          >
            <MessageSquare :size="16" aria-hidden="true" />
            <span>Ask: “{{ item.prompt }}”</span>
          </button>
        </li>
      </ul>

      <div v-if="s.id === 'evidence'" class="verify">
        <p>Verify a downloaded bundle offline, from a clone of the repository:</p>
        <pre tabindex="0" role="region" aria-label="Offline verification commands"><code>curl -sO {{ BACKEND_URL }}/.well-known/evidence-jwks.json
pnpm --filter evidence-verifier build
node packages/evidence-verifier/cli.mjs verify bundle.json --jwks evidence-jwks.json</code></pre>
      </div>

      <p class="notice" role="status" aria-live="polite">{{ notices[s.id] }}</p>
      <p v-if="s.docs" class="docs">
        Read more:
        <template v-for="(d, j) in s.docs" :key="d.path">
          <a :href="docUrl(d.path)" rel="noopener" target="_blank">{{ d.label }}</a><span v-if="j < s.docs.length - 1"> · </span>
        </template>
      </p>
    </section>

    <section id="modes" class="card" aria-labelledby="modes-title">
      <h2 id="modes-title"><span class="num">{{ SECTIONS.length + 1 }}</span>Widget modes</h2>
      <p class="seeing">
        <strong>What you're seeing.</strong> The same widget, the same session, in three layouts. Floating is the
        default launcher and panel (full screen below 640 px). Inline puts the panel in the page's own layout, and the
        floating launcher hides while it is there. Workspace opens the panel full screen. The widget renders in a shadow
        root, so this page's CSS never reaches it.
      </p>
      <ul class="items">
        <li class="item">
          <div class="item-head">
            <span class="item-label">{{ mode === "workspace" ? "Workspace (this page now)" : "Floating (this page now)" }}</span>
            <span :class="statusClass('live')"><CircleCheck :size="16" aria-hidden="true" />{{ STATUS_LABEL.live }}</span>
          </div>
          <a class="btn btn-secondary" :href="otherModeHref">
            {{ mode === "workspace" ? "Back to floating mode" : "Reload in workspace mode" }}
          </a>
        </li>
        <li class="item">
          <div class="item-head">
            <span class="item-label">Inline panel (BiatecChatInline)</span>
            <span :class="statusClass('live')"><CircleCheck :size="16" aria-hidden="true" />{{ STATUS_LABEL.live }}</span>
          </div>
          <button type="button" class="btn btn-secondary" :aria-expanded="showInline" aria-controls="inline-slot" @click="showInline = !showInline">
            {{ showInline ? "Remove the inline panel" : "Show the inline panel here" }}
          </button>
          <div id="inline-slot" class="inline-slot" :hidden="!showInline">
            <BiatecChatInline v-if="showInline" />
          </div>
        </li>
        <li class="item">
          <div class="item-head">
            <span class="item-label">Inside a Biatec DEX-styled page</span>
            <span :class="statusClass('live')"><CircleCheck :size="16" aria-hidden="true" />{{ STATUS_LABEL.live }}</span>
          </div>
          <p class="item-note">
            The widget embedded the way docs/integration-biatec-dex.md tells Biatec DEX to do it (PrimeVue, Tailwind,
            .p-dark). The trading form and its price are an illustrative host page and do nothing; only the widget is live.
          </p>
          <a class="btn btn-secondary" href="./dex.html">Open the DEX-styled page</a>
        </li>
      </ul>
    </section>

    <section id="how" class="card" aria-labelledby="how-title">
      <h2 id="how-title"><span class="num">{{ SECTIONS.length + 2 }}</span>How it works</h2>
      <p class="seeing">
        <strong>What you're seeing.</strong> The widget is a thin client. The backend is a BFF: it holds the session,
        talks to the model, builds and previews transactions, and asks Biatec OIDC to sign. Limits and phone approval
        live at Biatec OIDC, so the chat can only ask for more consent, never less.
      </p>
      <ArchitectureDiagram />
      <h3>Security invariants</h3>
      <ul class="invariants">
        <li><ShieldCheck :size="20" aria-hidden="true" />The browser never receives an access, refresh or ID token. Only the backend holds tokens, encrypted, in its vault.</li>
        <li><ShieldCheck :size="20" aria-hidden="true" />Signing always goes from the backend straight to Biatec OIDC, never through Biatec MCP.</li>
        <li><ShieldCheck :size="20" aria-hidden="true" />The backend never hand-assembles transaction groups: it uses Biatec MCP, the CLAMM SDK and algo-safe.</li>
        <li><ShieldCheck :size="20" aria-hidden="true" />Content from outside the user is untrusted data; tier and agent-mode gates are enforced server-side, whatever the model says.</li>
      </ul>
      <p class="docs">
        Read more:
        <a :href="docUrl('docs/architecture.md')" rel="noopener" target="_blank">Architecture</a> ·
        <a :href="docUrl('docs/threat-model.md')" rel="noopener" target="_blank">Threat model</a> ·
        <a :href="docUrl('docs/integration-widget.md')" rel="noopener" target="_blank">Embedding the widget</a> ·
        <a :href="docUrl('docs/demo-site.md')" rel="noopener" target="_blank">About this demo</a>
      </p>
    </section>
  </main>

  <footer class="wrap footer">
    <p>
      Biatec AI demo · Algorand testnet only · Answers come from Claude on Amazon Bedrock and can be wrong; nothing here
      is financial advice. Source:
      <a :href="REPO_URL" rel="noopener" target="_blank">github.com/scholtz/biatec-chat</a>.
    </p>
  </footer>
</template>
