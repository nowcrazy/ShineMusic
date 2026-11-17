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
import { useI18n } from 'vue-i18n'
import { Play, RefreshCw, Sparkles } from 'lucide-vue-next'
import TrackRow from '@/components/TrackRow.vue'
import PlayerBar from '@/components/PlayerBar.vue'
import CoverArt from '@/components/CoverArt.vue'
import { getDailyRecommendations } from '@/services/recommend'
import { playQueue } from '@/services/player'
import type { Track } from '@/types/music'

const { t } = useI18n()

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
  return t('daily.date', { month: Number(month), day: Number(day) })
}

/** 拉取每日推荐。 */
async function load() {
  loading.value = true
  loadError.value = false
  try {
    const result = await getDailyRecommendations()
    tracks.value = result.tracks
    personalized.value = result.personalized
    dateLabel.value = formatDate(result.date)
  } catch (error) {
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
      <view class="hero-art"><CoverArt :src="tracks[0]?.cover || ''" :seed="t('daily.title')" :rounded="8" :icon-size="36" /></view>
      <view class="hero-copy">
        <text class="hero-title">{{ t('daily.title') }}</text>
        <text class="hero-sub">
          {{ dateLabel ? dateLabel + ' · ' : '' }}{{ personalized ? t('daily.personalizedSub') : t('daily.freshSub') }}
        </text>
      </view>
      <button v-if="!loading && !loadError && tracks.length" class="hero-play" @tap="playAll">
        <Play :size="20" fill="currentColor" />
        <text>{{ t('daily.play') }}</text>
      </button>
    </view>

    <!-- 状态：加载中 / 失败 / 列表 -->
    <view v-if="loading" class="state-text">{{ t('daily.picking') }}</view>
    <view v-else-if="loadError" class="state-block">
      <text class="state-text">{{ t('daily.loadError') }}</text>
      <button class="retry-button" @tap="load"><RefreshCw :size="17" /><text>{{ t('daily.retry') }}</text></button>
    </view>
    <template v-else>
      <view v-if="tracks.length" class="list-head"><text class="list-count">{{ t('daily.totalTracks', { count: tracks.length }) }}</text></view>
      <TrackRow
        v-for="(track, index) in tracks"
        :key="track.id"
        :track="track"
        :index="index"
        :queue="tracks"
      />
      <view v-if="!tracks.length" class="state-text">{{ t('daily.quiet') }}</view>
    </template>
    <view class="page-bottom-spacer"></view>
  </view>

  <!-- 全局播放条 -->
  <PlayerBar />
</template>

<style lang="scss" scoped>
/* 每日推荐页（沿用 --sm-* 主题令牌） */
.daily-page { min-height: 100vh; padding: 24rpx 40rpx 60rpx; background: var(--sm-page-bg); box-sizing: border-box; }
.daily-hero { display: flex; align-items: center; gap: 24rpx; padding: 40rpx 32rpx; border: 1px solid var(--sm-accent-soft-border); border-radius: var(--sm-radius-card); background: var(--sm-accent-soft); box-shadow: none; }
.hero-icon { display: grid; place-items: center; width: 78rpx; height: 78rpx; border-radius: 24rpx; background: var(--sm-accent-soft); color: var(--sm-accent); }
.hero-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 8rpx; }
.hero-title { color: var(--sm-text); font-size: 40rpx; font-weight: 800; }
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
.daily-page { padding: 28px 24px 80px; max-width: 940px; margin: auto; }
.daily-hero { display: grid; grid-template-columns: 112px minmax(0,1fr); gap: 12px 20px; padding: 0; background: transparent; border: 0; }
.hero-art { width: 112px; height: 112px; grid-row: span 2; }
.hero-title { font-size: 24px; line-height: 1.3; }
.hero-sub { font-size: 12px; line-height: 1.6; }
.hero-play { justify-self: start; margin: 0; padding: 10px 16px; font-size: 12px; line-height: 1.5; background: var(--sm-accent); color: var(--sm-accent-ink); }
.hero-play::after, .retry-button::after { border: 0; }
.list-head { padding: 24px 0 12px; }
.list-count { font-size: 12px; }
.state-text { font-size: 13px; }
@media(min-width:800px) { .daily-page { padding: 32px 32px 80px; } }
</style>
