<script setup lang="ts">
/**
 * 我的收藏页：已收藏曲目列表（红心曲库）。
 * 收藏状态来自 library 全局单例，进入时强制刷新一次，
 * 列表复用 TrackRow（自带红心/播放/不感兴趣能力）。
 */
import { computed, ref } from 'vue'
import { onLoad } from '@dcloudio/uni-app'
import { useI18n } from 'vue-i18n'
import { Heart } from 'lucide-vue-next'
import EmptyState from '@/components/EmptyState.vue'
import PlayerBar from '@/components/PlayerBar.vue'
import TrackRow from '@/components/TrackRow.vue'
import { useFavorites } from '@/services/library'
import { isAuthenticated } from '@/services/session'

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
        <view class="list-head"><text class="list-count">{{ t('catalog.trackCount', { count: favoriteTracks.length }) }}</text></view>
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
.list-head { padding: 8rpx 0 18rpx; }
.list-count { color: var(--sm-text-2); font-size: 18rpx; }
.page-bottom-spacer { height: 180rpx; }
</style>