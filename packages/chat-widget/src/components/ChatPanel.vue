<script setup lang="ts">
import { ref, onMounted } from "vue";
import type { TierDisplay } from "../tier";

defineProps<{ id: string; tier: TierDisplay; modal: boolean }>();
const emit = defineEmits<{ close: [] }>();
const closeBtn = ref<HTMLButtonElement | null>(null);
onMounted(() => closeBtn.value?.focus());
</script>

<template>
  <section :id="id" class="bc-panel" role="dialog" :aria-modal="modal" aria-label="Biatec Chat">
    <header class="bc-header">
      <span class="bc-title">Biatec Chat</span>
      <span class="bc-tier" data-testid="tier">{{ tier ?? "Tier: n/a" }}</span>
      <button ref="closeBtn" type="button" class="bc-close" aria-label="Close chat" @click="emit('close')">
        &times;
      </button>
    </header>
    <div class="bc-body">
      <slot />
    </div>
  </section>
</template>
