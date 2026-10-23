<script setup lang="ts">
import { computed, ref } from "vue";
import { formatCompact, formatDay, formatNumber } from "../format";

export interface ChartSeries { name: string; values: number[] }

const props = defineProps<{ title: string; dates: string[]; series: ChartSeries[] }>();

const W = 640;
const H = 260;
const M = { l: 48, r: 14, t: 14, b: 30 };
const innerW = W - M.l - M.r;
const innerH = H - M.t - M.b;

/** A maximum that is four whole steps (1, 2 or 5 times a power of ten, at least 1): counts never get fractional ticks. */
function niceMax(v: number): number {
  const raw = Math.max(v, 1) / 4;
  const exp = Math.pow(10, Math.floor(Math.log10(raw)));
  const f = raw / exp;
  const nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
  return 4 * Math.max(1, nice * exp);
}

const yMax = computed(() => niceMax(Math.max(0, ...props.series.flatMap((s) => s.values))));
const yTicks = computed(() => [0, 1, 2, 3, 4].map((i) => (yMax.value * i) / 4));
const n = computed(() => props.dates.length);

function x(i: number): number {
  return n.value <= 1 ? M.l + innerW / 2 : M.l + (innerW * i) / (n.value - 1);
}
function y(v: number): number {
  return M.t + innerH - (innerH * v) / yMax.value;
}

const paths = computed(() =>
  props.series.map((s) => {
    const pts = s.values.map((v, i) => `${x(i).toFixed(1)},${y(v).toFixed(1)}`);
    const line = "M" + pts.join("L");
    const area = s.values.length ? `${line}L${x(s.values.length - 1).toFixed(1)},${y(0)}L${x(0).toFixed(1)},${y(0)}Z` : "";
    return { line, area };
  }),
);

const xLabels = computed(() => {
  const count = Math.min(6, n.value);
  if (count <= 1) return n.value ? [0] : [];
  return Array.from({ length: count }, (_, k) => Math.round((k * (n.value - 1)) / (count - 1)));
});

const active = ref<number | null>(null);

function setFromPointer(e: PointerEvent) {
  const el = e.currentTarget as SVGSVGElement;
  const rect = el.getBoundingClientRect();
  if (!rect.width || n.value === 0) return;
  const px = ((e.clientX - rect.left) / rect.width) * W;
  const t = (px - M.l) / innerW;
  active.value = Math.max(0, Math.min(n.value - 1, Math.round(t * (n.value - 1))));
}
function onKey(e: KeyboardEvent) {
  if (n.value === 0) return;
  const cur = active.value ?? -1;
  if (e.key === "ArrowRight") active.value = Math.min(n.value - 1, cur + 1);
  else if (e.key === "ArrowLeft") active.value = Math.max(0, cur < 0 ? 0 : cur - 1);
  else if (e.key === "Home") active.value = 0;
  else if (e.key === "End") active.value = n.value - 1;
  else if (e.key === "Escape") active.value = null;
  else return;
  e.preventDefault();
}

const tipStyle = computed(() => {
  if (active.value == null) return {};
  const pct = (x(active.value) / W) * 100;
  return pct > 60 ? { right: `${100 - pct + 1}%` } : { left: `${pct + 1}%` };
});
const live = computed(() =>
  active.value == null
    ? ""
    : `${formatDay(props.dates[active.value])}: ` +
      props.series.map((s) => `${s.name} ${formatNumber(s.values[active.value as number])}`).join(", "),
);
</script>

<template>
  <figure class="chart" data-test="line-chart">
    <figcaption class="chart-title">{{ title }}</figcaption>
    <ul v-if="series.length > 1" class="legend" aria-label="Legend">
      <li v-for="(s, i) in series" :key="s.name"><span class="swatch" :class="`s${i}`" aria-hidden="true"></span>{{ s.name }}</li>
    </ul>
    <div class="chart-body">
      <svg
        :viewBox="`0 0 ${W} ${H}`"
        class="chart-svg"
        role="group"
        tabindex="0"
        :aria-label="`${title}. Use left and right arrow keys to read values by day.`"
        @pointermove="setFromPointer"
        @pointerleave="active = null"
        @keydown="onKey"
        @blur="active = null"
      >
        <g class="grid">
          <g v-for="t in yTicks" :key="t">
            <line :x1="M.l" :x2="W - M.r" :y1="y(t)" :y2="y(t)" />
            <text :x="M.l - 6" :y="y(t) + 4" text-anchor="end">{{ formatCompact(t) }}</text>
          </g>
        </g>
        <g class="xaxis">
          <text v-for="i in xLabels" :key="i" :x="x(i)" :y="H - 8" text-anchor="middle">{{ formatDay(dates[i]) }}</text>
        </g>
        <path v-if="series.length === 1" :d="paths[0].area" class="area s0" />
        <path v-for="(p, i) in paths" :key="i" :d="p.line" class="line" :class="`s${i}`" />
        <g v-if="active != null">
          <line class="cursor" :x1="x(active)" :x2="x(active)" :y1="M.t" :y2="M.t + innerH" />
          <circle
            v-for="(s, i) in series"
            :key="s.name"
            class="dot"
            :class="`s${i}`"
            :cx="x(active)"
            :cy="y(s.values[active] ?? 0)"
            r="4.5"
          />
        </g>
      </svg>
      <div v-if="active != null" class="tooltip" :style="tipStyle" data-test="tooltip">
        <div class="tooltip-title">{{ formatDay(dates[active]) }}</div>
        <div v-for="(s, i) in series" :key="s.name" class="tooltip-row">
          <span class="swatch" :class="`s${i}`" aria-hidden="true"></span>{{ s.name }}
          <strong>{{ formatNumber(s.values[active]) }}</strong>
        </div>
      </div>
    </div>
    <p class="sr-only" aria-live="polite">{{ live }}</p>
    <table class="sr-only">
      <caption>{{ title }}</caption>
      <thead>
        <tr><th scope="col">Date</th><th v-for="s in series" :key="s.name" scope="col">{{ s.name }}</th></tr>
      </thead>
      <tbody>
        <tr v-for="(d, i) in dates" :key="d">
          <th scope="row">{{ d }}</th>
          <td v-for="s in series" :key="s.name">{{ s.values[i] }}</td>
        </tr>
      </tbody>
    </table>
  </figure>
</template>
