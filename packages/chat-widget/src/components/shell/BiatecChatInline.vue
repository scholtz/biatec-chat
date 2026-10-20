<script setup lang="ts">
import { inject, onMounted, shallowRef } from "vue";
import BiatecChatRoot from "../BiatecChatRoot.vue";
import { resolveOptions, type BiatecChatOptions, type ResolvedOptions } from "../../options";
import { sharedCore, type WidgetServices } from "../../api/core";
import widgetCss from "../../styles.css?inline";

/**
 * The widget as a panel in the host's layout (for example a `minmax(360px, 420px)` column next to an order form).
 * It shares the session of the floating widget when one is mounted, and the floating launcher hides while it is
 * here. Rendered into its own shadow root, like the floating widget, so host CSS and widget CSS never mix.
 * Options come from the `BiatecChat` plugin, or from the `options` prop when the plugin is not installed.
 */
const props = defineProps<{ options?: BiatecChatOptions; services?: WidgetServices }>();
const injected = inject<ResolvedOptions | null>("biatec-chat-options", null);
const core = sharedCore();
const resolved: ResolvedOptions | null = core?.options ?? (props.options ? resolveOptions(props.options) : injected);
if (!resolved) console.warn("[biatec-chat] BiatecChatInline needs the BiatecChat plugin or an `options` prop.");

const host = shallowRef<HTMLElement | null>(null);
const target = shallowRef<HTMLElement | null>(null);
onMounted(() => {
  const el = host.value;
  if (!el || !resolved) return;
  const shadow = el.shadowRoot ?? el.attachShadow({ mode: "open" });
  const style = document.createElement("style");
  style.textContent = widgetCss;
  const mountPoint = document.createElement("div");
  shadow.replaceChildren(style, mountPoint);
  target.value = mountPoint;
});
</script>

<template>
  <div ref="host" data-biatec-chat-inline-host>
    <Teleport v-if="target && resolved" :to="target">
      <BiatecChatRoot :options="resolved" :core="core ?? undefined" :services="services" :shadow="true" :inline="true" />
    </Teleport>
  </div>
</template>
