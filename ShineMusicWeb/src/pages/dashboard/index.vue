<script setup lang="ts">
/**
 * 仪表盘：以 ECharts 可视化曲库、导入、点歌、邀请与音源任务数据。
 * 顶部保留可点击的数字总览与明细弹窗入口；图表点击同样可以下钻到明细或对应页面。
 */
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import {
  ChatDotRound,
  Download,
  Headset,
  Postcard,
  Refresh,
  Search,
  UploadFilled,
  WarningFilled,
} from '@element-plus/icons-vue'
import type { EChartsOption } from 'echarts'
import { getAdminTracks } from '@/services/admin-catalog'
import { listImportItems } from '@/services/admin-import'
import { getAdminSongRequests } from '@/services/admin-requests'
import { getInvitations } from '@/services/admin-invitation'
import {
  getDownloadTasks,
  getUnmatchedTracks,
  SourceProviderLabel,
  SourceQualityLabel,
  SourceTaskStatusLabel,
  type ExternalDownloadTask,
} from '@/services/admin-source-download'
import type {
  ApiExternalDownloadTaskList,
  ApiTrack,
  ApiUnmatchedTrack,
  ApiUnmatchedTrackList,
} from '@/types/api.generated'
import type { ApiPageResult } from '@/types/music'
import BaseChart from '@/components/BaseChart.vue'
import UnmatchedTracksDialog from '@/components/UnmatchedTracksDialog.vue'
import { describeError } from '@/services/errors'
import { formatDateTime } from '@/utils/format'

/** BaseChart 透传的点击信息（与组件内部 ChartClickParams 结构一致）。 */
interface ChartClickParams {
  name: string
  seriesName: string
  dataIndex: number
  data?: unknown
}

const router = useRouter()
const { t } = useI18n()
const loading = ref(false)
const stats = ref({
  tracks: 0,
  pendingReview: 0,
  pendingPublication: 0,
  openRequests: 0,
  invitations: 0,
  runningTasks: 0,
  failedTasks: 0,
  unmatchedTracks: 0,
})

const STATUS_KEYS = ['queued', 'downloading', 'import_queued', 'completed', 'failed'] as const
type StatusKey = (typeof STATUS_KEYS)[number]

const STATUS_COLORS: Record<StatusKey, string> = {
  queued: '#5b6b82',
  downloading: '#3b76d9',
  import_queued: '#1e8e94',
  completed: '#2f9e69',
  failed: '#d64545',
}

const emptyStatusCounts = (): Record<StatusKey, number> => ({
  queued: 0,
  downloading: 0,
  import_queued: 0,
  completed: 0,
  failed: 0,
})

const statusCounts = ref<Record<StatusKey, number>>(emptyStatusCounts())

/** 失败任务按音源聚合的尝试结果（含错误码分布）。 */
interface FailedAttemptRow {
  provider: string
  count: number
  errorCodes: readonly { code: string; count: number }[]
}

const failedAttemptRows = ref<readonly FailedAttemptRow[]>([])
const failedTaskTotal = ref(0)
const failedAttemptAggregated = ref(0)

const unmatchedNoSource = ref(0)
const unmatchedTaskFailed = ref(0)
const unmatchedOrigins = ref<readonly { name: string; value: number }[]>([])
const unmatchedTotal = ref(0)
const unmatchedAggregated = ref(0)

/** 曲库风格分布（TOP 8 + 其他）。 */
const genreRows = ref<readonly { name: string; value: number }[]>([])
const genreAggregated = ref(0)

/** 曲库发行年代分布（按十年分段，含未知）。 */
const yearRows = ref<readonly { name: string; value: number }[]>([])

/** 音源任务音质分布。 */
const qualityRows = ref<readonly { name: string; value: number }[]>([])
const qualityAggregated = ref(0)

/** 近 14 天任务创建 / 完成趋势（按自然日聚合）。 */
const trendDays = ref<readonly string[]>([])
const trendCreated = ref<readonly number[]>([])
const trendCompleted = ref<readonly number[]>([])

/** 未匹配 / 无音源曲目弹窗状态 */
const unmatchedDialogVisible = ref(false)

/** 失败任务明细弹窗状态 */
const failedDialogVisible = ref(false)
const failedLoading = ref(false)
const failedTasks = ref<readonly ExternalDownloadTask[]>([])
const failedTotal = ref(0)
const failedPage = ref(1)
const failedPageSize = ref(100)

const cards = computed(() => [
  { key: 'tracks', label: '曲库曲目', value: stats.value.tracks, hint: '已发布及待整理曲目', icon: Headset, tone: 'green', to: '/catalog' },
  { key: 'pendingReview', label: '待审核导入', value: stats.value.pendingReview, hint: '等待人工审核的导入条目', icon: UploadFilled, tone: 'blue', to: '/import' },
  { key: 'pendingPublication', label: '待转公开', value: stats.value.pendingPublication, hint: '用户上传等待发布', icon: UploadFilled, tone: 'orange', to: '/import' },
  { key: 'openRequests', label: '待处理点歌', value: stats.value.openRequests, hint: '状态为待安排的需求', icon: ChatDotRound, tone: 'purple', to: '/requests' },
  { key: 'invitations', label: '邀请记录', value: stats.value.invitations, hint: '累计发出的邀请', icon: Postcard, tone: 'cyan', to: '/invitations' },
  { key: 'runningTasks', label: '音源任务', value: stats.value.runningTasks, hint: '排队 / 下载 / 待导入（不含已完成）', icon: Download, tone: 'slate', to: '/source-download' },
  { key: 'failedTasks', label: '失败任务', value: stats.value.failedTasks, hint: '音源下载失败，点击查看明细', icon: WarningFilled, tone: 'red', to: '/source-download' },
  { key: 'unmatchedTracks', label: '未匹配曲目', value: stats.value.unmatchedTracks, hint: '标题+艺人未匹配，未创建下载任务，点击查看明细', icon: Search, tone: 'amber', to: '/source-download' },
])

const shortcuts = [
  { title: '上传音频', desc: '批量导入并转码', to: '/import', icon: UploadFilled },
  { title: '审核发布', desc: '试听、修正元数据后入库', to: '/import', icon: Headset },
  { title: '处理点歌', desc: '推进或拒绝用户需求', to: '/requests', icon: ChatDotRound },
  { title: '清理存储', desc: '审计孤儿文件与空目录', to: '/storage', icon: Download },
]

const taskTotal = computed(() => STATUS_KEYS.reduce((sum, key) => sum + statusCounts.value[key], 0))
const hasTaskStatus = computed(() => taskTotal.value > 0)
const hasFailedAttempts = computed(() => failedAttemptRows.value.length > 0)
const hasUnmatchedStructure = computed(() => unmatchedNoSource.value + unmatchedTaskFailed.value > 0)
const hasUnmatchedOrigins = computed(() => unmatchedOrigins.value.length > 0)
const hasGenres = computed(() => genreRows.value.length > 0)
const hasYears = computed(() => yearRows.value.length > 0)
const hasQuality = computed(() => qualityRows.value.length > 0)
const hasTrend = computed(() => trendDays.value.length > 0)

const TASK_STATUS_SERIES = 'task-status'
const UNMATCHED_SERIES = 'unmatched-structure'
const FAILED_ATTEMPT_SERIES = 'failed-attempts'
const UNMATCHED_ORIGIN_SERIES = 'unmatched-origin'
const GENRE_SERIES = 'genre-distribution'
const YEAR_SERIES = 'year-distribution'
const QUALITY_SERIES = 'quality-distribution'
const TREND_CREATED_SERIES = 'trend-created'
const TREND_COMPLETED_SERIES = 'trend-completed'

/** 饼图共用的柔和色板（与现有状态色同族）。 */
const PIE_PALETTE = ['#3b76d9', '#2f9e69', '#c47a1d', '#7b58c9', '#1e8e94', '#d64545', '#a16207', '#5b6b82', '#8a97a8']

const pieLegend = {
  bottom: 0,
  icon: 'circle',
  itemWidth: 8,
  itemHeight: 8,
  itemGap: 14,
  textStyle: { fontSize: 12, color: '#5b6b82' },
} as const

function buildPieOption(seriesName: string, unit: string, rows: readonly { name: string; value: number }[]): EChartsOption {
  return {
    tooltip: { trigger: 'item', formatter: `{b}：{c} ${unit}（{d}%）` },
    legend: pieLegend,
    color: [...PIE_PALETTE],
    series: [
      {
        name: seriesName,
        type: 'pie',
        radius: ['42%', '66%'],
        center: ['50%', '44%'],
        padAngle: 2,
        itemStyle: { borderRadius: 6 },
        label: { show: true, formatter: '{b}\n{c}', fontSize: 12, color: '#5b6b82' },
        labelLine: { length: 10, length2: 8, lineStyle: { color: '#d7dee8' } },
        emphasis: { scale: true, scaleSize: 5 },
        data: rows.map((row) => ({ name: row.name, value: row.value })),
      },
    ],
  }
}

/** 音源状态环形图：点击「失败」扇区打开失败明细，其余跳转音源下载页。 */
const taskStatusOption = computed<EChartsOption>(() => ({
  tooltip: { trigger: 'item', formatter: '{b}：{c} 个（{d}%）' },
  legend: {
    bottom: 0,
    icon: 'circle',
    itemWidth: 8,
    itemHeight: 8,
    itemGap: 14,
    textStyle: { fontSize: 12, color: '#5b6b82' },
  },
  series: [
    {
      name: TASK_STATUS_SERIES,
      type: 'pie',
      radius: ['44%', '68%'],
      center: ['50%', '44%'],
      padAngle: 2,
      itemStyle: { borderRadius: 6 },
      label: { show: true, formatter: '{b}\n{c}', fontSize: 12, color: '#5b6b82' },
      labelLine: { length: 10, length2: 8, lineStyle: { color: '#d7dee8' } },
      emphasis: { scale: true, scaleSize: 5 },
      data: STATUS_KEYS.map((key) => ({
        name: t(SourceTaskStatusLabel[key]),
        value: statusCounts.value[key],
        itemStyle: { color: STATUS_COLORS[key] },
        key,
      })),
    },
  ],
}))

/** 失败任务逐源条形图：按音源统计失败尝试次数，悬浮展示错误码分布。 */
const failedAttemptOrdered = computed(() => [...failedAttemptRows.value].reverse())

const failedAttemptOption = computed<EChartsOption>(() => ({
  tooltip: {
    trigger: 'axis',
    axisPointer: { type: 'shadow' },
    formatter: failedAttemptTooltip,
  },
  grid: { left: 8, right: 40, top: 8, bottom: 8, containLabel: true },
  xAxis: {
    type: 'value',
    minInterval: 1,
    axisLabel: { color: '#8a97a8', fontSize: 11 },
    splitLine: { lineStyle: { color: '#eef1f6' } },
  },
  yAxis: {
    type: 'category',
    data: failedAttemptOrdered.value.map((row) => providerLabel(row.provider)),
    axisTick: { show: false },
    axisLine: { show: false },
    axisLabel: { color: '#5b6b82', fontSize: 12 },
  },
  series: [
    {
      name: FAILED_ATTEMPT_SERIES,
      type: 'bar',
      barMaxWidth: 18,
      data: failedAttemptOrdered.value.map((row) => ({
        value: row.count,
        itemStyle: { color: '#d64545', borderRadius: [0, 4, 4, 0] },
      })),
      label: { show: true, position: 'right', color: '#d64545', fontSize: 12 },
    },
  ],
}))

/** 未匹配曲目结构环形图：区分「无音源」与「下载失败」。 */
const unmatchedStructureOption = computed<EChartsOption>(() => ({
  tooltip: { trigger: 'item', formatter: '{b}：{c} 首（{d}%）' },
  legend: {
    bottom: 0,
    icon: 'circle',
    itemWidth: 8,
    itemHeight: 8,
    itemGap: 14,
    textStyle: { fontSize: 12, color: '#5b6b82' },
  },
  series: [
    {
      name: UNMATCHED_SERIES,
      type: 'pie',
      radius: ['44%', '68%'],
      center: ['50%', '44%'],
      padAngle: 2,
      itemStyle: { borderRadius: 6 },
      label: { show: true, formatter: '{b}\n{c}', fontSize: 12, color: '#5b6b82' },
      labelLine: { length: 10, length2: 8, lineStyle: { color: '#d7dee8' } },
      emphasis: { scale: true, scaleSize: 5 },
      data: [
        { name: '无音源', value: unmatchedNoSource.value, itemStyle: { color: '#a16207' } },
        { name: '下载失败', value: unmatchedTaskFailed.value, itemStyle: { color: '#d64545' } },
      ],
    },
  ],
}))

/** 未匹配曲目来源 TOP 条形图：定位问题集中在哪些歌单 / 独立队列。 */
const unmatchedOriginOrdered = computed(() => [...unmatchedOrigins.value].reverse())

const unmatchedOriginOption = computed<EChartsOption>(() => ({
  tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' }, formatter: '{b}<br/>未匹配 {c} 首' },
  grid: { left: 8, right: 40, top: 8, bottom: 8, containLabel: true },
  xAxis: {
    type: 'value',
    minInterval: 1,
    axisLabel: { color: '#8a97a8', fontSize: 11 },
    splitLine: { lineStyle: { color: '#eef1f6' } },
  },
  yAxis: {
    type: 'category',
    data: unmatchedOriginOrdered.value.map((row) => row.name),
    axisTick: { show: false },
    axisLine: { show: false },
    axisLabel: { color: '#5b6b82', fontSize: 12, width: 150, overflow: 'truncate' },
  },
  series: [
    {
      name: UNMATCHED_ORIGIN_SERIES,
      type: 'bar',
      barMaxWidth: 16,
      data: unmatchedOriginOrdered.value.map((row) => ({
        value: row.value,
        itemStyle: { color: '#a16207', borderRadius: [0, 4, 4, 0] },
      })),
      label: { show: true, position: 'right', color: '#a16207', fontSize: 12 },
    },
  ],
}))

/** 曲库风格分布环形图。 */
const genreOption = computed<EChartsOption>(() => buildPieOption(GENRE_SERIES, '首', genreRows.value))

/** 音源任务音质分布环形图。 */
const qualityOption = computed<EChartsOption>(() => buildPieOption(QUALITY_SERIES, '个', qualityRows.value))

/** 曲库发行年代柱状图：按十年分段，升序排列。 */
const yearOption = computed<EChartsOption>(() => ({
  tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' }, formatter: '{b}<br/>{c} 首' },
  grid: { left: 8, right: 16, top: 16, bottom: 8, containLabel: true },
  xAxis: {
    type: 'category',
    data: yearRows.value.map((row) => row.name),
    axisTick: { show: false },
    axisLine: { lineStyle: { color: '#d7dee8' } },
    axisLabel: { color: '#5b6b82', fontSize: 12 },
  },
  yAxis: {
    type: 'value',
    minInterval: 1,
    axisLabel: { color: '#8a97a8', fontSize: 11 },
    splitLine: { lineStyle: { color: '#eef1f6' } },
  },
  series: [
    {
      name: YEAR_SERIES,
      type: 'bar',
      barMaxWidth: 26,
      data: yearRows.value.map((row) => ({
        value: row.value,
        itemStyle: { color: '#7b58c9', borderRadius: [4, 4, 0, 0] },
      })),
      label: { show: true, position: 'top', color: '#7b58c9', fontSize: 12 },
    },
  ],
}))

/** 近 14 天任务趋势折线图：创建量 vs 完成量。 */
const trendOption = computed<EChartsOption>(() => ({
  tooltip: { trigger: 'axis' },
  legend: pieLegend,
  grid: { left: 8, right: 16, top: 24, bottom: 32, containLabel: true },
  xAxis: {
    type: 'category',
    boundaryGap: false,
    data: [...trendDays.value],
    axisTick: { show: false },
    axisLine: { lineStyle: { color: '#d7dee8' } },
    axisLabel: { color: '#8a97a8', fontSize: 11 },
  },
  yAxis: {
    type: 'value',
    minInterval: 1,
    axisLabel: { color: '#8a97a8', fontSize: 11 },
    splitLine: { lineStyle: { color: '#eef1f6' } },
  },
  series: [
    {
      name: '新建任务',
      type: 'line',
      smooth: true,
      symbolSize: 6,
      data: [...trendCreated.value],
      itemStyle: { color: '#3b76d9' },
      lineStyle: { width: 2 },
      areaStyle: { color: 'rgba(59, 118, 217, 0.12)' },
    },
    {
      name: '完成任务',
      type: 'line',
      smooth: true,
      symbolSize: 6,
      data: [...trendCompleted.value],
      itemStyle: { color: '#2f9e69' },
      lineStyle: { width: 2 },
      areaStyle: { color: 'rgba(47, 158, 105, 0.12)' },
    },
  ],
}))

async function loadStats() {
  loading.value = true
  const requests = [
    getAdminTracks({ pageSize: 1 }),
    listImportItems('pending_review', 1, 1),
    listImportItems('pending_publication', 1, 1),
    getAdminSongRequests({ status: 'open', pageSize: 1 }),
    getInvitations(undefined, 1, 1),
    // 音源任务：各状态取 total 用于状态环形图。
    ...STATUS_KEYS.map((status) => getDownloadTasks(status, 1, 1)),
    // 失败任务全量（上限 1000）用于聚合「逐源尝试」，并作为失败明细的数据来源。
    getDownloadTasks('failed', 1, 1000),
    // 未匹配 / 无音源曲目（no_source + failed），用于结构图与来源 TOP。
    getUnmatchedTracks({ page: 1, pageSize: 1000 }),
    // 全部任务（上限 1000）：音质分布 + 近 14 天创建/完成趋势。
    getDownloadTasks(undefined, 1, 1000),
    // 曲库样本（上限 1000）：风格分布 + 发行年代分布。
    getAdminTracks({ pageSize: 1000 }),
  ]
  const results = await Promise.allSettled(requests)
  const total = (index: number) => {
    const result = results[index]
    return result.status === 'fulfilled' ? result.value.total : 0
  }

  const nextStatusCounts = emptyStatusCounts()
  STATUS_KEYS.forEach((key, index) => {
    nextStatusCounts[key] = total(5 + index)
  })
  statusCounts.value = nextStatusCounts

  const failedListResult = results[10]
  if (failedListResult.status === 'fulfilled') {
    const failedList = failedListResult.value as ApiExternalDownloadTaskList
    failedTaskTotal.value = failedList.total
    failedAttemptAggregated.value = failedList.items.length
    failedAttemptRows.value = aggregateFailedAttempts(failedList.items)
  } else {
    failedTaskTotal.value = 0
    failedAttemptAggregated.value = 0
    failedAttemptRows.value = []
  }

  const unmatchedListResult = results[11]
  if (unmatchedListResult.status === 'fulfilled') {
    const unmatchedList = unmatchedListResult.value as ApiUnmatchedTrackList
    unmatchedTotal.value = unmatchedList.total
    unmatchedAggregated.value = unmatchedList.items.length
    unmatchedNoSource.value = unmatchedList.noSource
    unmatchedTaskFailed.value = unmatchedList.taskFailed
    unmatchedOrigins.value = aggregateUnmatchedOrigins(unmatchedList.items)
  } else {
    unmatchedTotal.value = 0
    unmatchedAggregated.value = 0
    unmatchedNoSource.value = 0
    unmatchedTaskFailed.value = 0
    unmatchedOrigins.value = []
  }

  const allTasksResult = results[12]
  if (allTasksResult.status === 'fulfilled') {
    const allTasks = allTasksResult.value as ApiExternalDownloadTaskList
    qualityAggregated.value = allTasks.items.length
    qualityRows.value = aggregateQuality(allTasks.items)
    const trend = aggregateTrend(allTasks.items)
    trendDays.value = trend.days
    trendCreated.value = trend.created
    trendCompleted.value = trend.completed
  } else {
    qualityAggregated.value = 0
    qualityRows.value = []
    trendDays.value = []
    trendCreated.value = []
    trendCompleted.value = []
  }

  const tracksResult = results[13]
  if (tracksResult.status === 'fulfilled') {
    const trackList = tracksResult.value as ApiPageResult<ApiTrack>
    genreAggregated.value = trackList.items.length
    genreRows.value = aggregateGenres(trackList.items)
    yearRows.value = aggregateYears(trackList.items)
  } else {
    genreAggregated.value = 0
    genreRows.value = []
    yearRows.value = []
  }

  stats.value = {
    tracks: total(0),
    pendingReview: total(1),
    pendingPublication: total(2),
    openRequests: total(3),
    invitations: total(4),
    runningTasks: nextStatusCounts.queued + nextStatusCounts.downloading + nextStatusCounts.import_queued,
    failedTasks: nextStatusCounts.failed,
    unmatchedTracks: unmatchedTotal.value,
  }
  if (results.some((item) => item.status === 'rejected')) {
    const first = results.find((item) => item.status === 'rejected') as PromiseRejectedResult | undefined
    ElMessage.warning(describeError(first?.reason, '部分统计加载失败'))
  }
  loading.value = false
}

onMounted(loadStats)

/** 失败任务按 provider 聚合：优先用 attemptErrors，缺失时回退到任务主音源。 */
function aggregateFailedAttempts(tasks: readonly ExternalDownloadTask[]): FailedAttemptRow[] {
  const byProvider = new Map<string, { provider: string; count: number; codes: Map<string, number> }>()
  for (const task of tasks) {
    const attempts = task.attemptErrors?.length
      ? task.attemptErrors
      : [{ provider: task.provider, errorCode: task.errorCode ?? undefined }]
    for (const attempt of attempts) {
      const entry = byProvider.get(attempt.provider) ?? { provider: attempt.provider, count: 0, codes: new Map<string, number>() }
      entry.count += 1
      const code = attempt.errorCode || '未标注错误码'
      entry.codes.set(code, (entry.codes.get(code) ?? 0) + 1)
      byProvider.set(attempt.provider, entry)
    }
  }
  return [...byProvider.values()]
    .map((entry) => ({
      provider: entry.provider,
      count: entry.count,
      errorCodes: [...entry.codes.entries()]
        .map(([code, count]) => ({ code, count }))
        .sort((a, b) => b.count - a.count),
    }))
    .sort((a, b) => b.count - a.count)
}

/** 未匹配曲目按来源（歌单 / 独立队列）聚合，保留 TOP 8 并合并其余。 */
function aggregateUnmatchedOrigins(rows: readonly ApiUnmatchedTrack[]): { name: string; value: number }[] {
  const byOrigin = new Map<string, number>()
  for (const row of rows) {
    const name = row.origin === 'queue' ? '独立加入下载队列' : row.playlistName || '歌单导入'
    byOrigin.set(name, (byOrigin.get(name) ?? 0) + 1)
  }
  const sorted = [...byOrigin.entries()]
    .map(([name, value]) => ({ name, value }))
    .sort((a, b) => b.value - a.value)
  if (sorted.length <= 8) return sorted
  const rest = sorted.slice(8).reduce((sum, item) => sum + item.value, 0)
  return [...sorted.slice(0, 7), { name: '其他来源', value: rest }]
}

/** 音质分布：按任务音质聚合，未知音质归入「未标注」。 */
function aggregateQuality(tasks: readonly ExternalDownloadTask[]): { name: string; value: number }[] {
  const byQuality = new Map<string, number>()
  for (const task of tasks) {
    const key = task.quality?.trim() || ''
    byQuality.set(key, (byQuality.get(key) ?? 0) + 1)
  }
  return [...byQuality.entries()]
    .map(([key, value]) => ({ name: qualityLabel(key), value }))
    .sort((a, b) => b.value - a.value)
}

function qualityLabel(quality: string) {
  if (!quality) return '未标注'
  const label = SourceQualityLabel[quality]
  return label ? t(label) : quality
}

/** 近 14 天趋势：按自然日聚合任务创建量与完成量（完成以 updatedAt 近似）。 */
function aggregateTrend(tasks: readonly ExternalDownloadTask[]): { days: string[]; created: number[]; completed: number[] } {
  const days: string[] = []
  const buckets = new Map<string, { created: number; completed: number }>()
  const today = new Date()
  for (let offset = 13; offset >= 0; offset--) {
    const day = new Date(today.getFullYear(), today.getMonth(), today.getDate() - offset)
    const key = `${day.getMonth() + 1}/${day.getDate()}`
    days.push(key)
    buckets.set(dayKey(day), { created: 0, completed: 0 })
  }
  for (const task of tasks) {
    const created = buckets.get(dayKey(new Date(task.createdAtUtc)))
    if (created) created.created += 1
    if (task.status === 'completed') {
      const done = buckets.get(dayKey(new Date(task.updatedAtUtc)))
      if (done) done.completed += 1
    }
  }
  const values = [...buckets.values()]
  return {
    days,
    created: values.map((bucket) => bucket.created),
    completed: values.map((bucket) => bucket.completed),
  }
}

/** 本地自然日 key（与趋势桶一致）。 */
function dayKey(date: Date) {
  return `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`
}

/** 曲库风格分布：一曲多风格各计一次，保留 TOP 8，其余合并。 */
function aggregateGenres(tracks: readonly ApiTrack[]): { name: string; value: number }[] {
  const byGenre = new Map<string, number>()
  for (const track of tracks) {
    const genres = track.genres.length > 0 ? track.genres : ['未标注']
    for (const genre of genres) {
      const name = genre.trim() || '未标注'
      byGenre.set(name, (byGenre.get(name) ?? 0) + 1)
    }
  }
  const sorted = [...byGenre.entries()]
    .map(([name, value]) => ({ name, value }))
    .sort((a, b) => b.value - a.value)
  if (sorted.length <= 8) return sorted
  const rest = sorted.slice(8).reduce((sum, item) => sum + item.value, 0)
  return [...sorted.slice(0, 7), { name: '其他风格', value: rest }]
}

/** 发行年代分布：按十年分段（如 2010s），未知年份归入「未知」，按年代升序、「未知」排最后。 */
function aggregateYears(tracks: readonly ApiTrack[]): { name: string; value: number }[] {
  const byDecade = new Map<string, number>()
  for (const track of tracks) {
    const year = track.releaseYear
    const name = year && year > 0 ? `${Math.floor(year / 10) * 10}s` : '未知'
    byDecade.set(name, (byDecade.get(name) ?? 0) + 1)
  }
  return [...byDecade.entries()]
    .map(([name, value]) => ({ name, value }))
    .sort((a, b) => {
      if (a.name === '未知') return 1
      if (b.name === '未知') return -1
      return a.name.localeCompare(b.name)
    })
}

function failedAttemptTooltip(params: unknown): string {
  const list = Array.isArray(params) ? params : [params]
  const first = list[0] as { dataIndex?: number } | undefined
  const row = failedAttemptOrdered.value[first?.dataIndex ?? -1]
  if (!row) return ''
  const codes = row.errorCodes
    .map((item) => `${escapeHtml(item.code)}：${item.count} 次`)
    .join('<br/>')
  return `<b>${escapeHtml(providerLabel(row.provider))}</b><br/>音源尝试失败 ${row.count} 次<br/>${codes}`
}

/** tooltip 以 HTML 渲染，外部错误码先转义避免注入。 */
function escapeHtml(text: string) {
  return text.replace(/[&<>"']/g, (char) => {
    const map: Record<string, string> = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }
    return map[char] ?? char
  })
}

/** 点击状态环形图：失败扇区打开明细弹窗，其余进入音源下载页。 */
function handleTaskStatusClick(params: ChartClickParams) {
  const row = params.data as { key?: StatusKey } | undefined
  if (row?.key === 'failed') {
    void openFailedDialog()
    return
  }
  void router.push('/source-download')
}

/** 打开失败明细弹窗：每次打开都回到第一页重新拉取，保证数据最新。 */
async function openFailedDialog() {
  failedDialogVisible.value = true
  failedPage.value = 1
  await loadFailedTasks()
}

async function loadFailedTasks() {
  failedLoading.value = true
  try {
    const result = await getDownloadTasks('failed', failedPage.value, failedPageSize.value)
    failedTasks.value = result.items
    failedTotal.value = result.total
  } catch (error) {
    ElMessage.error(describeError(error, '失败任务加载失败'))
  } finally {
    failedLoading.value = false
  }
}

function changeFailedPage(page: number) {
  failedPage.value = page
  void loadFailedTasks()
}

function changeFailedPageSize(size: number) {
  failedPageSize.value = size
  failedPage.value = 1
  void loadFailedTasks()
}

/** 普通数字跳转对应页面；失败 / 未匹配入口改为打开明细弹窗。 */
function handleCardClick(card: { key: string; to: string }) {
  if (card.key === 'failedTasks') {
    void openFailedDialog()
    return
  }
  if (card.key === 'unmatchedTracks') {
    unmatchedDialogVisible.value = true
    return
  }
  void router.push(card.to)
}

function providerLabel(provider: string) {
  const label = SourceProviderLabel[provider] ?? provider
  return label.startsWith('sourceDownload.') ? t(label) : label
}
</script>

<template>
  <div class="page">
    <div class="page-heading">
      <div>
        <h2>仪表盘</h2>
        <p>曲库与运营数据总览</p>
      </div>
      <el-button :icon="Refresh" :loading="loading" @click="loadStats">刷新数据</el-button>
    </div>

    <div class="overview-strip" v-loading="loading">
      <button
        v-for="card in cards"
        :key="card.key"
        class="overview-chip"
        type="button"
        :title="card.hint"
        @click="handleCardClick(card)"
      >
        <el-icon :size="15" :class="['chip-icon', card.tone]"><component :is="card.icon" /></el-icon>
        <span class="chip-label">{{ card.label }}</span>
        <strong class="chip-value">{{ card.value }}</strong>
      </button>
    </div>

    <div class="chart-grid">
      <section class="panel chart-panel chart-span-2">
        <div class="chart-head">
          <div>
            <div class="panel-title">近 14 天任务趋势</div>
            <p class="chart-sub">新建与完成的音源任务量（按最近 {{ qualityAggregated }} 条任务聚合）；点击进入音源下载页</p>
          </div>
        </div>
        <BaseChart
          v-if="hasTrend"
          :option="trendOption"
          height="300px"
          @chart-click="router.push('/source-download')"
        />
        <div v-else class="chart-empty">暂无任务数据</div>
      </section>

      <section class="panel chart-panel">
        <div class="chart-head">
          <div>
            <div class="panel-title">音源任务状态分布</div>
            <p class="chart-sub">共 {{ taskTotal }} 条任务；点击「失败」查看明细，其他扇区进入音源下载页</p>
          </div>
        </div>
        <BaseChart
          v-if="hasTaskStatus"
          :option="taskStatusOption"
          height="300px"
          @chart-click="handleTaskStatusClick"
        />
        <div v-else class="chart-empty">暂无音源任务</div>
      </section>

      <section class="panel chart-panel">
        <div class="chart-head">
          <div>
            <div class="panel-title">任务音质分布</div>
            <p class="chart-sub">按最近 {{ qualityAggregated }} 条任务聚合；点击进入音源下载页</p>
          </div>
        </div>
        <BaseChart
          v-if="hasQuality"
          :option="qualityOption"
          height="300px"
          @chart-click="router.push('/source-download')"
        />
        <div v-else class="chart-empty">暂无音质数据</div>
      </section>

      <section class="panel chart-panel">
        <div class="chart-head">
          <div>
            <div class="panel-title">曲库风格分布</div>
            <p class="chart-sub">按最近 {{ genreAggregated }} 首曲目聚合（一曲多风格各计一次）；点击进入曲目页</p>
          </div>
        </div>
        <BaseChart
          v-if="hasGenres"
          :option="genreOption"
          height="300px"
          @chart-click="router.push('/catalog')"
        />
        <div v-else class="chart-empty">暂无风格数据</div>
      </section>

      <section class="panel chart-panel">
        <div class="chart-head">
          <div>
            <div class="panel-title">曲库发行年代</div>
            <p class="chart-sub">按十年分段（按最近 {{ genreAggregated }} 首曲目聚合）；点击进入曲目页</p>
          </div>
        </div>
        <BaseChart
          v-if="hasYears"
          :option="yearOption"
          height="300px"
          @chart-click="router.push('/catalog')"
        />
        <div v-else class="chart-empty">暂无年份数据</div>
      </section>

      <section class="panel chart-panel">
        <div class="chart-head">
          <div>
            <div class="panel-title">失败任务：逐源尝试次数</div>
            <p class="chart-sub">
              共 {{ failedTaskTotal }} 个失败任务，按 {{ failedAttemptAggregated }} 条任务聚合；悬浮柱子查看错误码，点击查看明细
            </p>
          </div>
        </div>
        <BaseChart
          v-if="hasFailedAttempts"
          :option="failedAttemptOption"
          height="300px"
          @chart-click="openFailedDialog"
        />
        <div v-else class="chart-empty">暂无失败任务</div>
      </section>

      <section class="panel chart-panel">
        <div class="chart-head">
          <div>
            <div class="panel-title">未匹配曲目结构</div>
            <p class="chart-sub">
              无音源 {{ unmatchedNoSource }} 首 / 下载失败 {{ unmatchedTaskFailed }} 首（共 {{ unmatchedTotal }} 首）；点击扇区查看明细
            </p>
          </div>
        </div>
        <BaseChart
          v-if="hasUnmatchedStructure"
          :option="unmatchedStructureOption"
          height="300px"
          @chart-click="unmatchedDialogVisible = true"
        />
        <div v-else class="chart-empty">暂无未匹配曲目</div>
      </section>

      <section class="panel chart-panel">
        <div class="chart-head">
          <div>
            <div class="panel-title">未匹配来源 TOP</div>
            <p class="chart-sub">
              按歌单 / 独立队列聚合（{{ unmatchedAggregated }} 首明细）；点击柱子查看明细
            </p>
          </div>
        </div>
        <BaseChart
          v-if="hasUnmatchedOrigins"
          :option="unmatchedOriginOption"
          height="300px"
          @chart-click="unmatchedDialogVisible = true"
        />
        <div v-else class="chart-empty">暂无来源数据</div>
      </section>
    </div>

    <div class="panel">
      <div class="panel-title">快捷入口</div>
      <div class="shortcut-grid">
        <button
          v-for="item in shortcuts"
          :key="item.title"
          class="shortcut"
          type="button"
          @click="router.push(item.to)"
        >
          <el-icon :size="18"><component :is="item.icon" /></el-icon>
          <div>
            <strong>{{ item.title }}</strong>
            <span>{{ item.desc }}</span>
          </div>
        </button>
      </div>
    </div>

    <el-dialog v-model="failedDialogVisible" title="失败歌曲明细" width="960px" top="6vh">
      <div class="failed-toolbar">
        <span class="text-faint">共 {{ failedTotal }} 条失败任务（仅统计 failed 状态）</span>
        <el-button size="small" :loading="failedLoading" @click="loadFailedTasks">刷新</el-button>
      </div>
      <el-table v-loading="failedLoading" :data="failedTasks" max-height="460" empty-text="暂无失败任务">
        <el-table-column label="曲名" prop="title" sortable min-width="200" show-overflow-tooltip>
          <template #default="{ row }">{{ row.title || '—' }}</template>
        </el-table-column>
        <el-table-column label="艺人" prop="artists" sortable min-width="160" show-overflow-tooltip>
          <template #default="{ row }">{{ row.artists.join(' / ') || '—' }}</template>
        </el-table-column>
        <el-table-column label="专辑" prop="albumTitle" sortable min-width="150" show-overflow-tooltip>
          <template #default="{ row }">{{ row.albumTitle || '—' }}</template>
        </el-table-column>
        <el-table-column label="音源" prop="provider" sortable width="100">
          <template #default="{ row }">{{ providerLabel(row.provider) }}</template>
        </el-table-column>
        <el-table-column label="尝试音源" min-width="180">
          <template #default="{ row }">
            <div v-if="row.attemptErrors?.length" class="attempt-tags">
              <el-tag
                v-for="(attempt, index) in row.attemptErrors"
                :key="index"
                size="small"
                type="danger"
                effect="plain"
                :title="attempt.message || ''"
              >
                {{ providerLabel(attempt.provider) }}<span v-if="attempt.errorCode"> · {{ attempt.errorCode }}</span>
              </el-tag>
            </div>
            <span v-else class="text-faint">仅 1 个源</span>
          </template>
        </el-table-column>
        <el-table-column label="失败原因" prop="errorMessage" sortable min-width="240" show-overflow-tooltip>
          <template #default="{ row }">{{ row.errorMessage || row.errorCode || '未知原因' }}</template>
        </el-table-column>
        <el-table-column label="更新时间" prop="updatedAtUtc" sortable width="170">
          <template #default="{ row }">{{ formatDateTime(row.updatedAtUtc) }}</template>
        </el-table-column>
      </el-table>
      <div class="failed-pager">
        <el-pagination
          :current-page="failedPage"
          :page-size="failedPageSize"
          :page-sizes="[100, 150, 200, 500, 1000]"
          :total="failedTotal"
          layout="total, sizes, prev, pager, next"
          background
          @current-change="changeFailedPage"
          @size-change="changeFailedPageSize"
        />
      </div>
      <template #footer>
        <el-button @click="failedDialogVisible = false">关闭</el-button>
        <el-button type="primary" @click="router.push('/source-download')">去音源下载页处理</el-button>
      </template>
    </el-dialog>

    <UnmatchedTracksDialog v-model="unmatchedDialogVisible" />
  </div>
</template>

<style scoped>
.overview-strip {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
  gap: 10px;
}

.overview-chip {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  border: 1px solid var(--adm-border);
  border-radius: 9px;
  background: #ffffff;
  text-align: left;
  cursor: pointer;
  transition: border-color 0.15s ease, box-shadow 0.15s ease;
}

.overview-chip:hover {
  border-color: #bfe3d0;
  box-shadow: 0 4px 12px rgba(16, 24, 40, 0.06);
}

.chip-icon {
  flex: none;
}

.chip-icon.green { color: #2f9e69; }
.chip-icon.blue { color: #3b76d9; }
.chip-icon.orange { color: #c47a1d; }
.chip-icon.purple { color: #7b58c9; }
.chip-icon.cyan { color: #1e8e94; }
.chip-icon.slate { color: #5b6b82; }
.chip-icon.red { color: #d64545; }
.chip-icon.amber { color: #a16207; }

.chip-label {
  flex: 1;
  min-width: 0;
  color: var(--adm-text-2);
  font-size: 13px;
  white-space: nowrap;
}

.chip-value {
  color: #111827;
  font-size: 18px;
  font-weight: 700;
  line-height: 1.2;
}

.chart-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 14px;
}

.chart-span-2 {
  grid-column: 1 / -1;
}

.chart-panel {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

@media (max-width: 1100px) {
  .chart-grid {
    grid-template-columns: minmax(0, 1fr);
  }
}

.chart-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 4px;
}

.chart-sub {
  margin: 4px 0 0;
  color: var(--adm-text-3);
  font-size: 12px;
}

.chart-empty {
  display: grid;
  place-items: center;
  height: 300px;
  color: var(--adm-text-3);
  font-size: 13px;
}

.failed-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 10px;
}

.failed-pager {
  display: flex;
  justify-content: flex-end;
  margin-top: 12px;
}

.attempt-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.shortcut-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 12px;
}

.shortcut {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 14px 16px;
  border: 1px solid var(--adm-border);
  border-radius: 9px;
  background: #fbfcfd;
  color: var(--adm-text);
  text-align: left;
  cursor: pointer;
  transition: background 0.15s ease, border-color 0.15s ease;
}

.shortcut:hover {
  border-color: var(--el-color-primary-light-5);
  background: var(--adm-primary-soft);
}

.shortcut > div {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.shortcut strong {
  font-size: 14px;
  font-weight: 600;
}

.shortcut span {
  color: var(--adm-text-2);
  font-size: 12px;
}
</style>
