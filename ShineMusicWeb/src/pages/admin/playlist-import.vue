<script setup lang="ts">
/**
 * 歌单解析导入：粘贴外部平台（网易云/QQ 音乐/酷我/酷狗/汽水/YouTube Music）分享链接，
 * 解析预览（标记已匹配曲库的曲目）→ 独立加入下载队列，或单独导入歌单。
 *
 * 注意：导入请求本身不做音源搜索（后端只做曲库匹配 + 落库，秒级返回），
 * 真正的搜索与下载由后台 worker 分轮推进，因此大歌单也不会卡住页面。
 */
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { Link, Download, MagicStick, Refresh, Search } from '@element-plus/icons-vue'
import {
  importPlaylist,
  parsePlaylistImport,
  PlaylistImportPlatformLabel,
  queuePlaylistDownloads,
  rematchPlaylist,
  type PlaylistImportParseResult,
  type PlaylistRematchResult,
} from '@/services/playlist-import'
import { getPlaylist, getPlaylists } from '@/services/library'
import { describeError } from '@/services/errors'

const router = useRouter()
const form = reactive({ url: '', name: '' })
const parsing = ref(false)
const importing = ref(false)
const preview = ref<PlaylistImportParseResult | null>(null)

/** 最近一次导入结果（用于展示「已加入队列」回执与跳转下载任务）。 */
const queuedResult = ref<{
  type: 'queue' | 'playlist'
  playlistId?: string
  total: number
  matched: number
  pending: number
  queued?: boolean
} | null>(null)

const playlists = ref<{ id: string; name: string; trackCount: number }[]>([])
const rematchPlaylistId = ref('')
const rematching = ref(false)
const rematchResult = ref<PlaylistRematchResult | null>(null)

/** 补齐进度（按 pending 行状态聚合），用于直观展示「还在下载 / 待审核 / 无音源」。 */
interface BackfillProgress {
  total: number
  pending: number
  downloading: number
  needsReview: number
  noSource: number
  failed: number
  rejected: number
  skipped: number
}

/** 单条待补齐曲目的状态语义：关键是区分「还在流水线」与「已下载、只等人工入库」。 */
interface PendingStatusMeta {
  label: string
  type: 'primary' | 'warning' | 'info' | 'danger' | 'success'
  /** 后台是否还需要为它干活（决定是否继续轮询）。 */
  active: boolean
  hint: string
}

const PENDING_STATUS_META: Record<string, PendingStatusMeta> = {
  pending: { label: '排队中', type: 'warning', active: true, hint: '等后台搜索音源' },
  downloading: { label: '下载中', type: 'primary', active: true, hint: '后台正在下载/转码' },
  needs_review: {
    label: '待审核',
    type: 'info',
    active: false,
    hint: '已下载完成，不用再下载；评分低于自动入库阈值，确认后即可入库',
  },
  no_source: { label: '无音源', type: 'danger', active: false, hint: '搜索没有匹配到任何候选曲目（多为冷门曲/翻唱/纯音乐，标题对不上）' },
  failed: {
    label: '失败',
    type: 'danger',
    active: false,
    hint: '各音源都没拿到可下载直链（VIP/版权限制或平台只给试听），可「重新补齐」重试',
  },
  rejected: {
    label: '已拒绝',
    type: 'info',
    active: false,
    hint: '音频已下载，但在「导入审核」页被拒绝，不会入库；重新下载也不会改变结果',
  },
  skipped: { label: '未下载', type: 'info', active: false, hint: '导入时选了「仅导入歌单」，未排入下载队列' },
}

const STATUS_FALLBACK: PendingStatusMeta = { label: '未知', type: 'info', active: false, hint: '' }

interface PendingRowView {
  id: string
  title: string
  artists: string
  status: string
  meta: PendingStatusMeta
}

const progressRows = ref<PendingRowView[]>([])
const progressLoaded = ref(false)
const progressFilter = ref<'all' | 'active' | 'needs_review' | 'issue'>('all')

const progress = computed<BackfillProgress>(() => {
  const counts: BackfillProgress = {
    total: progressRows.value.length,
    pending: 0,
    downloading: 0,
    needsReview: 0,
    noSource: 0,
    failed: 0,
    rejected: 0,
    skipped: 0,
  }
  for (const row of progressRows.value) {
    switch (row.status) {
      case 'pending': counts.pending += 1; break
      case 'downloading': counts.downloading += 1; break
      case 'needs_review': counts.needsReview += 1; break
      case 'no_source': counts.noSource += 1; break
      case 'failed': counts.failed += 1; break
      case 'rejected': counts.rejected += 1; break
      case 'skipped': counts.skipped += 1; break
      default: break
    }
  }
  return counts
})

/** 是否还有后台在途任务（决定继续轮询还是收尾）。 */
const hasActiveWork = computed(() => progress.value.pending + progress.value.downloading > 0)

const filteredRows = computed(() => {
  switch (progressFilter.value) {
    case 'active':
      return progressRows.value.filter((row) => row.meta.active)
    case 'needs_review':
      return progressRows.value.filter((row) => row.status === 'needs_review')
    case 'issue':
      return progressRows.value.filter(
        (row) => row.status === 'no_source' || row.status === 'failed' || row.status === 'rejected',
      )
    default:
      return progressRows.value
  }
})

let progressTimer: ReturnType<typeof setInterval> | null = null

/** 拉取选中歌单的待补齐明细；仍有在途任务时保持轮询，全部收敛后自动停止。 */
async function loadProgress(playlistId = rematchPlaylistId.value) {
  if (!playlistId) {
    progressRows.value = []
    progressLoaded.value = false
    return
  }
  try {
    const { detail } = await getPlaylist(playlistId)
    // 期间切换了歌单：丢弃过期响应。
    if (playlistId !== rematchPlaylistId.value) return
    progressRows.value = (detail.pendingTracks ?? []).map((row) => ({
      id: row.id,
      title: row.title,
      artists: row.artists.join(' / '),
      status: row.status,
      meta: PENDING_STATUS_META[row.status] ?? STATUS_FALLBACK,
    }))
    progressLoaded.value = true
    // 没有排队中/下载中的行了说明本轮已收敛，停掉轮询（待审核的不会自己变化，等人工发布后再刷新）。
    if (!hasActiveWork.value) stopProgressPolling()
  } catch {
    // 进度面板是辅助信息，失败不打断主流程。
  }
}

function startProgressPolling() {
  stopProgressPolling()
  progressTimer = setInterval(() => { void loadProgress() }, 5000)
}

function stopProgressPolling() {
  if (progressTimer) {
    clearInterval(progressTimer)
    progressTimer = null
  }
}

const matchedCount = computed(() => preview.value?.tracks.filter((track) => track.matched).length ?? 0)
const unmatchedCount = computed(() => (preview.value ? preview.value.total - matchedCount.value : 0))

/** 预览曲目关键词：本地匹配曲名 / 艺人 / 专辑（不必回源请求）。 */
const previewKeyword = ref('')
const previewTracks = computed(() => {
  const tracks = preview.value?.tracks ?? []
  const term = previewKeyword.value.trim().toLowerCase()
  if (!term) return tracks
  return tracks.filter(
    (track) =>
      track.title.toLowerCase().includes(term) ||
      (track.albumTitle ?? '').toLowerCase().includes(term) ||
      track.artists.some((artist) => artist.toLowerCase().includes(term)),
  )
})

function formatDuration(durationMs: number | null): string {
  if (!durationMs || durationMs <= 0) return '-'
  const totalSeconds = Math.round(durationMs / 1000)
  const minutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  return `${minutes}:${String(seconds).padStart(2, '0')}`
}

async function handleParse() {
  const url = form.url.trim()
  if (!url) {
    ElMessage.warning('请先粘贴歌单链接')
    return
  }
  parsing.value = true
  preview.value = null
  try {
    preview.value = await parsePlaylistImport(url)
    if (!form.name.trim()) form.name = preview.value.name
    ElMessage.success(`解析成功，共 ${preview.value.total} 首，已匹配曲库 ${matchedCount.value} 首`)
  } catch (error) {
    ElMessage.error(describeError(error))
  } finally {
    parsing.value = false
  }
}

/**
 * 只导入歌单，不在这个操作中下载未匹配曲目。
 */
async function handleImport() {
  if (!preview.value) return
  importing.value = true
  try {
    const result = await importPlaylist(form.url.trim(), form.name.trim() || undefined, false)
    queuedResult.value = { type: 'playlist', ...result, pending: result.pending }
    ElMessage.success(`歌单已导入：${result.matched} 首已入库，${result.pending} 首未下载`)
    preview.value = null
    form.url = ''
    form.name = ''
    rematchPlaylistId.value = result.playlistId
    await loadPlaylists()
    await loadProgress(result.playlistId)
  } catch (error) {
    ElMessage.error(describeError(error))
  } finally {
    importing.value = false
  }
}

/** 仅将未匹配曲库的歌曲加入下载队列，不创建用户歌单。 */
async function handleQueueDownloads() {
  if (!preview.value) return
  importing.value = true
  try {
    const result = await queuePlaylistDownloads(form.url.trim())
    queuedResult.value = {
      type: 'queue',
      total: result.total,
      matched: result.matched,
      pending: result.queued,
    }
    ElMessage.success(`已加入下载队列：${result.queued} 首待处理，${result.matched} 首已在曲库中跳过`)
    preview.value = null
    form.url = ''
    form.name = ''
  } catch (error) {
    ElMessage.error(describeError(error))
  } finally {
    importing.value = false
  }
}

/** 跳转到「音源下载」页查看队列进度。 */
function goDownloadTasks() {
  router.push({ name: 'source-download' })
}

/** 拉取当前用户歌单列表（补齐选择器数据源）。 */
async function loadPlaylists() {
  try {
    const page = await getPlaylists()
    playlists.value = page.items.map((item) => ({ id: item.id, name: item.name, trackCount: item.trackCount }))
  } catch (error) {
    ElMessage.error(describeError(error, '歌单列表加载失败'))
  }
}

/** 重新补齐选中歌单的缺失曲目：逐条重跑匹配（查曲库 → 搜索音源 → 重建下载任务）。 */
async function handleRematch() {
  if (!rematchPlaylistId.value) {
    ElMessage.warning('请先选择要补齐的歌单')
    return
  }
  rematching.value = true
  rematchResult.value = null
  try {
    rematchResult.value = await rematchPlaylist(rematchPlaylistId.value)
    ElMessage.success('已重新排队，后台补齐中')
    await loadPlaylists()
    await loadProgress()
    startProgressPolling()
  } catch (error) {
    ElMessage.error(describeError(error, '补齐失败'))
  } finally {
    rematching.value = false
  }
}

onMounted(loadPlaylists)
onUnmounted(stopProgressPolling)

/** 跳转到「导入审核」页发布待审核曲目（自动评分低于阈值，需人工发布后才入库）。 */
function goReviewImport() {
  router.push({ name: 'import' })
}

/** 切换歌单时刷新进度面板。 */
function handlePlaylistChange() {
  rematchResult.value = null
  progressFilter.value = 'all'
  void loadProgress().then(() => {
    if (hasActiveWork.value) startProgressPolling()
  })
}
</script>

<template>
  <div class="page">
    <el-card shadow="never">
      <template #header>
        <div class="card-header">
          <span>歌单解析导入</span>
        </div>
      </template>

      <el-alert
        type="info"
        :closable="false"
        class="tip"
        title="支持平台：网易云音乐、QQ 音乐、酷我音乐、酷狗音乐、汽水音乐公开歌单/专辑分享链接（含短链接）、YouTube Music（YouTube Music 需服务端配置网络代理）。粘贴分享链接后点击解析。"
      />

      <div class="url-row">
        <el-input
          v-model="form.url"
          placeholder="粘贴歌单链接，如 https://music.163.com/playlist?id=24381616"
          clearable
          size="large"
          @keyup.enter="handleParse"
        >
          <template #prefix>
            <el-icon><Link /></el-icon>
          </template>
        </el-input>
        <el-button type="primary" size="large" :loading="parsing" @click="handleParse">
          <el-icon v-if="!parsing"><Refresh /></el-icon>
          解析
        </el-button>
      </div>

      <el-alert
        v-if="queuedResult"
        type="success"
        show-icon
        class="queue-result"
        @close="queuedResult = null"
      >
        <template #title>
          <span v-if="queuedResult.type === 'queue'">
            共 {{ queuedResult.total }} 首，{{ queuedResult.matched }} 首已在曲库中跳过，
            <b>{{ queuedResult.pending }} 首已加入后台下载队列</b>。后台会逐首搜索音源并下载，期间可离开本页。
          </span>
          <span v-else-if="queuedResult.queued">
            歌单已生成：共 {{ queuedResult.total }} 首，已入库 {{ queuedResult.matched }} 首，
            <b>{{ queuedResult.pending }} 首已加入后台下载队列</b>。后台会逐首搜索音源并下载，期间可离开本页；
            评分达标的曲目自动入库，评分偏低的会停在「待审核」，需到「导入审核」页发布。
          </span>
          <span v-else>
            歌单已生成：共 {{ queuedResult.total }} 首，已入库 {{ queuedResult.matched }} 首，
            {{ queuedResult.pending }} 首未下载。需要补齐时可在下方「重新补齐」把缺失曲目排入下载队列。
          </span>
          <el-button v-if="queuedResult.type === 'queue' || queuedResult.queued" link type="primary" class="queue-link" @click="goDownloadTasks">
            查看下载任务 →
          </el-button>
        </template>
      </el-alert>
    </el-card>

    <el-card v-if="preview" shadow="never" class="result-card">
      <template #header>
        <div class="card-header">
          <div class="playlist-info">
            <img v-if="preview.coverUrl" :src="preview.coverUrl" class="cover" alt="" />
            <div>
              <div class="playlist-name">
                {{ preview.name }}
                <el-tag size="small" effect="plain">{{ PlaylistImportPlatformLabel[preview.platform] ?? preview.platform }}</el-tag>
              </div>
              <div class="playlist-stats">
                共 {{ preview.total }} 首 · 已匹配曲库 {{ matchedCount }} 首 · 待补齐 {{ unmatchedCount }} 首
              </div>
            </div>
          </div>
          <div class="import-bar">
            <el-input
              v-model="previewKeyword"
              placeholder="搜索曲名 / 艺人 / 专辑"
              clearable
              class="preview-search"
            >
              <template #prefix><el-icon><Search /></el-icon></template>
            </el-input>
            <el-input v-model="form.name" placeholder="导入歌单名称" class="name-input" maxlength="100" />
            <el-button
              type="primary"
              :loading="importing"
              title="仅将未匹配曲目加入后台下载队列，不创建歌单"
              @click="handleQueueDownloads"
            >
              <el-icon v-if="!importing"><Download /></el-icon>
              加入下载队列
            </el-button>
            <el-button
              :disabled="importing"
              title="创建歌单并添加已匹配的曲目；未匹配曲目可之后在歌单中重新补齐"
              @click="handleImport"
            >
              导入歌单
            </el-button>
          </div>
        </div>
      </template>

      <el-table :data="previewTracks" stripe max-height="560">
        <el-table-column type="index" label="#" width="60" />
        <el-table-column prop="title" label="标题" sortable min-width="220" show-overflow-tooltip />
        <el-table-column prop="artists" label="艺人" sortable min-width="160" show-overflow-tooltip>
          <template #default="{ row }">{{ row.artists.join(' / ') || '-' }}</template>
        </el-table-column>
        <el-table-column prop="albumTitle" label="专辑" sortable min-width="160" show-overflow-tooltip>
          <template #default="{ row }">{{ row.albumTitle || '-' }}</template>
        </el-table-column>
        <el-table-column prop="durationMs" label="时长" sortable width="90">
          <template #default="{ row }">{{ formatDuration(row.durationMs) }}</template>
        </el-table-column>
        <el-table-column prop="matched" label="状态" sortable width="110">
          <template #default="{ row }">
            <el-tag v-if="row.matched" type="success" size="small">已匹配</el-tag>
            <el-tag v-else type="warning" size="small">待下载</el-tag>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <el-card shadow="never" class="result-card">
      <template #header>
        <div class="card-header">
          <span>重新补齐缺失曲目</span>
        </div>
      </template>

      <el-alert
        type="info"
        :closable="false"
        class="tip"
        title="歌单里出现「待补齐 / 无音源」曲目，或下载任务丢失时使用：会逐条重跑匹配（先查曲库，命中直接转正），未命中的重新排入后台补齐队列。点击后立即返回，补齐在后台继续，无需停留在本页。"
      />

      <div class="rematch-row">
        <el-select
          v-model="rematchPlaylistId"
          placeholder="选择要补齐的歌单"
          filterable
          class="rematch-select"
          @change="handlePlaylistChange"
        >
          <el-option v-for="item in playlists" :key="item.id" :label="item.name" :value="item.id">
            <span>{{ item.name }}</span>
            <span class="option-count">{{ item.trackCount }} 首</span>
          </el-option>
        </el-select>
        <el-button type="primary" :loading="rematching" @click="handleRematch">
          <el-icon v-if="!rematching"><MagicStick /></el-icon>
          重新补齐
        </el-button>
      </div>

      <div v-if="progressLoaded" class="progress-row">
        <span class="progress-label">补齐进度：</span>
        <el-tag v-if="progress.total === 0" type="success" effect="plain">全部已入库</el-tag>
        <el-tag v-if="progress.downloading" type="primary" effect="plain">下载中 {{ progress.downloading }}</el-tag>
        <el-tag v-if="progress.pending" type="warning" effect="plain">排队中 {{ progress.pending }}</el-tag>
        <el-tag v-if="progress.needsReview" type="info" effect="plain">待审核 {{ progress.needsReview }}</el-tag>
        <el-tag v-if="progress.noSource" type="danger" effect="plain">无音源 {{ progress.noSource }}</el-tag>
        <el-tag v-if="progress.failed" type="danger" effect="plain">失败 {{ progress.failed }}</el-tag>
        <el-tag v-if="progress.rejected" type="info" effect="plain">已拒绝 {{ progress.rejected }}</el-tag>
        <el-tag v-if="progress.skipped" effect="plain">未下载 {{ progress.skipped }}</el-tag>
        <el-button v-if="progress.needsReview" link type="primary" @click="goReviewImport">
          去发布待审核曲目 →
        </el-button>
      </div>

      <div v-if="progressLoaded && progress.needsReview" class="progress-note">
        入库是自动的、按评分决定：评分达标的曲目下载完成后直接入库；这
        <b>{{ progress.needsReview }} 首评分低于自动入库阈值</b>，音频已下载完成但停在「待审核」，
        <b>不需要重新下载</b>，到「导入审核」页确认后即可入库，10 秒内自动进歌单。
      </div>

      <template v-if="progressLoaded && progressRows.length">
        <div class="progress-toolbar">
          <el-radio-group v-model="progressFilter" size="small">
            <el-radio-button value="all">全部 {{ progress.total }}</el-radio-button>
            <el-radio-button value="active">进行中 {{ progress.pending + progress.downloading }}</el-radio-button>
            <el-radio-button value="needs_review">待审核 {{ progress.needsReview }}</el-radio-button>
            <el-radio-button value="issue">异常 {{ progress.noSource + progress.failed + progress.rejected }}</el-radio-button>
          </el-radio-group>
        </div>

        <el-table :data="filteredRows" size="small" max-height="360" class="progress-table">
          <el-table-column type="index" label="#" width="52" />
          <el-table-column prop="title" label="曲目" sortable min-width="200" show-overflow-tooltip />
          <el-table-column prop="artists" label="艺人" sortable min-width="150" show-overflow-tooltip>
            <template #default="{ row }">{{ row.artists || '-' }}</template>
          </el-table-column>
          <el-table-column prop="meta.label" label="状态" sortable width="110">
            <template #default="{ row }">
              <el-tag :type="row.meta.type" size="small" effect="plain">{{ row.meta.label }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="meta.hint" label="说明" sortable min-width="260" show-overflow-tooltip />
        </el-table>
      </template>

      <el-alert
        v-if="rematchResult"
        type="success"
        :closable="false"
        show-icon
        class="rematch-result"
        :title="`检查 ${rematchResult.total} 首：转正 ${rematchResult.promoted} 首 · 重新排队 ${rematchResult.retried} 首 · 仍无音源 ${rematchResult.noSource} 首 · 下载中保持 ${rematchResult.unchanged} 首`"
      />
    </el-card>
  </div>
</template>

<style scoped>
.page {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  flex-wrap: wrap;
}

.tip {
  margin-bottom: 16px;
}

.queue-result {
  margin-top: 16px;
}

.queue-link {
  margin-left: 8px;
}

.url-row {
  display: flex;
  gap: 12px;
}

.rematch-row {
  display: flex;
  align-items: center;
  gap: 12px;
}

.rematch-select {
  width: 320px;
}

.progress-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 16px;
}

.progress-label {
  font-size: 13px;
  color: var(--el-text-color-secondary);
}

.progress-note {
  margin-top: 10px;
  padding: 10px 12px;
  font-size: 13px;
  line-height: 1.6;
  color: var(--el-text-color-regular);
  background: var(--el-fill-color-light);
  border-radius: 6px;
}

.progress-toolbar {
  margin-top: 14px;
}

.progress-table {
  margin-top: 8px;
}

.option-count {
  float: right;
  margin-left: 16px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.rematch-result {
  margin-top: 12px;
}

.playlist-info {
  display: flex;
  align-items: center;
  gap: 12px;
}

.cover {
  width: 48px;
  height: 48px;
  border-radius: 6px;
  object-fit: cover;
}

.playlist-name {
  display: flex;
  align-items: center;
  gap: 8px;
  font-weight: 600;
}

.playlist-stats {
  margin-top: 4px;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

.import-bar {
  display: flex;
  gap: 12px;
}

.name-input {
  width: 260px;
}

.preview-search {
  width: 240px;
}
</style>
