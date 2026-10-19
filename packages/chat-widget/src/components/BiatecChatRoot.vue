<script setup lang="ts">
import { ref, nextTick } from "vue";
import type { ResolvedOptions } from "../options";
import ChatLauncher from "./ChatLauncher.vue";
import ChatPanel from "./ChatPanel.vue";

const props = defineProps<{ options: ResolvedOptions }>();
const open = ref(false);
const launcher = ref<InstanceType<typeof ChatLauncher> | null>(null);
const panelId = "biatec-chat-panel";

async function setOpen(v: boolean) {
  open.value = v;
  if (!v) {
    await nextTick();
    (launcher.value?.$el as HTMLElement | undefined)?.focus();
  }
}
</script>

<template>
  <div class="biatec-chat" :data-theme="props.options.theme" :lang="props.options.locale">
    <ChatPanel v-if="open" :id="panelId" :tier="null" @close="setOpen(false)">
      <p class="bc-placeholder">Chat is coming soon.</p>
    </ChatPanel>
    <ChatLauncher ref="launcher" :open="open" :controls="panelId" @toggle="setOpen(!open)" />
  </div>
</template>
