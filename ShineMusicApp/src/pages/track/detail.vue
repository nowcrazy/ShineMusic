<script setup lang="ts">
/**
 * 单曲详情页：大封面 + 标题/艺人 + 播放入口 + 评论区（CommentSection）。
 * 路由：/pages/track/detail?id={trackId}
 * 曲目不存在（404，或私有曲目无权访问）时展示空态。
 */
import { ref } from 'vue'
import { onLoad } from '@dcloudio/uni-app'
import { useI18n } from 'vue-i18n'
import { Play } from 'lucide-vue-next'
import CoverArt from '@/components/CoverArt.vue'
import CommentSection from '@/components/CommentSection.vue'
import EmptyState from '@/components/EmptyState.vue'
import PlayerBar from '@/components/PlayerBar.vue'
import { getTrack } from '@/services/catalog'
import { playTrack } from '@/services/player'
import { formatTime } from '@/utils/format'
import type { Track } from '@/types/music'
import { Music2 } from 'lucide-vue-next'

const { t } = useI18n()

const track = ref<Track | null>(null)
const loading = ref(true)
const notFound = ref(false)

async function load(trackId: string) {
  loading.value = true
  notFound.value = false
  try {
    track.value = await getTrack(trackId)
  } catch {
    notFound.value = true
  } finally {
    loading.value = false
  }
}

function play() {
  if (track.value) playTrack(track.value)
}

onLoad((query) => {
  const id = (query as Record<string, string>).id
  if (id) void load(id)
  else notFound.value = true
})
</script>

<template>
  <view class="track-detail">
    <view v-if="loading" class="detail-state">{{ t('trackDetail.loading') }}</view>
    <view v-else-if="notFound || !track" class="detail-state">
      <EmptyState :icon="Music2" :title="t('trackDetail.notFound')" :hint="t('trackDetail.notFoundHint')" tone="muted" />
    </view>
    <template v-else>
      <view class="detail-hero">
        <view class="detail-cover"><CoverArt :src="track.cover" :seed="track.title" :rounded="28" :icon-size="44" /></view>
        <text class="detail-title">{{ track.title }}</text>
        <text class="detail-artist">{{ track.artist }}</text>
        <view v-if="track.album" class="detail-meta">
          <text class="detail-meta-item">{{ track.album }}</text>
          <text v-if="track.duration" class="detail-meta-item">{{ formatTime(track.duration) }}</text>
        </view>
        <view class="detail-play" @tap="play"><Play :size="20" fill="currentColor" /><text>{{ t('trackDetail.play') }}</text></view>
      </view>

      <CommentSection :track-id="track.id" />
    </template>

    <view class="page-bottom-spacer"></view>
  </view>
  <PlayerBar />
</template>

<style lang="scss" scoped>
.track-detail { min-height: 100vh; padding: 24rpx 36rpx 60rpx; background: var(--sm-page-bg); box-sizing: border-box; }
.detail-state { padding: 120rpx 20rpx; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }

.detail-hero { display: flex; flex-direction: column; align-items: center; gap: 16rpx; padding: 40rpx 28rpx 34rpx; border-radius: var(--sm-radius-card); background: var(--sm-surface-3); border: 1rpx solid var(--sm-border-strong); box-shadow: 0 16rpx 40rpx var(--sm-art-shadow); }
.detail-cover { width: 320rpx; height: 320rpx; border-radius: 28rpx; overflow: hidden; box-shadow: 0 18rpx 44rpx var(--sm-art-shadow); }
.detail-title { margin-top: 10rpx; color: var(--sm-text); font-size: 36rpx; font-weight: 800; text-align: center; }
.detail-artist { color: var(--sm-text-2); font-size: 22rpx; }
.detail-meta { display: flex; gap: 16rpx; margin-top: 4rpx; }
.detail-meta-item { padding: 6rpx 18rpx; border-radius: 999rpx; background: var(--sm-surface); color: var(--sm-text-3); font-size: 17rpx; }
.detail-play { display: flex; align-items: center; gap: 10rpx; margin-top: 18rpx; padding: 18rpx 58rpx; border-radius: 999rpx; background: var(--sm-hero-ink-bg); color: var(--sm-hero-ink); font-size: 21rpx; font-weight: 800; }
.page-bottom-spacer { height: 180rpx; }
</style>