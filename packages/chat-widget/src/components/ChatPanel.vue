<script setup lang="ts">
import { ref, computed, onMounted } from "vue";
import type { TierDisplay } from "../tier";

const props = defineProps<{
  id: string;
  tier: TierDisplay;
  modal: boolean;
  email?: string | null;
  primaryAddress?: string | null;
  signingIn?: boolean;
}>();
const emit = defineEmits<{ close: []; signIn: []; signOut: [] }>();
const closeBtn = ref<HTMLButtonElement | null>(null);
onMounted(() => closeBtn.value?.focus());

const authenticated = computed(() => props.tier === "Authenticated");
const label = computed(() => (authenticated.value ? props.email || "Signed in" : "Guest"));
const shortAddress = computed(() => {
  const a = props.primaryAddress;
  return a ? (a.length > 13 ? `${a.slice(0, 6)}...${a.slice(-4)}` : a) : "";
});
</script>

<template>
  <section :id="id" class="bc-panel" role="dialog" :aria-modal="modal" aria-label="Biatec Chat">
    <header class="bc-header">
      <span class="bc-title">Biatec Chat</span>
      <span class="bc-tier" data-testid="tier">
        {{ label }}<template v-if="authenticated && shortAddress"> <span class="bc-addr" :title="primaryAddress ?? ''">({{ shortAddress }})</span></template>
      </span>
      <button v-if="authenticated" type="button" class="bc-link" data-testid="sign-out" @click="emit('signOut')">Sign out</button>
      <button v-else type="button" class="bc-link" data-testid="sign-in" :disabled="signingIn" @click="emit('signIn')">Sign in</button>
      <button ref="closeBtn" type="button" class="bc-close" aria-label="Close chat" @click="emit('close')">
        &times;
      </button>
    </header>
    <div class="bc-body">
      <slot />
    </div>
  </section>
</template>
