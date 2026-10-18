<script setup lang="ts">
/**
 * 主题外观设置页：展示全部内置主题并支持即时切换。
 *
 * 切换原理：applyTheme() 会把 <html data-sm-theme> 改写为目标主题 id，
 * 所有页面样式均引用 CSS 令牌变量，因此本页点击后全站立即换肤，无需跳转或刷新。
 * 当前选中主题来自 services/theme 的响应式 currentTheme。
 * 新增主题只需在 services/theme.ts 的 THEMES 中追加一项，并在 App.vue 补齐令牌。
 */
import { Check, Palette } from 'lucide-vue-next'
import { applyTheme, currentTheme, THEMES, type ThemeId } from '@/services/theme'

/** 选中某个主题（点击即生效并持久化） */
function selectTheme(id: ThemeId) {
  if (currentTheme.value === id) return
  applyTheme(id)
  uni.showToast({ title: `已切换为「${THEMES.find((t) => t.id === id)?.name}」`, icon: 'none' })
}
</script>

<template>
  <scroll-view class="theme-page" scroll-y :show-scrollbar="false">
    <!-- 页头 -->
    <view class="page-heading">
      <view class="heading-icon"><Palette :size="20" /></view>
      <view class="heading-copy">
        <text class="heading-title">主题外观</text>
        <text class="heading-subtitle">选择喜欢的界面风格，即时生效并自动记住</text>
      </view>
    </view>

    <!-- 主题卡片列表 -->
    <view class="theme-list">
      <view
        v-for="theme in THEMES"
        :key="theme.id"
        class="theme-card"
        :class="{ active: currentTheme === theme.id }"
        @tap="selectTheme(theme.id)"
      >
        <!-- 迷你预览：用该主题自身的色卡绘制，不受当前主题影响 -->
        <view class="theme-preview" :style="{ background: theme.swatch.bg }">
          <view class="preview-card" :style="{ background: theme.swatch.surface, borderColor: theme.swatch.accent }">
            <view class="preview-dot" :style="{ background: theme.swatch.accent }"></view>
            <view class="preview-lines">
              <view class="preview-line" :style="{ background: theme.swatch.text }"></view>
              <view class="preview-line short" :style="{ background: theme.swatch.text }"></view>
            </view>
          </view>
          <view class="preview-pill" :style="{ background: theme.swatch.accent }"></view>
        </view>

        <!-- 主题信息 -->
        <view class="theme-copy">
          <view class="theme-name-row">
            <text class="theme-name">{{ theme.name }}</text>
            <text class="theme-tone">{{ theme.tone }}</text>
          </view>
          <text class="theme-desc">{{ theme.description }}</text>
        </view>

        <!-- 选中标记 -->
        <view class="theme-check" :class="{ checked: currentTheme === theme.id }">
          <Check v-if="currentTheme === theme.id" :size="14" />
        </view>
      </view>
    </view>

    <view class="theme-note">
      <text>后续新增主题会直接出现在这里，无需其它设置。</text>
    </view>

    <view class="bottom-space"></view>
  </scroll-view>
</template>

<style lang="scss" scoped>
/* 主题外观设置页：自身也全部使用主题令牌，保证四种主题下都协调 */
.theme-page { height: 100vh; padding: 30rpx 34rpx 0; box-sizing: border-box; background: var(--sm-page-bg); }

.page-heading { display: flex; align-items: center; gap: 16rpx; padding: 10rpx 0 28rpx; }
.heading-icon { display: grid; place-items: center; width: 68rpx; height: 68rpx; border-radius: 18rpx; background: var(--sm-accent-soft); border: 1rpx solid var(--sm-border); color: var(--sm-accent); }
.heading-copy { display: flex; flex-direction: column; gap: 6rpx; }
.heading-title { color: var(--sm-text); font-size: 29rpx; font-weight: 800; }
.heading-subtitle { color: var(--sm-text-3); font-size: 17rpx; }

.theme-list { display: flex; flex-direction: column; gap: 18rpx; }

.theme-card {
  display: flex;
  align-items: center;
  gap: 20rpx;
  padding: 22rpx;
  border: 1rpx solid var(--sm-border);
  border-radius: var(--sm-radius-card);
  background: var(--sm-surface);
  -webkit-backdrop-filter: blur(var(--sm-blur));
  backdrop-filter: blur(var(--sm-blur));
  box-shadow: var(--sm-shadow-sm);
  transition: border-color .2s ease, transform .2s ease;
}

.theme-card.active {
  border-color: var(--sm-accent);
  box-shadow: 0 0 0 2rpx var(--sm-accent-soft-border), var(--sm-shadow-sm);
}

/* 迷你预览 */
.theme-preview {
  position: relative;
  display: flex;
  flex: none;
  align-items: flex-end;
  width: 130rpx;
  height: 130rpx;
  padding: 14rpx;
  border: 1rpx solid var(--sm-border);
  border-radius: 18rpx;
  overflow: hidden;
}

.preview-card {
  display: flex;
  align-items: center;
  gap: 8rpx;
  width: 100%;
  padding: 10rpx;
  border: 2rpx solid transparent;
  border-radius: 10rpx;
}

.preview-dot { flex: none; width: 16rpx; height: 16rpx; border-radius: 50%; }
.preview-lines { display: flex; flex: 1; flex-direction: column; gap: 6rpx; min-width: 0; }
.preview-line { height: 6rpx; border-radius: 3rpx; opacity: .85; }
.preview-line.short { width: 60%; opacity: .45; }
.preview-pill { position: absolute; top: 14rpx; right: 14rpx; width: 34rpx; height: 12rpx; border-radius: 6rpx; }

/* 主题信息 */
.theme-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 8rpx; }
.theme-name-row { display: flex; align-items: center; gap: 10rpx; }
.theme-name { color: var(--sm-text); font-size: 23rpx; font-weight: 800; }
.theme-tone { padding: 3rpx 12rpx; border: 1rpx solid var(--sm-border); border-radius: 20rpx; background: var(--sm-surface-2); color: var(--sm-text-3); font-size: 13rpx; }
.theme-desc { color: var(--sm-text-2); font-size: 16rpx; line-height: 1.55; }

/* 选中标记 */
.theme-check { display: grid; flex: none; place-items: center; width: 44rpx; height: 44rpx; border: 1rpx solid var(--sm-border-strong); border-radius: 50%; color: var(--sm-accent-ink); }
.theme-check.checked { border-color: transparent; background: var(--sm-accent); }

.theme-note { margin-top: 30rpx; padding: 22rpx 24rpx; border: 1rpx dashed var(--sm-border-strong); border-radius: var(--sm-radius-card); color: var(--sm-text-3); font-size: 16rpx; line-height: 1.6; text-align: center; }

.bottom-space { height: 60rpx; }
</style>
