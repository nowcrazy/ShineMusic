<script setup lang="ts">
/**
 * TrackRow：通用曲目行组件（方向 A · 呼吸感大卡片）
 * 用于首页推荐/搜索结果、专辑/艺人/专题详情、歌单等所有曲目列表。
 * 方向 A 改动：封面放大到 96rpx、行高加大到 130rpx、卡片化（背景+圆角+无硬分隔线）
 */
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Heart, MoreHorizontal } from 'lucide-vue-next'
import type { Track } from '@/types/music'
import { playTrack, usePlayer } from '@/services/player'
import { favoriteIds, toggleFavorite } from '@/services/library'
import { clearFeedback, feedbackMap, setFeedback } from '@/services/feedback'
import { isAuthenticated, requireLogin } from '@/services/session'
import { formatTime } from '@/utils/format'
import CoverArt from './CoverArt.vue'

const { t } = useI18n()

const props = withDefaults(
  defineProps<{
    track: Track
    index?: number
    queue?: readonly Track[]
    showHeart?: boolean
  }>(),
  { index: undefined, queue: undefined, showHeart: true },
)

const subtitle = computed(() =>
  props.track.album ? `${props.track.artist} · ${props.track.album}` : props.track.artist,
)
const notInterested = computed(() => feedbackMap.value.get(props.track.id) === 'not_interested')
const liking = ref(false)
const { state } = usePlayer()
const selected = computed(() => state.currentTrack?.id === props.track.id)

function handlePlay() {
  playTrack(props.track, props.queue)
}

function handleComment() {
  uni.navigateTo({ url: `/pages/track/detail?id=${props.track.id}` })
}

async function handleLike() {
  if (liking.value) return
  if (!isAuthenticated.value) { requireLogin(); return }
  liking.value = true
  try { await toggleFavorite(props.track) }
  catch { uni.showToast({ title: t('trackRow.operationFailed'), icon: 'none' }) }
  finally { liking.value = false }
}

function handleMore() {
  const actions = [
    ...(props.showHeart ? [{ label: favoriteIds.value.has(props.track.id) ? t('trackRow.removeFavorite') : t('trackRow.addFavorite'), run: handleLike }] : []),
    { label: t('player.comments'), run: handleComment },
    { label: notInterested.value ? t('trackRow.cancelNotInterested') : t('trackRow.notInterested'), run: handleFeedback },
  ]
  uni.showActionSheet({ itemList: actions.map((action) => action.label), success: ({ tapIndex }) => { void actions[tapIndex]?.run() } })
}
async function handleFeedback() {
  if (!requireLogin()) return
  try {
    if (notInterested.value) {
      await clearFeedback(props.track.id)
      uni.showToast({ title: t('trackRow.recommendRestored'), icon: 'none' })
    } else {
      await setFeedback(props.track.id, 'not_interested')
      uni.showToast({ title: t('trackRow.lessLikeThis'), icon: 'none' })
    }
  } catch { uni.showToast({ title: t('trackRow.operationFailed'), icon: 'none' }) }
}
</script>

<template>
  <view class="tr-row" :class="{ muted: notInterested, selected }">
    <button class="tr-main" :aria-label="t('homePage.play') + ' ' + track.title" @tap="handlePlay">
      <text v-if="index !== undefined" class="tr-index">{{ String(index + 1).padStart(2, '0') }}</text>
      <view class="tr-cover"><CoverArt :src="track.cover" :seed="track.title" :rounded="6" :icon-size="20" /></view>
      <view class="tr-copy"><text class="tr-title">{{ track.title }}</text><text class="tr-artist">{{ subtitle }}</text></view>
    </button>
    <text class="tr-duration">{{ formatTime(track.duration) }}</text>
    <button v-if="showHeart" class="tr-like" :class="{ liked: favoriteIds.has(track.id) }" :disabled="liking" :aria-pressed="favoriteIds.has(track.id)" :aria-label="t(favoriteIds.has(track.id) ? 'trackRow.removeFavorite' : 'trackRow.addFavorite') + ' ' + track.title" @tap="handleLike"><Heart :size="17" :fill="favoriteIds.has(track.id) ? 'currentColor' : 'none'" /></button>
    <button class="tr-more" :aria-label="t('homePage.more') + ' ' + track.title" @tap="handleMore"><MoreHorizontal :size="18" /></button>
  </view>
</template>

<style lang="scss" scoped>
.tr-row { display: flex; align-items: center; gap: 4px; min-width: 0; padding: 12px 0; border-bottom: 1px solid var(--sm-divider); }
button { margin: 0; padding: 0; background: transparent; color: inherit; line-height: 1.5; }
button::after { border: 0; }
.tr-main { display: flex; align-items: center; gap: 12px; flex: 1; min-width: 0; text-align: left; border-radius: 6px; }
.tr-index { flex: none; width: 20px; font: italic 18px Georgia, serif; color: var(--sm-text-3); }
.tr-cover { width: 48px; height: 48px; flex: none; }
.tr-copy { flex: 1; min-width: 0; }
.tr-title { display: block; color: var(--sm-text); font-size: 13px; font-weight: 600; overflow-wrap: anywhere; }
.tr-artist { display: block; margin-top: 4px; font-size: 11px; color: var(--sm-text-2); overflow-wrap: anywhere; }
.tr-duration { flex: none; padding-left: 4px; font-size: 10px; color: var(--sm-text-3); }
.tr-like, .tr-more { display: grid; place-items: center; flex: none; width: 36px; height: 40px; border-radius: 50%; color: var(--sm-text-3); }
.tr-like.liked, .selected .tr-title { color: var(--sm-accent); }
.muted { opacity: .6; }
.tr-row:hover { background: var(--sm-surface-2); }
button:focus-visible { outline: 2px solid var(--sm-accent); outline-offset: 2px; }
@media(max-width:360px) { .tr-main { gap: 8px; } .tr-cover { width: 40px; height: 40px; } .tr-like, .tr-more { width: 32px; } .tr-duration { display: none; } }
</style>
