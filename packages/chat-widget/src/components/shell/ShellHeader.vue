<script setup lang="ts">
import { computed } from "vue";
import { Maximize2, Minimize2, Minus, X } from "lucide-vue-next";
import { BaiSymbol, Button, IconButton } from "../../ui";
import { t } from "../../i18n";
import type { TierDisplay } from "../../tier";
import { shortAddress } from "./agentMode";

const props = defineProps<{
  tier: TierDisplay;
  email?: string | null;
  primaryAddress?: string | null;
  signingIn?: boolean;
  /** Workspace (full screen) is active. */
  expanded: boolean;
  showMinimize: boolean;
  showClose: boolean;
}>();
const emit = defineEmits<{ signIn: []; signOut: []; toggleExpand: []; minimize: []; close: [] }>();

const authenticated = computed(() => props.tier === "Authenticated");
const short = computed(() => shortAddress(props.primaryAddress));
</script>

<template>
  <header class="bc-shell-header">
    <BaiSymbol :size="42" aria-hidden="true" />
    <div class="bc-shell-heading">
      <h2 class="bc-shell-title">{{ t("shell.title") }}</h2>
      <p class="bc-shell-sub" data-testid="tier">
        <template v-if="authenticated">
          <span class="bc-shell-email">{{ email || t("shell.signedIn") }}</span>
          <template v-if="short">
            <span aria-hidden="true"> · </span>
            <span class="bc-shell-addr" :title="primaryAddress ?? ''">{{ short }}</span>
          </template>
        </template>
        <template v-else>{{ t("shell.guest") }}</template>
      </p>
    </div>
    <Button v-if="authenticated" variant="ghost" size="sm" data-testid="sign-out" @click="emit('signOut')">
      {{ t("shell.signOut") }}
    </Button>
    <Button v-else variant="ghost" size="sm" data-testid="sign-in" :disabled="signingIn" @click="emit('signIn')">
      {{ t("shell.signIn") }}
    </Button>
    <div class="bc-shell-actions">
      <IconButton
        :label="expanded ? t('shell.exitFullScreen') : t('shell.expand')"
        data-testid="expand"
        @click="emit('toggleExpand')"
      >
        <Minimize2 v-if="expanded" :size="16" aria-hidden="true" />
        <Maximize2 v-else :size="16" aria-hidden="true" />
      </IconButton>
      <IconButton v-if="showMinimize" :label="t('shell.minimize')" data-testid="minimize" @click="emit('minimize')">
        <Minus :size="16" aria-hidden="true" />
      </IconButton>
      <IconButton v-if="showClose" class="bc-close" :label="t('shell.close')" data-testid="close" @click="emit('close')">
        <X :size="16" aria-hidden="true" />
      </IconButton>
    </div>
  </header>
</template>
