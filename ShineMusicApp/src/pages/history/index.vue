<script setup lang="ts">
/**
 * 播放历史页：按曲目聚类的最近收听记录（最近收听时间 + 累计播放次数）。
 * 数据来自 GET /me/history（鉴权），复用 TrackRow 渲染，支持分页加载。
 */
import { computed, ref } from 'vue'
import { onLoad, onReachBottom } from '@dcloudio/uni-app'
import { useI18n } from 'vue-i18n'
import { History as HistoryIcon } from 'lucide-vue-next'
import EmptyState from '@/components/EmptyState.vue'
import PlayerBar from '@/components/PlayerBar.vue'
import TrackRow from '@/components/TrackRow.vue'
import { getListeningHistory, type HistoryEntry } from '@/services/history'
import { formatDate } from '@/utils/format'
import type { Track } from '@/types/music'

const { t } = useI18n()

const entries = ref<HistoryEntry[]>([])
const loading = ref(true)
const loadError = ref(false)
const total = ref(0)
const page = ref(1)
const pageSize = 20

const tracks = computed<Track[]>(() => entries.value.map((entry) => entry.track))
const hasMore = computed(() => entries.value.length < total.value)

async function load(reset = false) {
  if (reset) {
    loading.value = true
    page.value = 1
  }
  loadError.value = false
  try {
    const result = await getListeningHistory(reset ? 1 : page.value, pageSize)
    entries.value = reset ? result.items : [...entries.value, ...result.items]
    total.value = result.total
    page.value += 1
  } catch {
    loadError.value = true
  } finally {
    loading.value = false
  }
}

function loadMore() {
  if (hasMore.value && !loading.value) void load()
}

function historyLabel(iso: string): string {
  const date = formatDate(iso)
  return date ? t('history.recentWithDate', { date }) : t('history.recent')
}

onLoad(() => void load(true))
onReachBottom(loadMore)
</script>

<template>
  <view class="history-page">
    <view v-if="loading" class="page-state">{{ t('history.loading') }}</view>
    <view v-else-if="loadError" class="page-state">{{ t('history.loadError') }}</view>
    <template v-else>
      <EmptyState
        v-if="!entries.length"
        :icon="HistoryIcon"
        :title="t('history.empty')"
        :hint="t('history.emptyHint')"
      />
      <template v-else>
        <view class="list-head"><text class="list-count">{{ t('history.totalCount', { count: total }) }}</text></view>
        <view v-for="entry in entries" :key="entry.track.id" class="history-item">
          <view class="hi-head">
            <text class="hi-date">{{ historyLabel(entry.lastPlayedAt) }}</text>
            <text class="hi-count">{{ t('history.playCount', { count: entry.playCount }) }}</text>
          </view>
          <TrackRow :track="entry.track" :queue="tracks" />
        </view>
        <view v-if="hasMore" class="load-more" @tap="loadMore">{{ t('history.loadMore') }}</view>
      </template>
    </template>
    <view class="page-bottom-spacer"></view>
  </view>
  <PlayerBar />
</template>

<style lang="scss" scoped>
.history-page { min-height: 100vh; padding: 24rpx 40rpx 60rpx; background: var(--sm-page-bg); box-sizing: border-box; }
.page-state { padding: 100rpx 20rpx; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }
.list-head { padding: 8rpx 0 18rpx; }
.list-count { color: var(--sm-text-2); font-size: 18rpx; }
.history-item { margin-bottom: 28rpx; }
.hi-head { display: flex; align-items: center; justify-content: space-between; padding: 0 6rpx 12rpx; }
.hi-date { color: var(--sm-text); font-size: 23rpx; font-weight: 700; }
.hi-count { color: var(--sm-text-3); font-size: 17rpx; }
.load-more { padding: 30rpx 0 10rpx; color: var(--sm-accent); font-size: 18rpx; text-align: center; }
.page-bottom-spacer { height: 180rpx; }
</style>