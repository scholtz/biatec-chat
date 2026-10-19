<script setup lang="ts">
import { ref, nextTick, watch, onBeforeUnmount } from "vue";
import type { ResolvedOptions } from "../options";
import ChatLauncher from "./ChatLauncher.vue";
import ChatPanel from "./ChatPanel.vue";

const props = defineProps<{ options: ResolvedOptions }>();
const open = ref(false);
const modal = ref(false);
const launcher = ref<InstanceType<typeof ChatLauncher> | null>(null);
const rootEl = ref<HTMLElement | null>(null);
const panelId = "biatec-chat-panel";

async function setOpen(v: boolean) {
  open.value = v;
  if (!v) {
    await nextTick();
    (launcher.value?.$el as HTMLElement | undefined)?.focus();
  }
}

/** The panel covers the whole viewport on small screens, so it behaves as a modal there only. */
function isModal(): boolean {
  return typeof window !== "undefined" && !!window.matchMedia?.("(max-width: 640px)").matches;
}

const FOCUSABLE = "button:not([disabled]), [href], input:not([disabled]), select, textarea, [tabindex]:not([tabindex='-1'])";

function onDocKeydown(e: KeyboardEvent) {
  if (e.key === "Escape") {
    void setOpen(false);
    return;
  }
  if (e.key !== "Tab" || !modal.value || !rootEl.value) return;
  const items = [...rootEl.value.querySelectorAll<HTMLElement>(FOCUSABLE)];
  const first = items[0];
  const last = items[items.length - 1];
  if (!first || !last) return;
  const active = document.activeElement;
  if (!rootEl.value.contains(active)) {
    e.preventDefault();
    first.focus();
  } else if (e.shiftKey && active === first) {
    e.preventDefault();
    last.focus();
  } else if (!e.shiftKey && active === last) {
    e.preventDefault();
    first.focus();
  }
}

// Escape works wherever focus is while the panel is open; Tab is trapped on mobile, where the panel is full screen.
watch(open, (v) => {
  modal.value = v && isModal();
  if (v) document.addEventListener("keydown", onDocKeydown);
  else document.removeEventListener("keydown", onDocKeydown);
});
onBeforeUnmount(() => document.removeEventListener("keydown", onDocKeydown));
</script>

<template>
  <div ref="rootEl" class="biatec-chat" :data-theme="props.options.theme" :lang="props.options.locale">
    <ChatPanel v-if="open" :id="panelId" :tier="null" :modal="modal" @close="setOpen(false)">
      <p class="bc-placeholder">Chat is coming soon.</p>
    </ChatPanel>
    <ChatLauncher ref="launcher" :open="open" :controls="panelId" @toggle="setOpen(!open)" />
  </div>
</template>
