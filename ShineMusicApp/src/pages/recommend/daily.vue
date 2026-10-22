<script setup lang="ts">
/**
 * 每日推荐列表页。
 * 进入即拉取后端每日推荐（默认 30 首、互不重复、当日稳定）。
 * - 已登录：基于播放次数/跳过/反馈/收藏的个性化结果；
 * - 未登录：全局热度 + 最新兜底列表。
 * 支持下拉刷新（同一天内容稳定，刷新仅用于重试失败场景）。
 */
import { ref } from 'vue'
import { onLoad, onPullDownRefresh } from '@dcloudio/uni-app'
import { Play, RefreshCw, Sparkles } from 'lucide-vue-next'
import TrackRow from '@/components/TrackRow.vue'
import PlayerBar from '@/components/PlayerBar.vue'
import { getDailyRecommendations } from '@/services/recommend'
import { playQueue } from '@/services/player'
import type { Track } from '@/types/music'

const tracks = ref<Track[]>([])
const loading = ref(true)
const loadError = ref(false)
const personalized = ref(false)
const dateLabel = ref('')
const refreshing = ref(false)

/** 把 yyyy-MM-dd 转为“M 月 D 日”。 */
function formatDate(date: string): string {
  const [year, month, day] = date.split('-')
  if (!year || !month || !day) return date
  return `${Number(month)} 月 ${Number(day)} 日`
}

/** 拉取每日推荐。 */
async function load() {
  loading.value = true
  loadError.value = false
  try {
    const result = await getDailyRecommendations(30)
    tracks.value = result.tracks
    personalized.value = result.personalized
    dateLabel.value = formatDate(result.date)
  } catch {
    loadError.value = true
  } finally {
    loading.value = false
  }
}

/** 播放全部（以当前列表为队列）。 */
function playAll() {
  if (tracks.value.length) playQueue(tracks.value, 0)
}

onLoad(() => {
  void load()
})

onPullDownRefresh(async () => {
  refreshing.value = true
  await load()
  refreshing.value = false
  uni.stopPullDownRefresh()
})
</script>

<template>
  <view class="daily-page">
    <!-- 顶部推荐头图 -->
    <view class="daily-hero">
      <view class="hero-icon"><Sparkles :size="26" /></view>
      <view class="hero-copy">
        <text class="hero-title">每日推荐</text>
        <text class="hero-sub">
          {{ dateLabel ? dateLabel + ' · ' : '' }}{{ personalized ? '根据你的收听口味' : '热门与新鲜好歌' }}
        </text>
      </view>
      <view class="hero-play" :class="{ disabled: !tracks.length }" @tap="playAll">
        <Play :size="20" fill="currentColor" />
        <text>播放</text>
      </view>
    </view>

    <!-- 状态：加载中 / 失败 / 列表 -->
    <view v-if="loading" class="state-text">正在为你挑选…</view>
    <view v-else-if="loadError" class="state-block">
      <text class="state-text">暂时无法获取推荐，请稍后再试。</text>
      <view class="retry-button" @tap="load"><RefreshCw :size="17" /><text>重试</text></view>
    </view>
    <template v-else>
      <view v-if="tracks.length" class="list-head"><text class="list-count">共 {{ tracks.length }} 首</text></view>
      <TrackRow
        v-for="(track, index) in tracks"
        :key="track.id"
        :track="track"
        :index="index"
        :queue="tracks"
      />
      <view v-if="!tracks.length" class="state-text">曲库还比较安静，稍后再来看看。</view>
      <view v-else class="list-end">每天为你更新 30 首</view>
    </template>
    <view class="page-bottom-spacer"></view>
  </view>

  <!-- 全局播放条 -->
  <PlayerBar />
</template>

<style lang="scss" scoped>
/* 每日推荐页（沿用 --sm-* 主题令牌） */
.daily-page { min-height: 100vh; padding: 24rpx 40rpx 60rpx; background: var(--sm-page-bg); box-sizing: border-box; }
.daily-hero { display: flex; align-items: center; gap: 20rpx; padding: 30rpx 28rpx; border: 1rpx solid var(--sm-border-strong); border-radius: var(--sm-radius-card); background: var(--sm-surface-3); box-shadow: 0 16rpx 40rpx var(--sm-art-shadow); }
.hero-icon { display: grid; place-items: center; width: 78rpx; height: 78rpx; border-radius: 24rpx; background: var(--sm-accent-soft); color: var(--sm-accent); }
.hero-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 8rpx; }
.hero-title { color: var(--sm-text); font-size: 32rpx; font-weight: 800; }
.hero-sub { color: var(--sm-text-2); font-size: 17rpx; }
.hero-play { display: flex; align-items: center; gap: 8rpx; padding: 15rpx 24rpx; border-radius: var(--sm-radius-pill); background: var(--sm-hero-ink-bg); color: var(--sm-hero-ink); font-size: 18rpx; font-weight: 700; }
.hero-play.disabled { opacity: .45; }
.state-text { padding: 80rpx 20rpx; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }
.state-block { display: flex; flex-direction: column; align-items: center; gap: 20rpx; }
.retry-button { display: flex; align-items: center; gap: 8rpx; padding: 14rpx 28rpx; border: 1rpx solid var(--sm-border-strong); border-radius: var(--sm-radius-pill); color: var(--sm-text); font-size: 18rpx; }
.list-head { padding: 26rpx 0 14rpx; }
.list-count { color: var(--sm-text-2); font-size: 18rpx; }
.list-end { padding: 36rpx 0 20rpx; color: var(--sm-text-3); font-size: 17rpx; text-align: center; }
.page-bottom-spacer { height: 180rpx; }
</style>
