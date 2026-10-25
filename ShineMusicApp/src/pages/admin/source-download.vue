<script setup lang="ts">
/**
 * 管理员音源抓歌页：搜索开放版权平台（Audius/Jamendo）→ 勾选曲目下载 → 后台下载 + 评分 → 直通入库或转人工审核。
 *
 * 流程：
 * 1. 输入关键词搜索（可选定单一音源或全部聚合），结果标注音质徽章（无损/高码率/标准）与许可
 * 2. 勾选感兴趣的结果，点击「下载所选」批量创建下载任务（重复任务自动跳过）
 * 3. 任务列表轮询刷新：queued → downloading → import_queued → completed/failed
 *    - completed 且 autoPublished=true：评分达标已直通入库
 *    - completed 且 autoPublished=false：评分不足，已转人工审核（可跳导入审核页处理）
 *    - failed：可一键重试
 *
 * 访问控制：onShow 校验 isAdmin，非管理员 redirectTo 首页。
 */
import { computed, ref, onUnmounted } from 'vue'
import { onShow } from '@dcloudio/uni-app'
import { useI18n } from 'vue-i18n'
import {
  AlertCircle,
  Check,
  DownloadCloud,
  ListMusic,
  Music2,
  RefreshCw,
  RotateCcw,
  Search,
  ShieldCheck,
} from 'lucide-vue-next'
import { isAdmin } from '@/services/session'
import { describeError } from '@/services/errors'
import {
  createDownloadTasks,
  getDownloadTasks,
  retryDownloadTask,
  searchSources,
  SourceProviderLabel,
  SourceQualityLabel,
  SourceTaskStatus,
  SourceTaskStatusLabel,
  type ExternalDownloadTask,
  type SourceProviderFilter,
  type SourceProviderName,
  type SourceSearchItem,
} from '@/services/admin-source-download'

const { t } = useI18n()

// —— 搜索区状态 ——
const keyword = ref('')
const providerFilter = ref<SourceProviderFilter>('all')
const searching = ref(false)
const searchError = ref('')
const searched = ref(false)
const results = ref<readonly SourceSearchItem[]>([])
const providerErrors = ref<readonly { provider: string; message: string }[]>([])
/** 已勾选的搜索结果（provider::providerTrackId 作为唯一键） */
const selectedKeys = ref<string[]>([])

// —— 任务区状态 ——
const tasks = ref<readonly ExternalDownloadTask[]>([])
const tasksLoading = ref(false)
const tasksError = ref('')
const statusFilter = ref('')
const creating = ref(false)
const retryingId = ref('')

// 自动刷新定时器
let pollTimer: ReturnType<typeof setInterval> | null = null

const providerOptions: { value: SourceProviderFilter; label: string }[] = [
  { value: 'all', label: 'sourceDownload.providerAll' },
  { value: 'audius', label: 'Audius' },
  { value: 'jamendo', label: 'Jamendo' },
  { value: 'ytmusic', label: 'YT Music' },
  { value: 'soundcloud', label: 'SoundCloud' },
  { value: 'kuwo', label: 'Kuwo' },
]

const statusFilters = ['', SourceTaskStatus.Queued, SourceTaskStatus.Downloading, SourceTaskStatus.ImportQueued, SourceTaskStatus.Completed, SourceTaskStatus.Failed]

/** 结果唯一键（跨音源同 id 不冲突） */
function resultKey(item: SourceSearchItem): string {
  return `${item.provider}::${item.providerTrackId}`
}

/** 已勾选数 */
const selectedCount = computed(() => selectedKeys.value.length)

/** 是否存在活跃任务（决定轮询是否值得继续打接口） */
const hasActiveTasks = computed(() =>
  tasks.value.some((task) =>
    task.status === SourceTaskStatus.Queued ||
    task.status === SourceTaskStatus.Downloading ||
    task.status === SourceTaskStatus.ImportQueued))

// 权限拦截
onShow(() => {
  if (!isAdmin.value) {
    uni.redirectTo({ url: '/pages/home/index' })
    return
  }
  void loadTasks()
  startPolling()
})

onUnmounted(() => {
  stopPolling()
})

/** 轮询任务列表：有活跃任务时每 3 秒静默刷新，全部终态后停止 */
function startPolling() {
  stopPolling()
  pollTimer = setInterval(() => {
    if (hasActiveTasks.value) void refreshTasksSilently()
  }, 3000)
}

function stopPolling() {
  if (pollTimer) {
    clearInterval(pollTimer)
    pollTimer = null
  }
}

/** 执行搜索 */
async function doSearch() {
  const query = keyword.value.trim()
  if (!query || searching.value) return
  searching.value = true
  searchError.value = ''
  searched.value = false
  try {
    const response = await searchSources(query, providerFilter.value)
    results.value = response.results
    providerErrors.value = response.providerErrors
    searched.value = true
    // 音源切换后清掉已失效的勾选
    const validKeys = new Set(response.results.map((item) => resultKey(item)))
    selectedKeys.value = selectedKeys.value.filter((key) => validKeys.has(key))
  } catch (error) {
    searchError.value = describeError(error, t('sourceDownload.searchFailed'))
  } finally {
    searching.value = false
  }
}

/** 切换音源后自动重搜（已有关键词时） */
function switchProvider(value: SourceProviderFilter) {
  if (providerFilter.value === value) return
  providerFilter.value = value
  if (keyword.value.trim()) void doSearch()
}

/** 勾选/取消勾选 */
function toggleSelect(item: SourceSearchItem) {
  const key = resultKey(item)
  const index = selectedKeys.value.indexOf(key)
  if (index >= 0) {
    selectedKeys.value.splice(index, 1)
  } else {
    selectedKeys.value.push(key)
  }
}

function isSelected(item: SourceSearchItem): boolean {
  return selectedKeys.value.includes(resultKey(item))
}

/** 下载所选：按音源分组批量创建任务 */
async function downloadSelected() {
  if (creating.value || selectedKeys.value.length === 0) return
  creating.value = true
  try {
    // 按 provider 分组，逐组创建
    const groups = new Map<string, string[]>()
    for (const item of results.value) {
      if (!isSelected(item)) continue
      const list = groups.get(item.provider) ?? []
      list.push(item.providerTrackId)
      groups.set(item.provider, list)
    }
    let created = 0
    let skipped = 0
    for (const [provider, ids] of groups) {
      const result = await createDownloadTasks(provider as SourceProviderName, ids)
      created += result.created.length
      skipped += result.skippedDuplicates.length
    }
    selectedKeys.value = []
    if (created > 0) {
      uni.showToast({ title: t('sourceDownload.downloadCreated', { count: created }), icon: 'none' })
    } else if (skipped > 0) {
      uni.showToast({ title: t('sourceDownload.downloadSkipped', { count: skipped }), icon: 'none' })
    }
    await loadTasks()
  } catch (error) {
    uni.showToast({ title: describeError(error, t('sourceDownload.downloadFailed')), icon: 'none' })
  } finally {
    creating.value = false
  }
}

/** 加载任务列表 */
async function loadTasks() {
  tasksLoading.value = true
  tasksError.value = ''
  try {
    const result = await getDownloadTasks(statusFilter.value || undefined, 1, 50)
    tasks.value = result.items
  } catch (error) {
    tasksError.value = describeError(error, t('sourceDownload.loadTasksFailed'))
  } finally {
    tasksLoading.value = false
  }
}

/** 静默刷新任务（不打 loading，不报错） */
async function refreshTasksSilently() {
  try {
    const result = await getDownloadTasks(statusFilter.value || undefined, 1, 50)
    tasks.value = result.items
  } catch {
    // 静默失败
  }
}

/** 切换任务状态筛选 */
function switchStatusFilter(value: string) {
  if (statusFilter.value === value) return
  statusFilter.value = value
  void loadTasks()
}

/** 重试失败任务 */
async function retryTask(task: ExternalDownloadTask) {
  if (retryingId.value) return
  retryingId.value = task.id
  try {
    await retryDownloadTask(task.id)
    uni.showToast({ title: t('sourceDownload.retried'), icon: 'none' })
    await loadTasks()
  } catch (error) {
    uni.showToast({ title: describeError(error, t('sourceDownload.retryFailed')), icon: 'none' })
  } finally {
    retryingId.value = ''
  }
}

/** 跳转导入审核页（处理转人工的条目） */
function goImportReview() {
  uni.navigateTo({ url: '/pages/admin/import' })
}

/** 状态标签 */
function statusLabel(status: string): string {
  const key = SourceTaskStatusLabel[status]
  return key ? t(key) : status
}

/** 音质标签 */
function qualityLabel(quality: string | null | undefined): string {
  if (!quality) return ''
  const key = SourceQualityLabel[quality]
  return key ? t(key) : quality
}

/** 音源展示名（ytmusic → YT Music 等，未知名称回退原始 id） */
function providerLabel(provider: string): string {
  return SourceProviderLabel[provider] ?? provider
}

/** 状态样式 */
function statusClass(status: string): string {
  switch (status) {
    case SourceTaskStatus.Completed:
      return 'status-ok'
    case SourceTaskStatus.Failed:
      return 'status-error'
    case SourceTaskStatus.Downloading:
    case SourceTaskStatus.ImportQueued:
      return 'status-warn'
    default:
      return 'status-info'
  }
}

/** 格式化时长 */
function formatDuration(ms: number | null | undefined): string {
  if (!ms) return t('sourceDownload.unknownDuration')
  const totalSec = Math.round(ms / 1000)
  const min = Math.floor(totalSec / 60)
  const sec = totalSec % 60
  return `${min}:${sec.toString().padStart(2, '0')}`
}
</script>

<template>
  <scroll-view class="sd-page" scroll-y :show-scrollbar="false">
    <!-- 页头 -->
    <view class="page-heading">
      <view class="heading-icon"><DownloadCloud :size="20" /></view>
      <view class="heading-copy">
        <text class="heading-title">{{ $t('sourceDownload.pageTitle') }}</text>
        <text class="heading-subtitle">{{ $t('sourceDownload.headingSubtitle') }}</text>
      </view>
    </view>

    <!-- 搜索区 -->
    <view class="search-card">
      <view class="search-row">
        <view class="search-input-wrap">
          <Search :size="16" class="search-input-icon" />
          <input
            v-model="keyword"
            class="search-input"
            :placeholder="$t('sourceDownload.searchPlaceholder')"
            confirm-type="search"
            @confirm="doSearch"
          />
        </view>
        <view class="search-btn" :class="{ disabled: searching || !keyword.trim() }" @tap="doSearch">
          <text>{{ searching ? $t('sourceDownload.searching') : $t('sourceDownload.searchBtn') }}</text>
        </view>
      </view>
      <view class="provider-row">
        <view
          v-for="option in providerOptions"
          :key="option.value"
          class="provider-chip"
          :class="{ active: providerFilter === option.value }"
          @tap="switchProvider(option.value)"
        >
          <text>{{ option.label.startsWith('sourceDownload.') ? $t(option.label) : option.label }}</text>
        </view>
      </view>
    </view>

    <!-- 单源失败提示（不阻断其他源结果） -->
    <view v-if="providerErrors.length" class="provider-error-box">
      <AlertCircle :size="15" />
      <text>
        {{ $t('sourceDownload.providerErrorTitle') }}：
        {{ providerErrors.map((e) => `${e.provider} (${e.message})`).join('；') }}
      </text>
    </view>

    <!-- 搜索结果 -->
    <view v-if="searchError" class="state-message error"><AlertCircle :size="17" /><text>{{ searchError }}</text></view>
    <view v-else-if="!searched && !searching" class="state-message">
      <Search :size="26" />
      <text>{{ $t('sourceDownload.searchHint') }}</text>
    </view>
    <view v-else-if="searched && !results.length" class="state-message">
      <Music2 :size="26" />
      <text>{{ $t('sourceDownload.noResults') }}</text>
    </view>
    <view v-else class="result-list">
      <view
        v-for="item in results"
        :key="resultKey(item)"
        class="result-row"
        :class="{ selected: isSelected(item) }"
        @tap="toggleSelect(item)"
      >
        <view class="result-check">
          <view class="checkbox" :class="{ checked: isSelected(item) }">
            <Check v-if="isSelected(item)" :size="13" :stroke-width="3" />
          </view>
        </view>
        <view class="result-thumb">
          <image v-if="item.coverUrl" :src="item.coverUrl" mode="aspectFill" class="thumb-img" />
          <view v-else class="thumb-placeholder"><Music2 :size="18" /></view>
        </view>
        <view class="result-copy">
          <text class="result-title">{{ item.title || $t('sourceDownload.untitled') }}</text>
          <text class="result-meta">
            {{ item.artists.join(' / ') || $t('sourceDownload.unknownArtist') }}
            <text v-if="item.durationMs"> · {{ formatDuration(item.durationMs) }}</text>
          </text>
          <view class="result-badges">
            <text class="badge provider-badge">{{ providerLabel(item.provider) }}</text>
            <text class="badge" :class="`quality-${item.quality}`">{{ qualityLabel(item.quality) }}</text>
            <text v-if="item.license" class="badge license-badge">{{ item.license }}</text>
          </view>
        </view>
      </view>
    </view>

    <!-- 下载所选（吸底操作条） -->
    <view v-if="selectedCount > 0" class="download-bar">
      <text class="download-count">{{ $t('sourceDownload.selectedCount', { count: selectedCount }) }}</text>
      <view class="download-btn" :class="{ disabled: creating }" @tap="downloadSelected">
        <DownloadCloud :size="16" />
        <text>{{ $t('sourceDownload.downloadSelected') }}</text>
      </view>
    </view>

    <!-- 任务区 -->
    <view class="tasks-section">
      <view class="tasks-heading">
        <view class="tasks-title-wrap">
          <ListMusic :size="17" />
          <text class="tasks-title">{{ $t('sourceDownload.tasksTitle') }}</text>
        </view>
        <view class="refresh-button" @tap="loadTasks"><RefreshCw :size="16" /></view>
      </view>
      <view class="status-filter-row">
        <view
          v-for="value in statusFilters"
          :key="value || 'all'"
          class="status-chip"
          :class="{ active: statusFilter === value }"
          @tap="switchStatusFilter(value)"
        >
          <text>{{ value ? statusLabel(value) : $t('sourceDownload.filterAll') }}</text>
        </view>
      </view>

      <view v-if="tasksLoading" class="state-message">{{ $t('sourceDownload.loadingTasks') }}</view>
      <view v-else-if="tasksError" class="state-message error"><AlertCircle :size="17" /><text>{{ tasksError }}</text></view>
      <view v-else-if="!tasks.length" class="state-message">
        <Music2 :size="26" />
        <text>{{ $t('sourceDownload.noTasks') }}</text>
      </view>
      <view v-else class="task-list">
        <view v-for="task in tasks" :key="task.id" class="task-card">
          <view class="task-thumb">
            <image v-if="task.coverUrl" :src="task.coverUrl" mode="aspectFill" class="thumb-img" />
            <view v-else class="thumb-placeholder"><Music2 :size="18" /></view>
          </view>
          <view class="task-copy">
            <text class="task-title">{{ task.title || $t('sourceDownload.untitled') }}</text>
            <text class="task-meta">
              {{ task.artists.join(' / ') || $t('sourceDownload.unknownArtist') }}
              <text v-if="task.durationMs"> · {{ formatDuration(task.durationMs) }}</text>
            </text>
            <view class="task-badges">
              <text class="badge provider-badge">{{ providerLabel(task.provider) }}</text>
              <text v-if="task.quality" class="badge" :class="`quality-${task.quality}`">{{ qualityLabel(task.quality) }}</text>
              <text class="badge status-badge" :class="statusClass(task.status)">{{ statusLabel(task.status) }}</text>
              <text
                v-if="task.status === SourceTaskStatus.Queued && task.autoRetryCount > 0"
                class="badge retry-badge"
              >{{ $t('sourceDownload.autoRetryBadge', { count: task.autoRetryCount, max: 3 }) }}</text>
              <text v-if="task.autoScore != null" class="badge score-badge">{{ $t('sourceDownload.autoScoreLabel', { score: task.autoScore }) }}</text>
              <text
                v-if="task.status === SourceTaskStatus.Completed && task.hasLyrics === false"
                class="badge no-lyrics-badge"
              >{{ $t('sourceDownload.noLyricsBadge') }}</text>
              <text v-if="task.autoPublished" class="badge published-badge">
                <ShieldCheck :size="11" /> {{ $t('sourceDownload.autoPublishedBadge') }}
              </text>
              <text v-else-if="task.status === SourceTaskStatus.Completed && task.importItemId" class="badge review-badge">
                {{ $t('sourceDownload.pendingReviewBadge') }}
              </text>
            </view>
            <text v-if="task.errorMessage" class="task-error">{{ task.errorMessage }}</text>
          </view>
          <view class="task-actions">
            <view
              v-if="task.status === SourceTaskStatus.Failed"
              class="action-btn retry"
              :class="{ disabled: retryingId === task.id }"
              @tap.stop="retryTask(task)"
            >
              <RotateCcw :size="14" />
              <text>{{ $t('sourceDownload.retryBtn') }}</text>
            </view>
            <view
              v-if="task.importItemId && !task.autoPublished"
              class="action-btn review"
              @tap.stop="goImportReview"
            >
              <text>{{ $t('sourceDownload.goReview') }}</text>
            </view>
          </view>
        </view>
      </view>
    </view>
  </scroll-view>
</template>

<style lang="scss" scoped>
/* 音源抓歌管理页：搜索区 + 结果列表 + 吸底下载条 + 任务列表 */
.sd-page { min-height: 100vh; padding: 0 30rpx 220rpx; background: var(--sm-page-bg); box-sizing: border-box; }
.page-heading { display: flex; align-items: center; gap: 20rpx; padding: calc(env(safe-area-inset-top) + 40rpx) 8rpx 24rpx; }
.heading-icon { display: grid; place-items: center; width: 72rpx; height: 72rpx; border-radius: 22rpx; background: var(--sm-accent-soft); color: var(--sm-accent); }
.heading-copy { display: flex; flex-direction: column; gap: 6rpx; }
.heading-title { color: var(--sm-text); font-size: 34rpx; font-weight: 800; }
.heading-subtitle { color: var(--sm-text-3); font-size: 18rpx; }

/* 搜索卡片 */
.search-card { padding: 24rpx; border: 1rpx solid var(--sm-border); border-radius: 24rpx; background: var(--sm-surface); }
.search-row { display: flex; align-items: center; gap: 16rpx; }
.search-input-wrap { position: relative; display: flex; flex: 1; align-items: center; }
.search-input-icon { position: absolute; left: 22rpx; color: var(--sm-text-3); }
.search-input { flex: 1; height: 76rpx; padding: 0 22rpx 0 60rpx; border: 1rpx solid var(--sm-border); border-radius: 38rpx; background: var(--sm-surface-2); color: var(--sm-text); font-size: 22rpx; }
.search-btn { display: flex; align-items: center; justify-content: center; height: 76rpx; padding: 0 34rpx; border-radius: 38rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 22rpx; font-weight: 700; }
.search-btn.disabled { opacity: .5; }
.provider-row { display: flex; gap: 14rpx; margin-top: 18rpx; }
.provider-chip { padding: 12rpx 28rpx; border: 1rpx solid var(--sm-border); border-radius: 30rpx; background: var(--sm-surface-2); color: var(--sm-text-2); font-size: 20rpx; font-weight: 600; }
.provider-chip.active { border-color: var(--sm-accent-soft-border); background: var(--sm-accent-soft); color: var(--sm-accent); }

/* 音源错误提示 */
.provider-error-box { display: flex; align-items: center; gap: 12rpx; margin-top: 20rpx; padding: 18rpx 22rpx; border: 1rpx solid var(--sm-danger-soft-border); border-radius: 18rpx; background: var(--sm-danger-soft); color: var(--sm-danger); font-size: 18rpx; }

/* 状态消息 */
.state-message { display: flex; flex-direction: column; align-items: center; gap: 16rpx; padding: 70rpx 0; color: var(--sm-text-3); font-size: 22rpx; }
.state-message.error { flex-direction: row; justify-content: center; color: var(--sm-danger); }

/* 搜索结果列表 */
.result-list { display: flex; flex-direction: column; gap: 16rpx; margin-top: 24rpx; }
.result-row { display: flex; align-items: center; gap: 18rpx; padding: 18rpx; border: 1rpx solid var(--sm-border); border-radius: 22rpx; background: var(--sm-surface); transition: border-color .2s ease; }
.result-row.selected { border-color: var(--sm-accent); box-shadow: 0 0 0 2rpx var(--sm-accent-soft-border); }
.result-check { flex: none; }
.checkbox { display: grid; place-items: center; width: 40rpx; height: 40rpx; border: 2rpx solid var(--sm-border-strong); border-radius: 12rpx; background: var(--sm-surface-2); color: var(--sm-accent-ink); }
.checkbox.checked { border-color: var(--sm-accent); background: var(--sm-accent); }
.result-thumb { flex: none; width: 96rpx; height: 96rpx; border-radius: 16rpx; overflow: hidden; background: var(--sm-surface-3); }
.thumb-img { width: 100%; height: 100%; }
.thumb-placeholder { display: grid; place-items: center; width: 100%; height: 100%; color: var(--sm-text-3); }
.result-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 6rpx; }
.result-title { overflow: hidden; color: var(--sm-text); font-size: 24rpx; font-weight: 700; text-overflow: ellipsis; white-space: nowrap; }
.result-meta { overflow: hidden; color: var(--sm-text-2); font-size: 18rpx; text-overflow: ellipsis; white-space: nowrap; }
.result-badges { display: flex; flex-wrap: wrap; gap: 10rpx; margin-top: 4rpx; }

/* 徽章 */
.badge { display: inline-flex; align-items: center; gap: 5rpx; padding: 4rpx 14rpx; border-radius: 24rpx; font-size: 16rpx; font-weight: 700; }
.provider-badge { background: var(--sm-surface-3); color: var(--sm-text-2); text-transform: uppercase; letter-spacing: .06em; }
.quality-lossless { background: var(--sm-accent-soft); color: var(--sm-accent); }
.quality-high { background: var(--sm-info-soft); color: var(--sm-accent-2); }
.quality-standard { background: var(--sm-surface-3); color: var(--sm-text-3); }
.license-badge { background: var(--sm-surface-3); color: var(--sm-text-3); }
.status-badge.status-ok { background: var(--sm-accent-soft); color: var(--sm-accent); }
.status-badge.status-warn { background: var(--sm-info-soft); color: var(--sm-accent-2); }
.status-badge.status-error { background: var(--sm-danger-soft); color: var(--sm-danger); }
.status-badge.status-info { background: var(--sm-surface-3); color: var(--sm-text-2); }
.score-badge { background: var(--sm-accent-3-soft); color: var(--sm-accent-3); }
.no-lyrics-badge { background: var(--sm-warn-soft, rgba(230, 162, 60, 0.12)); color: var(--sm-warn, #b26a00); }
.retry-badge { background: var(--sm-info-soft); color: var(--sm-accent-2); }
.published-badge { background: var(--sm-accent-soft); color: var(--sm-accent); }
.review-badge { background: var(--sm-info-soft); color: var(--sm-accent-2); }

/* 吸底下载条 */
.download-bar { position: fixed; right: 30rpx; bottom: calc(env(safe-area-inset-bottom) + 30rpx); left: 30rpx; z-index: 50; display: flex; align-items: center; justify-content: space-between; padding: 20rpx 24rpx; border: 1rpx solid var(--sm-border-strong); border-radius: 26rpx; background: var(--sm-surface); box-shadow: 0 12rpx 40rpx rgba(0, 0, 0, .3); }
.download-count { color: var(--sm-text-2); font-size: 20rpx; font-weight: 600; }
.download-btn { display: flex; align-items: center; gap: 10rpx; padding: 16rpx 30rpx; border-radius: 34rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 21rpx; font-weight: 700; }
.download-btn.disabled { opacity: .5; }

/* 任务区 */
.tasks-section { margin-top: 40rpx; }
.tasks-heading { display: flex; align-items: center; justify-content: space-between; padding: 0 8rpx; }
.tasks-title-wrap { display: flex; align-items: center; gap: 12rpx; color: var(--sm-text); }
.tasks-title { font-size: 26rpx; font-weight: 800; }
.refresh-button { display: grid; place-items: center; width: 60rpx; height: 60rpx; border: 1rpx solid var(--sm-border); border-radius: 50%; background: var(--sm-surface); color: var(--sm-text-2); }
.status-filter-row { display: flex; flex-wrap: wrap; gap: 12rpx; margin-top: 18rpx; }
.status-chip { padding: 10rpx 24rpx; border: 1rpx solid var(--sm-border); border-radius: 28rpx; background: var(--sm-surface-2); color: var(--sm-text-2); font-size: 19rpx; font-weight: 600; }
.status-chip.active { border-color: var(--sm-accent-soft-border); background: var(--sm-accent-soft); color: var(--sm-accent); }

/* 任务卡片 */
.task-list { display: flex; flex-direction: column; gap: 16rpx; margin-top: 20rpx; }
.task-card { display: flex; gap: 18rpx; padding: 20rpx; border: 1rpx solid var(--sm-border); border-radius: 22rpx; background: var(--sm-surface); }
.task-thumb { flex: none; width: 88rpx; height: 88rpx; border-radius: 16rpx; overflow: hidden; background: var(--sm-surface-3); }
.task-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 6rpx; }
.task-title { overflow: hidden; color: var(--sm-text); font-size: 23rpx; font-weight: 700; text-overflow: ellipsis; white-space: nowrap; }
.task-meta { overflow: hidden; color: var(--sm-text-2); font-size: 17rpx; text-overflow: ellipsis; white-space: nowrap; }
.task-badges { display: flex; flex-wrap: wrap; gap: 8rpx; margin-top: 4rpx; }
.task-error { color: var(--sm-danger); font-size: 16rpx; }
.task-actions { display: flex; flex-direction: column; justify-content: center; gap: 10rpx; }
.action-btn { display: flex; align-items: center; gap: 8rpx; padding: 12rpx 20rpx; border-radius: 26rpx; font-size: 18rpx; font-weight: 700; }
.action-btn.retry { border: 1rpx solid var(--sm-accent-soft-border); background: var(--sm-accent-soft); color: var(--sm-accent); }
.action-btn.retry.disabled { opacity: .5; }
.action-btn.review { border: 1rpx solid var(--sm-border); background: var(--sm-surface-2); color: var(--sm-text-2); }
</style>
