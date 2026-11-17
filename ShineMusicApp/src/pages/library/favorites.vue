<script setup lang="ts">
/**
 * 我的收藏页：已收藏曲目列表（红心曲库）。
 * 收藏状态来自 library 全局单例，进入时强制刷新一次，
 * 列表复用 TrackRow（自带红心/播放/不感兴趣能力）。
 */
import { ref } from 'vue'
import { onLoad } from '@dcloudio/uni-app'
import { useI18n } from 'vue-i18n'
import { Heart, Play } from 'lucide-vue-next'
import EmptyState from '@/components/EmptyState.vue'
import PlayerBar from '@/components/PlayerBar.vue'
import TrackRow from '@/components/TrackRow.vue'
import { useFavorites } from '@/services/library'
import { isAuthenticated } from '@/services/session'
import { playQueue } from '@/services/player'

const { t } = useI18n()

const { favoriteTracks, loadFavorites } = useFavorites()
const loading = ref(true)
const loadError = ref(false)

async function load() {
  if (!isAuthenticated.value) {
    loading.value = false
    return
  }
  loading.value = true
  loadError.value = false
  try {
    await loadFavorites(true)
  } catch {
    loadError.value = true
  } finally {
    loading.value = false
  }
}

function goLogin() {
  uni.navigateTo({ url: '/pages/auth/login' })
}

function playAll() {
  if (isAuthenticated.value && !loading.value && !loadError.value && favoriteTracks.value.length) {
    playQueue(favoriteTracks.value)
  }
}

onLoad(() => void load())
</script>

<template>
  <view class="favorites-page">
    <view v-if="loading" class="page-state">{{ t('libraryPage.favLoading') }}</view>
    <view v-else-if="loadError" class="page-state">{{ t('libraryPage.favLoadError') }}</view>
    <template v-else>
      <EmptyState
        v-if="!isAuthenticated"
        :icon="Heart"
        :title="t('libraryPage.favLoginTitle')"
        :hint="t('libraryPage.favLoginCopy')"
        :action-text="t('libraryPage.goLogin')"
        @action="goLogin"
      />
      <EmptyState
        v-else-if="!favoriteTracks.length"
        :icon="Heart"
        :title="t('libraryPage.favEmptyTitle')"
        :hint="t('libraryPage.favEmptyCopy')"
      />
      <template v-else>
        <view class="list-head">
          <text class="list-count">{{ t('catalog.trackCount', { count: favoriteTracks.length }) }}</text>
          <button class="play-all" @tap="playAll"><Play :size="16" fill="currentColor" />{{ t('catalogPage.playAll') }}</button>
        </view>
        <TrackRow
          v-for="(track, index) in favoriteTracks"
          :key="track.id"
          :track="track"
          :index="index"
          :queue="favoriteTracks"
        />
      </template>
    </template>
    <view class="page-bottom-spacer"></view>
  </view>
  <PlayerBar />
</template>

<style lang="scss" scoped>
.favorites-page { min-height: 100vh; padding: 24rpx 40rpx 60rpx; background: var(--sm-page-bg); box-sizing: border-box; }
.page-state { padding: 100rpx 20rpx; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }
.list-head { padding: 32rpx; margin-bottom: 32rpx; border-radius: var(--sm-radius-card); background: var(--sm-danger-soft); border: 1px solid var(--sm-danger-soft-border); }
.list-count { color: var(--sm-text); font-size: 28rpx; font-weight: 700; }
.list-head { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 20rpx; }
.play-all { display: flex; align-items: center; gap: 12rpx; margin: 0; padding: 12rpx 28rpx; background: var(--sm-accent); color: var(--sm-accent-ink); border-radius: var(--sm-radius-pill); font-size: 24rpx; }
.page-bottom-spacer { height: 180rpx; }
</style>