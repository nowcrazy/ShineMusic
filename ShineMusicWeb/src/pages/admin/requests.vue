<script setup lang="ts">
/**
 * 管理端「需求管理」页：集中查看用户点歌需求，推进状态（纳入计划 / 开始处理 / 上传满足 / 拒绝）。
 *
 * 与用户端需求池的区别：
 * - 数据来自 GET /admin/song-requests（可按状态过滤，含 declined/withdrawn 等终态）；
 * - 可直接推进状态或拒绝（拒绝必须填写原因，会记录 AdminNote）；
 * - 「上传并满足」跳转导入页并携带 songRequestId，发布后自动满足该需求。
 *
 * 访问控制：onShow 校验 isAdmin，非管理员 redirectTo 首页。
 */
import { ref } from 'vue'
import { onShow } from '@dcloudio/uni-app'
import { useI18n } from 'vue-i18n'
import { ChevronUp, Inbox, Play, RotateCcw, ShieldCheck, Upload, X } from 'lucide-vue-next'
import { isAdmin } from '@/services/session'
import { getAdminSongRequests, reopenSongRequest, updateSongRequestStatus } from '@/services/admin-requests'
import { SONG_REQUEST_STATUS } from '@/services/requests'
import { ApiError } from '@/services/http'
import { formatDate } from '@/utils/format'
import type { ApiSongRequest, SongRequestStatus } from '@/types/music'

// 状态筛选：空字符串表示全部（labelKey 为展示层 i18n key，key 仍是过滤逻辑用的状态枚举）
const { t } = useI18n()
type Filter = '' | SongRequestStatus
const filters: { key: Filter; labelKey: string }[] = [
  { key: '', labelKey: 'requestsAdmin.filterAll' },
  { key: 'open', labelKey: 'requestsAdmin.filterOpen' },
  { key: 'planned', labelKey: 'requestsAdmin.filterPlanned' },
  { key: 'in_progress', labelKey: 'requestsAdmin.filterInProgress' },
  { key: 'fulfilled', labelKey: 'requestsAdmin.filterFulfilled' },
  { key: 'declined', labelKey: 'requestsAdmin.filterDeclined' },
  { key: 'withdrawn', labelKey: 'requestsAdmin.filterWithdrawn' },
]

const filter = ref<Filter>('')
const items = ref<readonly ApiSongRequest[]>([])
const loading = ref(true)
const loadError = ref(false)
const busyId = ref('')

/** 加载需求队列 */
async function load() {
  loading.value = true
  loadError.value = false
  try {
    const result = await getAdminSongRequests({ status: filter.value || undefined, pageSize: 50 })
    items.value = result.items
  } catch {
    loadError.value = true
  } finally {
    loading.value = false
  }
}

onShow(() => {
  if (!isAdmin.value) {
    uni.redirectTo({ url: '/pages/home/index' })
    return
  }
  void load()
})

function switchFilter(next: Filter) {
  filter.value = next
  void load()
}

/** 安全读取状态元信息 */
function statusMeta(status: SongRequestStatus) {
  return SONG_REQUEST_STATUS[status] ?? { label: status, tone: 'open' }
}

/** 推进状态 / 拒绝 */
async function patchStatus(item: ApiSongRequest, status: SongRequestStatus, adminNote?: string) {
  if (busyId.value) return
  busyId.value = item.id
  try {
    await updateSongRequestStatus(item.id, { status, adminNote })
    uni.showToast({ title: t('requestsAdmin.updated'), icon: 'none' })
    await load()
  } catch (error) {
    uni.showToast({ title: error instanceof ApiError ? error.message : t('requestsAdmin.actionFailed'), icon: 'none' })
  } finally {
    busyId.value = ''
  }
}

function handlePlan(item: ApiSongRequest) {
  void patchStatus(item, 'planned')
}

function handleStart(item: ApiSongRequest) {
  void patchStatus(item, 'in_progress')
}

/** 拒绝：必须填写原因（后端同样强制） */
function handleDecline(item: ApiSongRequest) {
  uni.showModal({
    title: t('requestsAdmin.declineTitle'),
    content: t('requestsAdmin.declineConfirm', { title: item.title }),
    editable: true,
    placeholderText: t('requestsAdmin.declinePlaceholder'),
    confirmText: t('requestsAdmin.decline'),
    confirmColor: '#d0553a',
    success: (res) => {
      if (!res.confirm) return
      const note = ((res as unknown as { content?: string }).content ?? '').trim()
      if (!note) {
        uni.showToast({ title: t('requestsAdmin.declineReasonRequired'), icon: 'none' })
        return
      }
      void patchStatus(item, 'declined', note)
    },
  })
}

/** 跳转导入页并携带需求信息，上传发布后自动满足 */
function handleFulfill(item: ApiSongRequest) {
  const params = [
    `songRequestId=${encodeURIComponent(item.id)}`,
    `title=${encodeURIComponent(item.title)}`,
    `artistName=${encodeURIComponent(item.artistName)}`,
  ]
  if (item.albumTitle) params.push(`albumTitle=${encodeURIComponent(item.albumTitle)}`)
  uni.navigateTo({ url: `/pages/admin/import?${params.join('&')}` })
}

/**
 * 重新开启已结束的需求（declined/withdrawn → open）。
 * 原因必填：后端写入时间线并通知提交人，原拒绝/撤回原因作为历史保留。
 */
function handleReopen(item: ApiSongRequest) {
  if (busyId.value) return
  uni.showModal({
    title: t('requestsAdmin.reopenTitle'),
    content: t('requestsAdmin.reopenConfirm', { title: item.title }),
    editable: true,
    placeholderText: t('requestsAdmin.reopenPlaceholder'),
    confirmText: t('requestsAdmin.reopen'),
    success: async (res) => {
      if (!res.confirm) return
      const reason = ((res as unknown as { content?: string }).content ?? '').trim()
      if (!reason) {
        uni.showToast({ title: t('requestsAdmin.reopenReasonRequired'), icon: 'none' })
        return
      }
      busyId.value = item.id
      try {
        await reopenSongRequest(item.id, reason)
        uni.showToast({ title: t('requestsAdmin.reopened'), icon: 'none' })
        await load()
      } catch (error) {
        uni.showToast({ title: error instanceof ApiError ? error.message : t('requestsAdmin.actionFailed'), icon: 'none' })
      } finally {
        busyId.value = ''
      }
    },
  })
}
</script>

<template>
  <scroll-view class="admin-requests" scroll-y :show-scrollbar="false">
    <!-- 页头 -->
    <view class="page-heading">
      <view class="heading-icon"><ShieldCheck :size="20" /></view>
      <view class="heading-copy">
        <text class="heading-title">{{ $t('requestsAdmin.pageTitle') }}</text>
        <text class="heading-subtitle">{{ $t('requestsAdmin.subtitle') }}</text>
      </view>
    </view>

    <!-- 状态筛选 -->
    <view class="filter-row">
      <view
        v-for="entry in filters"
        :key="entry.key"
        class="filter-chip"
        :class="{ active: filter === entry.key }"
        @click="switchFilter(entry.key)"
      >
        <text>{{ $t(entry.labelKey) }}</text>
      </view>
    </view>

    <view v-if="loading" class="state-text">{{ $t('adminCommon.loading') }}</view>
    <view v-else-if="loadError" class="state-text">{{ $t('requestsAdmin.loadError') }}</view>
    <view v-else-if="!items.length" class="state-empty">
      <Inbox :size="28" />
      <text>{{ $t('requestsAdmin.empty') }}</text>
    </view>

    <view v-else class="request-list">
      <view v-for="item in items" :key="item.id" class="request-card">
        <view class="card-head">
          <view class="card-title-wrap">
            <text class="card-title">{{ item.title }}</text>
            <text class="card-artist">
              {{ item.artistName }}{{ item.albumTitle ? ` · ${item.albumTitle}` : '' }}
            </text>
          </view>
          <text class="status-badge" :class="`status-${statusMeta(item.status).tone}`">
            {{ statusMeta(item.status).label }}
          </text>
        </view>

        <view class="card-meta">
          <view class="vote-pill"><ChevronUp :size="14" /><text>{{ item.voteCount }}</text></view>
          <text class="meta-date">{{ formatDate(item.createdAt) }}</text>
        </view>

        <!-- 仅未结束的需求显示操作 -->
        <view
          v-if="item.status === 'open' || item.status === 'planned' || item.status === 'in_progress'"
          class="card-actions"
        >
          <view v-if="item.status === 'open'" class="act-btn" @click="handlePlan(item)">
            <Play :size="14" /><text>{{ $t('requestsAdmin.plan') }}</text>
          </view>
          <view v-if="item.status === 'planned'" class="act-btn" @click="handleStart(item)">
            <Play :size="14" /><text>{{ $t('requestsAdmin.start') }}</text>
          </view>
          <view class="act-btn primary" @click="handleFulfill(item)">
            <Upload :size="14" /><text>{{ $t('requestsAdmin.fulfill') }}</text>
          </view>
          <view class="act-btn danger" @click="handleDecline(item)">
            <X :size="14" /><text>{{ $t('requestsAdmin.decline') }}</text>
          </view>
        </view>

        <!-- 已拒绝/已撤回的需求可重新开启（补到音源后回补 / 误撤回恢复） -->
        <view v-else-if="item.status === 'declined' || item.status === 'withdrawn'" class="card-actions">
          <view class="act-btn primary" @click="handleReopen(item)">
            <RotateCcw :size="14" /><text>{{ $t('requestsAdmin.reopen') }}</text>
          </view>
        </view>
      </view>
    </view>

    <view class="bottom-space"></view>
  </scroll-view>
</template>

<style lang="scss" scoped>
.admin-requests { min-height: 100vh; padding: 30rpx 40rpx 60rpx; box-sizing: border-box; background: var(--sm-page-bg); }
.page-heading { display: flex; align-items: center; gap: 16rpx; padding: 10rpx 0 24rpx; }
.heading-icon { display: grid; place-items: center; width: 64rpx; height: 64rpx; border-radius: 18rpx; background: var(--sm-accent-soft); color: var(--sm-accent); }
.heading-copy { display: flex; flex-direction: column; }
.heading-title { color: var(--sm-text); font-size: 29rpx; font-weight: 800; }
.heading-subtitle { margin-top: 6rpx; color: var(--sm-text-2); font-size: 16rpx; }
.filter-row { display: flex; flex-wrap: wrap; gap: 12rpx; padding-bottom: 22rpx; }
.filter-chip { padding: 12rpx 24rpx; border: 1rpx solid var(--sm-border); border-radius: 32rpx; background: var(--sm-surface); color: var(--sm-text-2); font-size: 17rpx; }
.filter-chip.active { border-color: transparent; background: var(--sm-accent); color: var(--sm-accent-ink); font-weight: 700; }
.state-text { padding: 90rpx 20rpx; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }
.state-empty { display: flex; flex-direction: column; align-items: center; gap: 16rpx; padding: 100rpx 20rpx; color: var(--sm-text-3); font-size: 18rpx; }
.request-list { display: flex; flex-direction: column; gap: 16rpx; }
.request-card { padding: 24rpx; border: 1rpx solid var(--sm-border); border-radius: 20rpx; background: var(--sm-surface); }
.card-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 16rpx; }
.card-title-wrap { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 6rpx; }
.card-title { color: var(--sm-text); font-size: 22rpx; font-weight: 700; }
.card-artist { color: var(--sm-text-2); font-size: 17rpx; }
.status-badge { flex: none; padding: 4rpx 14rpx; border-radius: 20rpx; font-size: 14rpx; font-weight: 700; }
.status-open { background: var(--sm-accent-soft); color: var(--sm-accent); }
.status-planned { background: var(--sm-info-soft); color: var(--sm-accent-2); }
.status-progress { background: var(--sm-warn-soft); color: var(--sm-warn); }
.status-fulfilled { background: var(--sm-accent-soft); color: var(--sm-accent-2); }
.status-declined { background: var(--sm-danger-soft); color: var(--sm-danger); }
.status-withdrawn { background: var(--sm-divider); color: var(--sm-text-3); }
.card-meta { display: flex; align-items: center; gap: 16rpx; margin-top: 12rpx; }
.vote-pill { display: inline-flex; align-items: center; gap: 2rpx; color: var(--sm-accent); font-size: 16rpx; font-weight: 700; }
.meta-date { color: var(--sm-text-3); font-size: 14rpx; }
.card-actions { display: flex; flex-wrap: wrap; gap: 12rpx; margin-top: 18rpx; }
.act-btn { display: inline-flex; align-items: center; gap: 6rpx; padding: 10rpx 22rpx; border: 1rpx solid var(--sm-border); border-radius: 24rpx; background: var(--sm-surface); color: var(--sm-text-2); font-size: 15rpx; font-weight: 700; }
.act-btn.primary { border-color: transparent; background: var(--sm-accent); color: var(--sm-accent-ink); }
.act-btn.danger { border-color: transparent; background: var(--sm-danger-soft); color: var(--sm-danger); }
.bottom-space { height: 40rpx; }
</style>
