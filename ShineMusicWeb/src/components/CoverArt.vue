<script setup lang="ts">
/**
 * CoverArt：统一封面图组件。
 * 有封面 URL 时正常展示图片；无封面时显示：
 * - 若提供了 seed（如歌曲名）→ 生成确定性的彩色渐变背景 + 音符图标
 * - 否则 → 主题令牌渐变 + 音符图标
 * 用法：<CoverArt :src="track.cover" :seed="track.title" :rounded="14" />
 */
import { computed } from 'vue'
import { AudioLines } from 'lucide-vue-next'

const props = withDefaults(
  defineProps<{
    /** 封面图片地址；空字符串/null 表示无封面，展示占位图 */
    src?: string | null
    /** 圆角大小（rpx）；默认 14 */
    rounded?: number
    /** 图标大小；默认 18 */
    iconSize?: number
    /** 占位图色彩种子（如歌曲名），相同种子产生相同渐变 */
    seed?: string
  }>(),
  { src: '', rounded: 14, iconSize: 18, seed: '' },
)

/** 是否有有效封面 */
const hasCover = computed(() => Boolean(props.src))
/** 容器圆角样式 */
const radiusStyle = computed(() => ({
  borderRadius: `${props.rounded}rpx`,
}))

/** 简易字符串 hash（djb2 算法的变体），返回 32-bit 无符号整数 */
function simpleHash(str: string): number {
  let h = 5381
  for (let i = 0; i < str.length; i++) {
    h = ((h << 5) + h + str.charCodeAt(i)) >>> 0
  }
  return h >>> 0
}

/** HSL 转 hex (#RRGGBB) */
function hslToHex(h: number, s: number, l: number): string {
  h = ((h % 360) + 360) % 360
  s = Math.max(0, Math.min(100, s)) / 100
  l = Math.max(0, Math.min(100, l)) / 100
  const c = (1 - Math.abs(2 * l - 1)) * s
  const x = c * (1 - Math.abs(((h / 60) % 2) - 1))
  const m = l - c / 2
  let r = 0, g = 0, b = 0
  if (h < 60) { r = c; g = x; b = 0 }
  else if (h < 120) { r = x; g = c; b = 0 }
  else if (h < 180) { r = 0; g = c; b = x }
  else if (h < 240) { r = 0; g = x; b = c }
  else if (h < 300) { r = x; g = 0; b = c }
  else { r = c; g = 0; b = x }
  const toHex = (v: number) => {
    const n = Math.round((v + m) * 255)
    return n.toString(16).padStart(2, '0')
  }
  return `#${toHex(r)}${toHex(g)}${toHex(b)}`
}

/**
 * 根据 seed 生成确定性的渐变背景。
 * - 主色 hue：hash % 360（0-359）
 * - 饱和度：45-75% 之间
 * - 两个亮度层：前景 50% / 背景 30%
 */
const placeholderStyle = computed(() => {
  const base: Record<string, string> = {
    ...radiusStyle.value,
    border: '1px solid var(--sm-border)',
  }
  if (!props.seed) {
    // 无 seed：使用主题令牌
    base.background = 'var(--sm-cover-bg)'
    return base
  }
  const h = simpleHash(props.seed) % 360
  const s1 = 55 + (simpleHash(props.seed + 's1') % 20) // 55-74
  const s2 = 40 + (simpleHash(props.seed + 's2') % 20) // 40-59
  const l1 = 48 + (simpleHash(props.seed + 'l1') % 10) // 48-57
  const l2 = 28 + (simpleHash(props.seed + 'l2') % 12) // 28-39
  const c1 = hslToHex(h, s1, l1)
  const c2 = hslToHex((h + 40) % 360, s2, l2)
  base.background = `linear-gradient(135deg, ${c1} 0%, ${c2} 100%)`
  return base
})
</script>

<template>
  <img v-if="hasCover" class="cover-art" :src="props.src!" mode="aspectFill" :style="radiusStyle" lazy-load />
  <view v-else class="cover-art cover-placeholder" :style="placeholderStyle">
    <AudioLines :size="props.iconSize" :stroke-width="2.5" />
  </view>
</template>

<style scoped>
.cover-art { width: 100%; height: 100%; display: flex; align-items: center; justify-content: center; overflow: hidden; }
.cover-placeholder { color: var(--sm-cover-ink); }
</style>
