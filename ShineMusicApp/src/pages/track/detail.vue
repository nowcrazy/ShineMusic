<script setup lang="ts">
/**
 * 单曲详情页：紧凑曲目信息、专辑入口与评论区（CommentSection）。
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

function goAlbum() {
  if (track.value?.albumId) uni.navigateTo({ url: `/pages/catalog/album?id=${encodeURIComponent(track.value.albumId)}` })
}

onLoad((query) => {
  const id = (query as Record<string, string>).id
  if (id) {
    void load(id)
  } else {
    // 无 id 时同样要结束加载态，否则模板会永久停在「加载中…」。
    notFound.value = true
    loading.value = false
  }
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
        <view class="detail-copy">
        <text class="detail-title">{{ track.title }}</text>
        <text class="detail-artist">{{ track.artist }}</text>
        <view class="detail-meta">
          <button v-if="track.albumId" class="album-link" @tap="goAlbum">{{ track.album || t('nav.album') }}</button>
          <text v-else-if="track.album" class="detail-meta-item">{{ track.album }}</text>
          <text v-if="track.duration" class="detail-meta-item">{{ formatTime(track.duration) }}</text>
        </view>
        <button class="detail-play" @tap="play"><Play :size="16" fill="currentColor" /><text>{{ t('trackDetail.play') }}</text></button>
        </view>
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

.detail-hero { display: flex; align-items: flex-start; gap: 24rpx; padding: 28rpx; border-radius: var(--sm-radius-card); background: var(--sm-surface); border: 1px solid var(--sm-border); }
.detail-cover { flex: none; width: 144rpx; height: 144rpx; border-radius: 20rpx; overflow: hidden; }
.detail-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 10rpx; }
.detail-title { color: var(--sm-text); font-size: 32rpx; font-weight: 800; overflow-wrap: anywhere; }
.detail-artist { color: var(--sm-text-2); font-size: 24rpx; }
.detail-meta { display: flex; flex-wrap: wrap; align-items: center; gap: 12rpx; }
.detail-meta-item { color: var(--sm-text-3); font-size: 22rpx; }
.album-link { margin: 0; padding: 0; background: transparent; color: var(--sm-accent); font-size: 24rpx; text-align: left; line-height: 1.6; }
.album-link::after { border: 0; }
.detail-play { display: flex; align-self: flex-start; align-items: center; gap: 10rpx; margin: 8rpx 0 0; padding: 8rpx 24rpx; border-radius: var(--sm-radius-pill); background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 24rpx; }
@media (min-width: 960px) { .detail-cover { width: 112px; height: 112px; } }
.page-bottom-spacer { height: 180rpx; }
</style>