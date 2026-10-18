<script setup lang="ts">
/**
 * TrackRow：通用曲目行组件（方向 A · 呼吸感大卡片）
 * 用于首页推荐/搜索结果、专辑/艺人/专题详情、歌单等所有曲目列表。
 * 方向 A 改动：封面放大到 96rpx、行高加大到 130rpx、卡片化（背景+圆角+无硬分隔线）
 */
import { computed } from 'vue'
import { Heart, Play } from 'lucide-vue-next'
import type { Track } from '@/types/music'
import { playTrack } from '@/services/player'
import { favoriteIds, toggleFavorite } from '@/services/library'
import { clearFeedback, feedbackMap, setFeedback } from '@/services/feedback'
import { isAuthenticated, requireLogin } from '@/services/session'
import { formatTime } from '@/utils/format'
import CoverArt from './CoverArt.vue'

const props = withDefaults(
  defineProps<{
    track: Track
    index?: number
    queue?: Track[]
    showHeart?: boolean
  }>(),
  { index: undefined, queue: undefined, showHeart: true },
)

const subtitle = computed(() =>
  props.track.album ? `${props.track.artist} · ${props.track.album}` : props.track.artist,
)
const notInterested = computed(() => feedbackMap.value.get(props.track.id) === 'not_interested')

function handlePlay() {
  playTrack(props.track, props.queue)
}

async function handleLike() {
  if (!isAuthenticated.value) { requireLogin(); return }
  try { await toggleFavorite(props.track) }
  catch { uni.showToast({ title: '操作失败，请稍后再试', icon: 'none' }) }
}

function handleLongPress() {
  if (!requireLogin()) return
  const marked = notInterested.value
  uni.showActionSheet({
    itemList: [marked ? '取消"不感兴趣"' : '不感兴趣'],
    success: async ({ tapIndex }) => {
      if (tapIndex !== 0) return
      try {
        if (marked) {
          await clearFeedback(props.track.id)
          uni.showToast({ title: '已恢复推荐', icon: 'none' })
        } else {
          await setFeedback(props.track.id, 'not_interested')
          uni.showToast({ title: '已减少此类推荐', icon: 'none' })
        }
      } catch { uni.showToast({ title: '操作失败，请稍后再试', icon: 'none' }) }
    },
  })
}
</script>

<template>
  <view class="tr-row" :class="{ muted: notInterested }" @tap="handlePlay" @longpress="handleLongPress">
    <text v-if="index !== undefined" class="tr-index">{{ String(index + 1).padStart(2, '0') }}</text>
    <view class="tr-cover"><CoverArt :src="track.cover" :seed="track.title" :rounded="16" :icon-size="20" /></view>
    <view class="tr-copy">
      <text class="tr-title">{{ track.title }}</text>
      <text class="tr-artist">{{ subtitle }}</text>
    </view>
    <view class="tr-actions">
      <text v-if="notInterested" class="tr-flag">不感兴趣</text>
      <view
        v-if="showHeart"
        class="tr-like"
        :class="{ liked: favoriteIds.has(track.id) }"
        @tap.stop="handleLike"
      >
        <Heart :size="18" :fill="favoriteIds.has(track.id) ? 'currentColor' : 'none'" />
      </view>
      <text class="tr-duration">{{ formatTime(track.duration) }}</text>
      <view class="tr-play"><Play :size="15" fill="currentColor" /></view>
    </view>
  </view>
</template>

<style lang="scss" scoped>
/* 方向 A：呼吸感大卡片 —— 更大封面、更大内边距、无硬分隔线、背景卡片化 */
.tr-row {
  display: flex; align-items: center; gap: 24rpx;
  min-height: 130rpx;
  padding: 14rpx 20rpx;
  border-radius: 24rpx;
  background: var(--sm-surface-2);
  -webkit-backdrop-filter: blur(12px);
  backdrop-filter: blur(12px);
  transition: background .15s ease;
}
.tr-row + .tr-row { margin-top: 16rpx; }
.tr-row:active { background: var(--sm-surface-3); }
.tr-row.muted { opacity: .45; }

.tr-flag { padding: 4rpx 12rpx; border-radius: 20rpx; color: var(--sm-text-3); font-size: 14rpx; background: var(--sm-surface); }

.tr-index { width: 40rpx; color: var(--sm-text-3); font-family: 'DM Sans', sans-serif; font-size: 22rpx; font-weight: 700; }

.tr-cover {
  flex: none; width: 96rpx; height: 96rpx; border-radius: 16rpx; overflow: hidden;
  background: var(--sm-surface-3);
  box-shadow: 0 6rpx 18rpx var(--sm-art-shadow);
}

.tr-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 8rpx; }
.tr-title { overflow: hidden; color: var(--sm-text); font-size: 26rpx; font-weight: 700; text-overflow: ellipsis; white-space: nowrap; }
.tr-artist { overflow: hidden; color: var(--sm-text-2); font-size: 19rpx; text-overflow: ellipsis; white-space: nowrap; }

.tr-actions { display: flex; flex: none; align-items: center; gap: 14rpx; }
.tr-like { display: grid; place-items: center; width: 44rpx; height: 44rpx; color: var(--sm-text-3); border-radius: 50%; }
.tr-like.liked { color: var(--sm-danger); }
.tr-duration { width: 64rpx; color: var(--sm-text-2); font-family: 'DM Sans', sans-serif; font-size: 18rpx; text-align: right; }
.tr-play { display: grid; place-items: center; width: 44rpx; height: 44rpx; color: var(--sm-text-2); border-radius: 50%; }
</style>
