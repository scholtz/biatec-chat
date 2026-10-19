<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from "vue";
import { nextUid } from "./uid";

const props = defineProps<{ open: boolean; title: string }>();
const emit = defineEmits<{ close: [] }>();

const FOCUSABLE =
  "button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex='-1'])";
const titleId = `bc-dlg-${nextUid()}`;
const el = ref<HTMLElement | null>(null);
let previous: HTMLElement | null = null;

/** document.activeElement is the shadow host inside a shadow root, so ask the dialog's own root. */
function activeIn(node: HTMLElement): HTMLElement | null {
  const root = node.getRootNode() as Document | ShadowRoot;
  return (root.activeElement as HTMLElement | null) ?? null;
}

function items(): HTMLElement[] {
  return el.value ? [...el.value.querySelectorAll<HTMLElement>(FOCUSABLE)] : [];
}

function restoreFocus() {
  previous?.focus?.();
  previous = null;
}

watch(
  () => props.open,
  async (v) => {
    if (v) {
      await nextTick();
      if (!el.value) return;
      previous = activeIn(el.value);
      (items()[0] ?? el.value).focus();
    } else {
      restoreFocus();
    }
  },
  { immediate: true, flush: "post" },
);
onBeforeUnmount(() => {
  if (props.open) restoreFocus();
});

function onKeydown(e: KeyboardEvent) {
  if (e.key === "Escape") {
    e.stopPropagation();
    emit("close");
    return;
  }
  if (e.key !== "Tab" || !el.value) return;
  const list = items();
  const first = list[0];
  const last = list[list.length - 1];
  if (!first || !last) {
    e.preventDefault();
    return;
  }
  const active = activeIn(el.value);
  if (e.shiftKey && (active === first || active === el.value)) {
    e.preventDefault();
    last.focus();
  } else if (!e.shiftKey && active === last) {
    e.preventDefault();
    first.focus();
  }
}
</script>

<template>
  <div v-if="open" class="bc-scrim" @keydown="onKeydown">
    <div ref="el" class="bc-dialog" role="dialog" aria-modal="true" :aria-labelledby="titleId" tabindex="-1">
      <h2 :id="titleId" class="bc-dialog-title">{{ title }}</h2>
      <slot />
    </div>
  </div>
</template>
