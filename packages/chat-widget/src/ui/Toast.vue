<script setup lang="ts">
import { t } from "../i18n";
import IconButton from "./IconButton.vue";
import { X } from "lucide-vue-next";

export interface ToastMessage {
  id: string | number;
  text: string;
  tone?: "neutral" | "danger";
}

defineProps<{ messages: ToastMessage[] }>();
// The live region is always in the DOM so assistive tech announces messages added later.
defineEmits<{ dismiss: [id: string | number] }>();
</script>

<template>
  <div class="bc-toasts" aria-live="polite" role="status">
    <div v-for="m in messages" :key="m.id" class="bc-toast" :data-tone="m.tone ?? 'neutral'">
      <span>{{ m.text }}</span>
      <IconButton :label="t('ui.dismiss')" @click="$emit('dismiss', m.id)"><X :size="16" :stroke-width="2" aria-hidden="true" /></IconButton>
    </div>
  </div>
</template>
