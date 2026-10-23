<script setup lang="ts">
/**
 * 快捷面板（底部弹出宫格菜单）：首页 tabbar 中间凸起按钮的承载面板。
 * 收纳高频个人功能：消息 / 播放历史 / 我的收藏 / 关注与粉丝 / 上传音频 / 主题外观。
 * 打开时机：已登录用户点击 tabbar 中间按钮（未登录由首页直接引导去登录页）。
 * 面板常驻 DOM 以支持滑入/滑出过渡，关闭时 pointer-events 置空不影响页面交互。
 */
import { Bell, Heart, History, Palette, Upload, UserRound, X } from 'lucide-vue-next'
import { useI18n } from 'vue-i18n'

// 文案多语言
const { t } = useI18n()

// 显隐由父组件 v-model 控制
const props = defineProps<{ modelValue: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()

/** 关闭面板 */
function close() {
  emit('update:modelValue', false)
}

/** 跳转后自动收起面板 */
function go(url: string) {
  close()
  uni.navigateTo({ url })
}
</script>

<template>
  <view class="qpanel-root" :class="{ open: props.modelValue }">
    <!-- 遮罩：点击关闭 -->
    <view class="qpanel-mask" @tap="close"></view>

    <!-- 底部面板 -->
    <view class="qpanel-sheet">
      <view class="qpanel-head">
        <text class="qpanel-title">{{ t('quickPanel.title') }}</text>
        <view class="qpanel-close" @tap="close"><X :size="18" /></view>
      </view>

      <view class="qpanel-grid">
        <view class="qpanel-item" @tap="go('/pages/notifications/index')">
          <view class="qpanel-icon notify"><Bell :size="21" /></view>
          <text class="qpanel-label">{{ t('quickPanel.messages') }}</text>
        </view>
        <view class="qpanel-item" @tap="go('/pages/history/index')">
          <view class="qpanel-icon history"><History :size="21" /></view>
          <text class="qpanel-label">{{ t('quickPanel.history') }}</text>
        </view>
        <view class="qpanel-item" @tap="go('/pages/library/favorites')">
          <view class="qpanel-icon favorite"><Heart :size="21" /></view>
          <text class="qpanel-label">{{ t('quickPanel.favorites') }}</text>
        </view>
        <view class="qpanel-item" @tap="go('/pages/social/follow')">
          <view class="qpanel-icon follow"><UserRound :size="21" /></view>
          <text class="qpanel-label">{{ t('quickPanel.follow') }}</text>
        </view>
        <view class="qpanel-item" @tap="go('/pages/library/upload')">
          <view class="qpanel-icon upload"><Upload :size="21" /></view>
          <text class="qpanel-label">{{ t('quickPanel.upload') }}</text>
        </view>
        <view class="qpanel-item" @tap="go('/pages/settings/theme')">
          <view class="qpanel-icon theme"><Palette :size="21" /></view>
          <text class="qpanel-label">{{ t('quickPanel.theme') }}</text>
        </view>
      </view>
    </view>
  </view>
</template>

<style lang="scss" scoped>
/* 快捷面板（底部弹出 · 玻璃质感） */
.qpanel-root { position: fixed; inset: 0; z-index: 95; pointer-events: none; }
.qpanel-root.open { pointer-events: auto; }
.qpanel-mask { position: absolute; inset: 0; background: rgba(8, 10, 14, .52); opacity: 0; transition: opacity .25s ease; }
.qpanel-root.open .qpanel-mask { opacity: 1; }
.qpanel-sheet { position: absolute; right: 0; bottom: 0; left: 0; padding: 30rpx 34rpx calc(env(safe-area-inset-bottom) + 30rpx); border: 1rpx solid var(--sm-border); border-bottom: none; border-radius: 34rpx 34rpx 0 0; background: var(--sm-bg-soft); box-shadow: 0 -12rpx 44rpx rgba(0, 0, 0, .3); transform: translateY(103%); transition: transform .3s cubic-bezier(.32, .72, 0, 1); box-sizing: border-box; }
.qpanel-root.open .qpanel-sheet { transform: translateY(0); }
.qpanel-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 26rpx; }
.qpanel-title { color: var(--sm-text); font-size: 26rpx; font-weight: 800; }
.qpanel-close { display: grid; place-items: center; width: 56rpx; height: 56rpx; border-radius: 50%; background: var(--sm-surface-3); color: var(--sm-text-2); }
.qpanel-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 22rpx; }
/* 宽视口（横屏 / 折叠屏展开）：高度受限或横向空间富余，宫格改为一行 6 列 */
@media (orientation: landscape) and (max-height: 820px) and (min-width: 560px),
       (hover: none) and (pointer: coarse) and (min-width: 560px) and (min-aspect-ratio: 2/3) {
  .qpanel-grid { grid-template-columns: repeat(6, 1fr); }
}
.qpanel-item { display: flex; flex-direction: column; align-items: center; gap: 12rpx; padding: 24rpx 0 20rpx; border: 1rpx solid var(--sm-border); border-radius: 22rpx; background: var(--sm-surface-2); transition: background .15s ease; }
.qpanel-item:active { background: var(--sm-surface-3); }
.qpanel-icon { display: grid; place-items: center; width: 84rpx; height: 84rpx; border-radius: 24rpx; }
.qpanel-icon.notify { background: var(--sm-warn-soft); color: var(--sm-warn); }
.qpanel-icon.history { background: var(--sm-info-soft); color: var(--sm-accent-2); }
.qpanel-icon.favorite { background: var(--sm-danger-soft); color: var(--sm-danger); }
.qpanel-icon.follow { background: var(--sm-accent-3-soft); color: var(--sm-accent-3); }
.qpanel-icon.upload { background: var(--sm-accent-soft); color: var(--sm-accent); }
.qpanel-icon.theme { background: var(--sm-info-soft); color: var(--sm-accent-2); }
.qpanel-label { color: var(--sm-text); font-size: 19rpx; font-weight: 600; }
</style>
