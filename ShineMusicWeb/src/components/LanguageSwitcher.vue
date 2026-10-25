<script setup lang="ts">
/**
 * 语言切换器（复用组件）：当前语言高亮，点击立即切换并持久化。
 * 使用场景：
 *  - 登录页顶部（未登录时也能切换，避免登录前看到不懂的语言）
 *  - ProfileDrawer 左侧抽屉「语言」行内嵌展开
 * 语言名以母语显示（简体中文 / English / 日本語），无需再翻译，保证跨语言可识别。
 * 切换底层走 locale/index.setLocale，内部已做存储与重复切换短路。
 */
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check } from 'lucide-vue-next'
import { setLocale, LOCALE_REGISTRY, type Locale } from '@/locale'

const props = withDefaults(
  defineProps<{
    /** 是否显示当前选中右侧的 √ 图标（抽屉内嵌场景需要，登录页顶部小组件可关） */
    showCheck?: boolean
    /** fill：chips 均分撑满父容器（抽屉内嵌）；inline：宽度自适应（登录页顶部右上角） */
    layout?: 'fill' | 'inline'
  }>(),
  { showCheck: false, layout: 'fill' },
)

// vue-i18n composition 模式返回的 locale 是全局单例 ref，setLocale 改动会响应到这里
const { locale } = useI18n()

// 语言选项由全局注册表派生：新增语言只需在 locale/config.ts 登记一次
const options = computed(() =>
  LOCALE_REGISTRY.map(({ code, nativeName }) => ({ value: code as Locale, label: nativeName })),
)

/** 选中切换语言；重复点击当前语言时短路，避免无意义的存储写入 */
function pick(value: Locale) {
  if (locale.value === value) return
  setLocale(value)
}
</script>

<template>
  <view class="lang-switch" :class="`lang-switch--${props.layout}`">
    <view
      v-for="opt in options"
      :key="opt.value"
      class="lang-chip"
      :class="{ active: locale === opt.value }"
      @click="pick(opt.value)"
    >
      <text class="lang-chip-label">{{ opt.label }}</text>
      <view v-if="props.showCheck && locale === opt.value" class="lang-chip-check"><Check :size="12" /></view>
    </view>
  </view>
</template>

<style lang="scss" scoped>
/* 语言切换 chip 组：视觉与 ProfileDrawer theme-chip 对齐，未选中=玻璃面，选中=主色描边 */
.lang-switch { display: flex; flex-wrap: wrap; gap: 12rpx; }
.lang-switch--inline { flex-wrap: nowrap; }
.lang-chip { display: flex; flex: 1; min-width: 0; align-items: center; justify-content: center; gap: 8rpx; padding: 16rpx 18rpx; border: 1rpx solid var(--sm-border); border-radius: 999rpx; background: var(--sm-surface); color: var(--sm-text-2); font-size: 18rpx; font-weight: 600; transition: border-color .18s ease, color .18s ease, box-shadow .18s ease; }
.lang-switch--inline .lang-chip { flex: none; padding: 12rpx 20rpx; font-size: 17rpx; }
.lang-chip.active { border-color: var(--sm-accent); color: var(--sm-accent); box-shadow: 0 0 0 2rpx var(--sm-accent-soft-border); background: var(--sm-accent-soft); }
.lang-chip-label { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.lang-chip-check { display: grid; flex: none; place-items: center; width: 26rpx; height: 26rpx; border-radius: 50%; background: var(--sm-accent); color: var(--sm-accent-ink); }
</style>
