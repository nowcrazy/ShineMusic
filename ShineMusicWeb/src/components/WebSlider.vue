<script setup lang="ts">
/**
 * Web 版滑杆，替代 uni-app 的 <slider> 内置组件。
 * 事件载荷与 uni 保持一致：{ detail: { value } }，方便原页面代码直接复用。
 */
import { computed } from 'vue'

const props = withDefaults(
  defineProps<{
    min?: number
    max?: number
    step?: number
    value?: number
    activeColor?: string
    backgroundColor?: string
    blockSize?: number
  }>(),
  {
    min: 0,
    max: 100,
    step: 1,
    value: 0,
    activeColor: 'var(--sm-accent)',
    backgroundColor: 'var(--sm-border)',
    blockSize: 20,
  },
)

const emit = defineEmits<{
  (e: 'changing', payload: { detail: { value: number } }): void
  (e: 'change', payload: { detail: { value: number } }): void
}>()

const percent = computed(() => {
  const span = props.max - props.min || 1
  return Math.max(0, Math.min(100, ((props.value - props.min) / span) * 100))
})

function readValue(target: EventTarget | null): number {
  return Number((target as HTMLInputElement | null)?.value ?? props.value)
}

function onInput(event: Event) {
  emit('changing', { detail: { value: readValue(event.target) } })
}

function onChange(event: Event) {
  emit('change', { detail: { value: readValue(event.target) } })
}
</script>

<template>
  <input
    class="web-slider"
    type="range"
    :min="min"
    :max="max"
    :step="step"
    :value="value"
    :style="{
      '--sl-active': activeColor,
      '--sl-bg': backgroundColor,
      '--sl-thumb': `${blockSize}px`,
      '--sl-percent': `${percent}%`,
    }"
    @input="onInput"
    @change="onChange"
  />
</template>

<style scoped>
.web-slider {
  -webkit-appearance: none;
  appearance: none;
  width: 100%;
  height: 4px;
  border-radius: 999px;
  outline: none;
  cursor: pointer;
  background: linear-gradient(
    to right,
    var(--sl-active) 0%,
    var(--sl-active) var(--sl-percent),
    var(--sl-bg) var(--sl-percent),
    var(--sl-bg) 100%
  );
}

.web-slider::-webkit-slider-thumb {
  -webkit-appearance: none;
  appearance: none;
  width: var(--sl-thumb);
  height: var(--sl-thumb);
  border-radius: 50%;
  border: 2px solid var(--sl-active);
  background: #fff;
  box-shadow: 0 1px 4px rgb(0 0 0 / 25%);
}

.web-slider::-moz-range-thumb {
  width: var(--sl-thumb);
  height: var(--sl-thumb);
  border-radius: 50%;
  border: 2px solid var(--sl-active);
  background: #fff;
}
</style>
