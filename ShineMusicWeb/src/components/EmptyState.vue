<script setup lang="ts">
/**
 * EmptyState：统一空态组件（插画 + 文案 + CTA）。
 * 用一抹主题渐变的圆盘插画 + 居中的图标替代裸文字提示，让「无内容 / 不存在 / 未登录」
 * 等状态更有层级感，且自动跟随当前主题令牌换肤。
 *
 * 用法：
 *   <EmptyState :icon="Inbox" :title="t('notifications.empty')"
 *     :hint="t('notifications.emptyHint')" :action-text="t('auth.goLogin')" @action="goLogin" />
 *
 * 说明：
 * - icon 传入 lucide-vue-next 图标组件；
 * - actionText 非空时渲染 CTA 按钮，点击触发 action 事件；
 * - compact 用于列表内嵌的空态（更紧凑的间距与更小的插画）。
 */
import { computed, type Component } from 'vue'

const props = withDefaults(
  defineProps<{
    /** 插画中央的图标（lucide-vue-next 组件） */
    icon: Component
    /** 主文案（必填，一句话说明当前状态） */
    title: string
    /** 辅助文案（可选，补充说明或下一步提示） */
    hint?: string
    /** CTA 按钮文案（可选，非空时显示按钮） */
    actionText?: string
    /** 内嵌于列表中的紧凑模式 */
    compact?: boolean
    /** 插画色调：default=主题强调色，muted=中性弱化（错误/不存在态） */
    tone?: 'default' | 'muted'
  }>(),
  { hint: '', actionText: '', compact: false, tone: 'default' },
)

const emit = defineEmits<{ action: [] }>()

/** 是否显示 CTA 按钮 */
const hasAction = computed(() => Boolean(props.actionText))

/** 点击 CTA */
function handleAction() {
  emit('action')
}
</script>

<template>
  <view class="empty-state" :class="{ 'empty-state--compact': props.compact }">
    <!-- 插画：主题渐变圆盘 + 中心图标 + 环绕装饰点 -->
    <view class="empty-art" :class="{ 'empty-art--muted': props.tone === 'muted' }">
      <view class="art-ring"></view>
      <view class="art-core">
        <component :is="props.icon" :size="props.compact ? 30 : 40" :stroke-width="2.2" />
      </view>
      <view class="art-dot dot-a"></view>
      <view class="art-dot dot-b"></view>
      <view class="art-dot dot-c"></view>
    </view>

    <text class="empty-title">{{ props.title }}</text>
    <text v-if="props.hint" class="empty-hint">{{ props.hint }}</text>

    <view v-if="hasAction" class="empty-action" hover-class="empty-action--hover" @click="handleAction">
      <text>{{ props.actionText }}</text>
    </view>
  </view>
</template>

<style lang="scss" scoped>
/* 空态：插画由主题令牌渐变 + 半透明圆环绘制，浅/深色主题下都协调 */
.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 18rpx;
  padding: 96rpx 48rpx;
  text-align: center;
}

.empty-state--compact {
  gap: 14rpx;
  padding: 60rpx 32rpx;
}

/* 插画圆盘 */
.empty-art {
  position: relative;
  display: grid;
  place-items: center;
  width: 176rpx;
  height: 176rpx;
  margin-bottom: 8rpx;
}

.empty-state--compact .empty-art {
  width: 132rpx;
  height: 132rpx;
  margin-bottom: 2rpx;
}

/* 外圈光晕 */
.empty-art::before {
  content: '';
  position: absolute;
  inset: 0;
  border-radius: 50%;
  background: var(--sm-accent-soft);
  opacity: .8;
}

/* 内圈渐变 */
.art-ring {
  position: absolute;
  inset: 22rpx;
  border-radius: 50%;
  background: var(--sm-accent-grad);
  opacity: .85;
}

.empty-state--compact .art-ring {
  inset: 16rpx;
}

/* 中心圆面 + 图标（压在渐变之上） */
.art-core {
  position: relative;
  z-index: 2;
  display: grid;
  place-items: center;
  width: 104rpx;
  height: 104rpx;
  border-radius: 50%;
  background: var(--sm-surface);
  border: 1rpx solid var(--sm-border-strong);
  color: var(--sm-accent);
  box-shadow: var(--sm-shadow-sm);
}

.empty-state--compact .art-core {
  width: 80rpx;
  height: 80rpx;
}

/* 环绕装饰点：悬停在插画边缘，营造“漂浮音符”的活泼感 */
.art-dot {
  position: absolute;
  z-index: 3;
  border-radius: 50%;
  background: var(--sm-accent);
  box-shadow: 0 4rpx 12rpx var(--sm-accent-glow);
}

.dot-a { width: 16rpx; height: 16rpx; top: 4rpx; right: 22rpx; opacity: .9; }
.dot-b { width: 11rpx; height: 11rpx; bottom: 16rpx; left: 12rpx; opacity: .6; }
.dot-c { width: 9rpx; height: 9rpx; bottom: 8rpx; right: 8rpx; background: var(--sm-accent-2); opacity: .7; }

.empty-state--compact .art-dot { transform: scale(.8); }

/* 弱化态（错误/不存在）：改用中性色，与强调色插画区分 */
.empty-art--muted::before {
  background: var(--sm-muted-soft);
}

.empty-art--muted .art-ring {
  background: var(--sm-surface-3);
  opacity: 1;
}

.empty-art--muted .art-core {
  color: var(--sm-text-3);
}

.empty-art--muted .art-dot {
  background: var(--sm-text-3);
  box-shadow: none;
}

.empty-title {
  color: var(--sm-text);
  font-size: 25rpx;
  font-weight: 800;
  line-height: 1.4;
}

.empty-state--compact .empty-title {
  font-size: 22rpx;
}

.empty-hint {
  max-width: 480rpx;
  color: var(--sm-text-3);
  font-size: 17rpx;
  line-height: 1.6;
}

.empty-action {
  margin-top: 10rpx;
  padding: 16rpx 52rpx;
  border-radius: 999rpx;
  background: var(--sm-accent);
  color: var(--sm-accent-ink);
  font-size: 19rpx;
  font-weight: 800;
  box-shadow: 0 10rpx 30rpx var(--sm-accent-glow);
  transition: transform .15s ease, opacity .15s ease;
}

.empty-action--hover {
  transform: translateY(-2rpx);
  opacity: .92;
}
</style>