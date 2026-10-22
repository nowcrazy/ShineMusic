<script setup lang="ts">
/**
 * 精选专题详情页：展示专题封面、简介与专题内曲目列表，支持播放全部。
 * 进入参数（onLoad query）：id —— 专题 ID（缺失时直接展示错误态）。
 * 对应后端接口：services/catalog.getCollection（返回专题详情 + 曲目列表）。
 */
import { ref } from 'vue'
import { onLoad } from '@dcloudio/uni-app'
import { Music2, Play } from 'lucide-vue-next'
import TrackRow from '@/components/TrackRow.vue'
import PlayerBar from '@/components/PlayerBar.vue'
import { getCollection } from '@/services/catalog'
import { resolveUrl } from '@/services/http'
import { playQueue } from '@/services/player'
import type { ApiCollectionDetail, Track } from '@/types/music'

// 页面状态：专题详情 / 曲目列表 / 封面完整 URL / 加载中 / 加载失败
const detail = ref<ApiCollectionDetail | null>(null)
const tracks = ref<Track[]>([])
const cover = ref('')
const loading = ref(true)
const loadError = ref(false)

/**
 * 页面入参解析：必须携带 id，缺参直接进入错误态，否则拉取详情
 * @param options onLoad query，期望含 id
 */
onLoad((options) => {
  const id = options?.id
  if (!id) {
    loadError.value = true
    loading.value = false
    return
  }
  void load(id)
})

/**
 * 加载专题详情与曲目，并同步原生导航栏标题
 * @param id 专题 ID
 * 关键副作用：失败置 loadError；无论成败关闭 loading
 */
async function load(id: string) {
  try {
    const result = await getCollection(id)
    detail.value = result.detail
    tracks.value = result.tracks
    cover.value = result.detail.artworkUrl ? resolveUrl(result.detail.artworkUrl) : ''
    uni.setNavigationBarTitle({ title: result.detail.title })
  } catch {
    loadError.value = true
  } finally {
    loading.value = false
  }
}

/** 播放全部：将专题曲目整体作为队列交给播放器（空列表不响应） */
function playAll() {
  if (tracks.value.length) playQueue(tracks.value)
}
</script>

<template>
  <scroll-view class="detail-page" scroll-y :show-scrollbar="false">
    <!-- 加载中 / 失败或不存在 的状态提示 -->
    <view v-if="loading" class="state-text">{{ $t('catalogPage.loading') }}</view>
    <view v-else-if="loadError || !detail" class="state-text">{{ $t('catalogPage.collectionUnavailable') }}</view>
    <template v-else>
      <!-- 专题头部：封面、标题、简介、曲目数、播放全部 -->
      <view class="detail-header">
        <image v-if="cover" class="detail-art" :src="cover" mode="aspectFill" />
        <view v-else class="detail-art detail-art-fallback"><Music2 :size="46" /></view>
        <text class="detail-title">{{ detail.title }}</text>
        <text v-if="detail.description" class="detail-sub">{{ detail.description }}</text>
        <text class="detail-meta">{{ $t('catalogPage.editorialPicks') }} · {{ $t('catalog.trackCount', { count: tracks.length }) }}</text>
        <view class="play-all" @tap="playAll"><Play :size="16" fill="currentColor" /><text>{{ $t('catalogPage.playAll') }}</text></view>
      </view>
      <!-- 专题曲目列表（空专题给出空态提示） -->
      <view class="detail-list">
        <TrackRow
          v-for="(track, index) in tracks"
          :key="track.id"
          :track="track"
          :index="index"
          :queue="tracks"
        />
        <view v-if="!tracks.length" class="state-text">{{ $t('catalogPage.collectionNoTracks') }}</view>
      </view>
      <view class="page-bottom-spacer"></view>
    </template>
  </scroll-view>

  <!-- 全局播放条 -->
  <PlayerBar />
</template>

<style lang="scss" scoped>
/* 专题详情（方向 B · 玻璃质感） */
.detail-page { height: 100vh; padding: 32rpx 40rpx 0; box-sizing: border-box; background: var(--sm-page-bg); }
.state-text { padding: 80rpx 20rpx; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }
.detail-header { display: flex; flex-direction: column; align-items: flex-start; padding: 12rpx 0 20rpx; }
.detail-art { width: 300rpx; height: 300rpx; align-self: center; border: 1rpx solid var(--sm-border-strong); border-radius: 26rpx; background: var(--sm-surface-3); box-shadow: 0 18rpx 44rpx var(--sm-art-shadow); }
.detail-art-fallback { display: grid; place-items: center; color: var(--sm-text-3); }
.detail-title { margin-top: 30rpx; color: var(--sm-text); font-size: 38rpx; font-weight: 800; }
.detail-sub { margin-top: 12rpx; color: var(--sm-text-2); font-size: 20rpx; line-height: 1.6; }
.detail-meta { margin-top: 14rpx; color: var(--sm-text-2); font-size: 17rpx; }
.play-all { display: flex; align-items: center; gap: 10rpx; margin-top: 26rpx; padding: 16rpx 34rpx; border-radius: 44rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 20rpx; font-weight: 800; box-shadow: 0 10rpx 30rpx var(--sm-accent-glow); }
.detail-list { margin-top: 26rpx; }
.page-bottom-spacer { height: 180rpx; }
</style>
