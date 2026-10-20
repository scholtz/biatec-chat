<script setup lang="ts">
import type { TierDisplay } from "../tier";
import { t } from "../i18n";
import ShellHeader from "./shell/ShellHeader.vue";
import StatusBar from "./shell/StatusBar.vue";
import type { AgentModeDisplay } from "./shell/agentMode";

export type PanelMode = "floating" | "inline" | "workspace";

/**
 * Panel frame: header, status bar, conversation and the regions T-066 fills.
 * Slots: default = conversation; `banner` = under the status bar (paused banner, refusals); `aside` = Activity aside (beside the conversation in workspace, an overlay sheet
 * otherwise; rendered only while Activity is on); `dialog` = permissions dialog.
 */
const props = defineProps<{
  id: string;
  mode: PanelMode;
  /** Covers the viewport (workspace, or a floating panel below 640 px). */
  fullscreen: boolean;
  modal: boolean;
  /** Mounted by BiatecChatInline (no Close, no Minimize). */
  inline: boolean;
  tier: TierDisplay;
  email?: string | null;
  primaryAddress?: string | null;
  signingIn?: boolean;
  agentMode: AgentModeDisplay;
  activityOpen: boolean;
  permissionsOpen: boolean;
  notice?: string;
}>();
const emit = defineEmits<{
  close: [];
  minimize: [];
  toggleExpand: [];
  signIn: [];
  signOut: [];
  openPermissions: [];
  toggleActivity: [];
}>();
const asideId = `${props.id}-activity`;
</script>

<template>
  <section
    :id="id"
    class="bc-panel"
    :data-mode="mode"
    :data-fullscreen="fullscreen ? '' : undefined"
    :role="modal ? 'dialog' : 'region'"
    :aria-modal="modal ? 'true' : undefined"
    :aria-label="t('shell.panelLabel')"
  >
    <ShellHeader
      :tier="tier"
      :email="email"
      :primary-address="primaryAddress"
      :signing-in="signingIn"
      :expanded="mode === 'workspace'"
      :show-minimize="mode === 'floating'"
      :show-expand="!inline"
      :show-close="!inline"
      @sign-in="emit('signIn')"
      @sign-out="emit('signOut')"
      @toggle-expand="emit('toggleExpand')"
      @minimize="emit('minimize')"
      @close="emit('close')"
    />
    <StatusBar
      :mode="agentMode"
      :permissions-open="permissionsOpen"
      :activity-open="activityOpen"
      :controls-aside="asideId"
      @open-permissions="emit('openPermissions')"
      @toggle-activity="emit('toggleActivity')"
    />
    <slot name="banner" />
    <p v-if="notice" class="bc-shell-notice" role="status" data-testid="shell-notice">{{ notice }}</p>
    <div class="bc-body">
      <div class="bc-shell-main">
        <slot />
      </div>
      <aside v-if="activityOpen && $slots.aside" :id="asideId" class="bc-shell-aside" :aria-label="t('shell.activity')">
        <slot name="aside" />
      </aside>
    </div>
    <slot name="dialog" />
  </section>
</template>
