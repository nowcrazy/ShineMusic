<script setup lang="ts">
/**
 * 艺人详情页：展示艺人头像、名字、简介、专辑入口列表与曲目列表，支持播放全部。
 * 进入参数（onLoad query）：id —— 艺人 ID（缺失时展示错误态）。
 * 对应后端接口：services/catalog.getArtist（返回艺人详情 + 专辑 + 曲目）。
 */
import { ref } from 'vue'
import { onLoad } from '@dcloudio/uni-app'
import { ChevronRight, Play, UserRound } from 'lucide-vue-next'
import TrackRow from '@/components/TrackRow.vue'
import PlayerBar from '@/components/PlayerBar.vue'
import { getArtist } from '@/services/catalog'
import { playQueue } from '@/services/player'
import type { ApiArtistDetail, Track } from '@/types/music'

// 页面状态：艺人详情（含专辑）/ 曲目列表 / 加载中 / 加载失败
const detail = ref<ApiArtistDetail | null>(null)
const tracks = ref<Track[]>([])
const loading = ref(true)
const loadError = ref(false)

/**
 * 解析 onLoad query：必须携带 id，缺参进入错误态
 * @param options query，期望含 id
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
 * 加载艺人详情、专辑与曲目，并以艺人名字同步导航栏标题
 * @param id 艺人 ID
 * 关键副作用：失败置 loadError；无论成败关闭 loading
 */
async function load(id: string) {
  try {
    const result = await getArtist(id)
    detail.value = result.detail
    tracks.value = result.tracks
    uni.setNavigationBarTitle({ title: result.detail.name })
  } catch {
    loadError.value = true
  } finally {
    loading.value = false
  }
}

/** 播放全部：艺人曲目整体入队（空列表不响应） */
function playAll() {
  if (tracks.value.length) playQueue(tracks.value)
}

/**
 * 跳转专辑详情
 * @param albumId 专辑 ID
 */
function goAlbum(albumId: string) {
  uni.navigateTo({ url: `/pages/catalog/album?id=${albumId}` })
}
</script>

<template>
  <scroll-view class="detail-page" scroll-y :show-scrollbar="false">
    <!-- 加载中 / 失败或不存在 的状态提示 -->
    <view v-if="loading" class="state-text">正在加载…</view>
    <view v-else-if="loadError || !detail" class="state-text">艺人不存在或暂时无法打开。</view>
    <template v-else>
      <!-- 艺人头部：头像、名字、简介、播放全部 -->
      <view class="detail-header">
        <view class="artist-avatar"><UserRound :size="46" /></view>
        <text class="detail-title">{{ detail.name }}</text>
        <text v-if="detail.biography" class="detail-sub">{{ detail.biography }}</text>
        <view v-if="tracks.length" class="play-all" @tap="playAll">
          <Play :size="16" fill="currentColor" /><text>播放全部</text>
        </view>
      </view>

      <!-- 专辑区块：点击行进入专辑详情 -->
      <view v-if="detail.albums.length" class="section">
        <text class="section-title">专辑</text>
        <view
          v-for="album in detail.albums"
          :key="album.id"
          class="album-row"
          @tap="goAlbum(album.id)"
        >
          <text class="album-name">{{ album.title }}</text>
          <ChevronRight :size="16" class="chevron" />
        </view>
      </view>

      <!-- 曲目区块 -->
      <view class="section">
        <text class="section-title">曲目 · {{ tracks.length }}</text>
        <view class="detail-list">
          <TrackRow
            v-for="(track, index) in tracks"
            :key="track.id"
            :track="track"
            :index="index"
            :queue="tracks"
          />
          <view v-if="!tracks.length" class="state-text">还没有可播放的曲目。</view>
        </view>
      </view>
      <view class="page-bottom-spacer"></view>
    </template>
  </scroll-view>

  <!-- 全局播放条 -->
  <PlayerBar />
</template>

<style lang="scss" scoped>
/* 艺人详情（方向 B · 玻璃质感） */
.detail-page { height: 100vh; padding: 32rpx 40rpx 0; box-sizing: border-box; background: var(--sm-page-bg); }
.state-text { padding: 80rpx 20rpx; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }
.detail-header { display: flex; flex-direction: column; align-items: flex-start; padding: 12rpx 0 20rpx; }
.artist-avatar { display: grid; place-items: center; align-self: center; width: 200rpx; height: 200rpx; border: 1rpx solid var(--sm-border-strong); border-radius: 50%; background: var(--sm-cover-bg); color: var(--sm-cover-ink); }
.detail-title { margin-top: 28rpx; color: var(--sm-text); font-size: 38rpx; font-weight: 800; }
.detail-sub { margin-top: 12rpx; color: var(--sm-text-2); font-size: 20rpx; line-height: 1.6; }
.play-all { display: flex; align-items: center; gap: 10rpx; margin-top: 26rpx; padding: 16rpx 34rpx; border-radius: 44rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 20rpx; font-weight: 800; box-shadow: 0 10rpx 30rpx var(--sm-accent-glow); }
.section { margin-top: 40rpx; }
.section-title { display: block; color: var(--sm-text); font-size: 30rpx; font-weight: 800; }
.album-row { display: flex; align-items: center; justify-content: space-between; min-height: 92rpx; border-bottom: 1rpx solid var(--sm-divider); }
.album-name { color: var(--sm-text); font-size: 21rpx; font-weight: 600; }
.chevron { color: var(--sm-text-3); }
.detail-list { margin-top: 12rpx; }
.page-bottom-spacer { height: 180rpx; }
</style>
