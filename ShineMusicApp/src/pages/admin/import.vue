<script setup lang="ts">
/**
 * 管理员曲库导入审核页：上传音频 → 自动解析 → 人工审核元数据 → 发布入库。
 *
 * 流程：
 * 1. 选择音频文件上传，后端自动完成校验/元数据解析/转码（状态：uploaded → pending_review）
 * 2. 管理员在"待审核"区试听并核对/修正元数据（标题、艺人、专辑、曲序、碟序、年份、流派）
 * 3. 修正后保存，若标题和艺人齐全，状态变为 ready_to_publish
 * 4. 点击"发布入库"，曲目进入正式库可播放；或"拒绝"删除隔离文件
 *
 * 访问控制：onShow 校验 isAdmin，非管理员 redirectTo 首页。
 */
import { computed, ref, onUnmounted } from 'vue'
import { onLoad, onShow } from '@dcloudio/uni-app'
import { useI18n } from 'vue-i18n'
import {
  AlertCircle,
  Check,
  CloudUpload,
  FileUp,
  Music2,
  RefreshCw,
  ShieldCheck,
  Sparkles,
  X,
} from 'lucide-vue-next'
import { isAdmin } from '@/services/session'
import { pickLocalFiles } from '@/utils/app-file-picker'
import { ApiError } from '@/services/http'
import { describeError, ErrorCodes } from '@/services/errors'
import {
  enrichImportItem,
  getImportBatches,
  getPreviewUrl,
  ImportBatchStatusLabel,
  ImportItemStatus,
  ImportItemStatusLabel,
  publishImportItem,
  rejectImportItem,
  unlinkImportItemSongRequest,
  updateImportItem,
  uploadImportFiles,
  uploadImportFilesBatch,
  uploadLyricsFile,
  type LibraryImportBatch,
  type LibraryImportItem,
} from '@/services/admin-import'

// —— 列表 / 选择状态 ——
const { t } = useI18n()
const batches = ref<readonly LibraryImportBatch[]>([])
const selectedItemId = ref('')
const loading = ref(false)
const uploading = ref(false)
const saving = ref(false)
const publishing = ref(false)
const enriching = ref(false)
const loadError = ref('')

// 从需求池「上传并满足」跳转而来时携带的目标需求；发布后自动满足该需求
const targetRequest = ref<{ id: string; title: string; artistName: string } | null>(null)

// 自动刷新定时器
let autoRefreshTimer: ReturnType<typeof setInterval> | null = null

/** 判断条目是否需要显示在审核列表中（排除已发布/已拒绝/处理失败等终态） */
function isActiveItem(status: string): boolean {
  return status !== ImportItemStatus.Published &&
    status !== ImportItemStatus.Rejected &&
    status !== ImportItemStatus.ProcessingFailed
}

/** 过滤后的批次列表：只保留包含非终态条目的批次 */
const activeBatches = computed<LibraryImportBatch[]>(() => {
  return batches.value
    .map(batch => ({
      ...batch,
      items: batch.items.filter(item => isActiveItem(item.status)),
    }))
    .filter(batch => batch.items.length > 0)
})

// —— 编辑表单 ——
const title = ref('')
const artists = ref('')
const albumTitle = ref('')
const trackNumber = ref('')
const discNumber = ref('')
const releaseYear = ref('')
const genres = ref('')
const lyrics = ref('')

// 当前选中的条目（在过滤后的批次 items 中查找）
const selectedItem = computed<LibraryImportItem | null>(() => {
  for (const batch of activeBatches.value) {
    const found = batch.items.find((item) => item.id === selectedItemId.value)
    if (found) return found
  }
  return null
})

// 发布前完整性校验：管理员导入要求歌名、艺人、时长齐全；
// 用户上传的私有曲目（待审核转公开）曲目已存在，由后端把关，前端不拦截
interface PublishCheck { ok: boolean; missing: string[] }
const publishCheck = computed<PublishCheck>(() => {
  const item = selectedItem.value
  if (!item) return { ok: false, missing: [] }
  if (item.status === ImportItemStatus.PendingPublication) return { ok: true, missing: [] }
  const missing: string[] = []
  if (!item.metadata.title?.trim()) missing.push(t('importReview.requiredTitle'))
  if (!item.metadata.artists.length) missing.push(t('importReview.requiredArtist'))
  if (!item.metadata.durationMs) missing.push(t('importReview.requiredDuration'))
  return { ok: missing.length === 0, missing }
})

// 歌词来源徽章：自动匹配（尤其外部源）需要人工核对，用不同颜色提示
const lyricsSourceLabel = computed(() => {
  const source = selectedItem.value?.metadata.lyricsSource
  if (!source) return null
  const labels: Record<string, string> = {
    embedded: t('importReview.lyricsSourceEmbedded'),
    sidecar: t('importReview.lyricsSourceSidecar'),
    lrclib: t('importReview.lyricsSourceAuto'),
    manual: t('importReview.lyricsSourceManual'),
  }
  const confidence = selectedItem.value?.metadata.lyricsConfidence
  const suffix = confidence != null && confidence < 1 ? ` · ${Math.round(confidence * 100)}%` : ''
  return `${labels[source] ?? source}${suffix}`
})
// 外部源自动匹配的歌词可信度低于内嵌/人工，界面上单独标色提醒核对
const lyricsAutoMatched = computed(() => selectedItem.value?.metadata.lyricsSource === 'lrclib')

/** 解码路由 query 参数（uni-app 传参可能是编码后的字符串） */
function decodeQuery(value: unknown): string {
  if (typeof value !== 'string' || !value) return ''
  try {
    return decodeURIComponent(value)
  } catch {
    return value
  }
}

// 从需求池「上传并满足」跳转：记录目标需求，上传时带上 songRequestId，发布后自动满足
onLoad((query) => {
  const id = decodeQuery(query?.songRequestId)
  if (!id) return
  targetRequest.value = {
    id,
    title: decodeQuery(query?.title),
    artistName: decodeQuery(query?.artistName),
  }
})

// 权限拦截
onShow(() => {
  if (!isAdmin.value) {
    uni.redirectTo({ url: '/pages/home/index' })
    return
  }
  void loadBatches()
  startAutoRefresh()
})

onUnmounted(() => {
  stopAutoRefresh()
})

/** 启动自动刷新：每 3 秒轮询一次，有处理中条目时加快到每 2 秒 */
function startAutoRefresh() {
  stopAutoRefresh()
  autoRefreshTimer = setInterval(() => {
    // 静默刷新（不显示 loading）
    void refreshBatchesSilently()
  }, 3000)
}

/** 停止自动刷新 */
function stopAutoRefresh() {
  if (autoRefreshTimer) {
    clearInterval(autoRefreshTimer)
    autoRefreshTimer = null
  }
}

/** 静默刷新：不显示 loading 状态，失败也不报错 */
async function refreshBatchesSilently() {
  try {
    const result = await getImportBatches(undefined, 1, 50)
    batches.value = result.items
    // 若当前选中项已不在列表中，清空选择
    if (selectedItemId.value && !selectedItem.value) {
      selectedItemId.value = ''
    }
  } catch {
    // 静默失败，不打扰用户
  }
}

/** 拉取导入批次列表 */
async function loadBatches() {
  loading.value = true
  loadError.value = ''
  try {
    const result = await getImportBatches(undefined, 1, 50)
    batches.value = result.items
    // 若当前选中项已不在列表中，清空选择
    if (selectedItemId.value && !selectedItem.value) {
      selectedItemId.value = ''
    }
  } catch (error) {
    // 展示具体原因（网络不可达 / 权限不足 / 服务端错误），避免统一文案掩盖真实故障
    loadError.value = describeError(error, t('importReview.loadFailed'))
  } finally {
    loading.value = false
  }
}

/** chooseFile 返回的文件项：H5 下带原生 File 对象，可用于一次 multipart 上传多个文件 */
interface PickedFile {
  path: string
  name: string
  file: File | undefined
}

/**
 * 归一化 uni.chooseFile / uni.chooseMessageFile 返回的 tempFiles。
 * 各平台该字段可能是单个文件对象或文件数组，此处统一抽取为 { path, name, file } 数组。
 * @param tempFiles chooseFile 回调中的 tempFiles 原值
 * @returns 有效的文件项数组
 */
function extractPickedFiles(tempFiles: unknown): PickedFile[] {
  const list = Array.isArray(tempFiles) ? tempFiles : [tempFiles]
  const picked: PickedFile[] = []
  for (const entry of list) {
    const item = entry as { path?: string; name?: string; file?: File } | null | undefined
    if (!item?.path) continue
    picked.push({ path: item.path, name: item.name ?? '', file: item.file })
  }
  return picked
}

/**
 * 只抽取本地文件路径（供 uni.uploadFile 这类单文件接口使用）。
 * @param tempFiles chooseFile 回调中的 tempFiles 原值
 * @returns 有效的本地文件路径数组
 */
function extractTempFilePaths(tempFiles: unknown): string[] {
  return extractPickedFiles(tempFiles).map((entry) => entry.path)
}

/** 选择并上传音频文件（可同时选中同名 .lrc/.txt，后端自动配对） */
function handleUpload() {
  if (uploading.value) return
  // #ifdef H5
  uni.chooseFile({
    count: 20,
    extension: ['.mp3', '.flac', '.wav', '.m4a', '.aac', '.ogg', '.wma', '.aiff', '.ape', '.lrc', '.txt'],
    success: async (res) => {
      await doUpload(extractPickedFiles(res.tempFiles))
    },
  })
  // #endif
  // #ifdef APP-PLUS
  // App 端没有 chooseFile：走原生 SAF 选择器（uni.chooseMessageFile 是小程序 API，App 上不弹任何界面）
  pickLocalFiles()
    .then((files) => doUpload(files.map((entry) => ({ path: entry.path, name: entry.name, file: undefined }))))
    .catch((error) => {
      uni.showToast({ title: t('importReview.uploadFailedWith', { message: (error as Error).message }), icon: 'none' })
    })
  // #endif
  // #ifdef MP
  uni.chooseMessageFile({
    count: 10,
    type: 'file',
    extension: ['mp3', 'flac', 'wav', 'm4a', 'aac', 'ogg', 'wma', 'aiff', 'ape'],
    success: async (res) => {
      await doUpload(extractPickedFiles(res.tempFiles))
    },
  })
  // #endif
}

/**
 * 执行上传。
 * H5：所有文件（音频 + 同名歌词）合并为一个批次，一次 multipart 请求上传，
 *     后端按文件名主干把 .lrc/.txt 配到对应音频，实现真正的批量导入。
 * 非 H5：uni.uploadFile 只支持单文件，逐个建批次；歌词文件跳过（该端不支持配对）。
 */
async function doUpload(files: PickedFile[]) {
  if (!files.length) return
  uploading.value = true
  let successCount = 0

  // #ifdef H5
  const nativeFiles = files
    .map((entry) => entry.file)
    .filter((file): file is File => file instanceof File)
  if (nativeFiles.length === files.length) {
    try {
      const batch = await uploadImportFilesBatch(nativeFiles, targetRequest.value?.id)
      successCount = batch.items.length
    } catch (err) {
      uni.showToast({ title: t('importReview.uploadFailedWith', { message: (err as Error).message }), icon: 'none' })
    }
    uploading.value = false
    if (successCount > 0) {
      uni.showToast({ title: t('importReview.uploadedCount', { count: successCount }), icon: 'success' })
      await loadBatches()
    }
    return
  }
  // #endif

  for (const entry of files) {
    if (/\.(lrc|txt)$/i.test(entry.name || entry.path)) continue
    try {
      await uploadImportFiles([entry.path], targetRequest.value?.id)
      successCount++
    } catch (err) {
      uni.showToast({ title: t('importReview.uploadFailedWith', { message: (err as Error).message }), icon: 'none' })
    }
  }
  uploading.value = false
  if (successCount > 0) {
    uni.showToast({ title: t('importReview.uploadedFilesCount', { count: successCount }), icon: 'success' })
    await loadBatches()
  }
}

/** 选中条目并填充编辑表单 */
function selectItem(item: LibraryImportItem) {
  selectedItemId.value = item.id
  title.value = item.metadata.title ?? ''
  artists.value = item.metadata.artists.join('、')
  albumTitle.value = item.metadata.albumTitle ?? ''
  trackNumber.value = item.metadata.trackNumber?.toString() ?? ''
  discNumber.value = item.metadata.discNumber?.toString() ?? ''
  releaseYear.value = item.metadata.releaseYear?.toString() ?? ''
  genres.value = item.metadata.genres.join('、')
  lyrics.value = item.metadata.lyrics ?? ''
}

/** 拆分多值文本（顿号/中英文逗号） */
function splitValues(value: string): string[] {
  return [...new Set(value.split(/[、,，]/).map((s) => s.trim()).filter(Boolean))]
}

/** 解析可选正整数 */
function parseOptionalNumber(value: string): number | null {
  const normalized = value.trim()
  if (!normalized) return null
  const parsed = Number(normalized)
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null
}

/** 一键补全：调度歌词链 + iTunes 元数据，只填充表单中仍为空的字段（不覆盖人工输入） */
async function handleEnrich() {
  const item = selectedItem.value
  if (!item || enriching.value) return
  enriching.value = true
  try {
    const result = await enrichImportItem(item.id)
    // 就地更新列表中的条目（后端已落库）
    for (const batch of batches.value) {
      const idx = batch.items.findIndex((i) => i.id === result.item.id)
      if (idx >= 0) {
        batch.items.splice(idx, 1, result.item)
        break
      }
    }
    // 只填表单空字段，保留管理员已输入的内容
    const m = result.item.metadata
    if (!artists.value.trim() && m.artists.length > 0) artists.value = m.artists.join('、')
    if (!albumTitle.value.trim() && m.albumTitle) albumTitle.value = m.albumTitle
    if (!trackNumber.value.trim() && m.trackNumber) trackNumber.value = String(m.trackNumber)
    if (!discNumber.value.trim() && m.discNumber) discNumber.value = String(m.discNumber)
    if (!releaseYear.value.trim() && m.releaseYear) releaseYear.value = String(m.releaseYear)
    if (!genres.value.trim() && m.genres.length > 0) genres.value = m.genres.join('、')
    if (!lyrics.value.trim() && m.lyrics) lyrics.value = m.lyrics
    uni.showToast({
      title: result.filled.length > 0
        ? t('importReview.autoFillFilled', { count: result.filled.length })
        : t('importReview.autoFillNoHit'),
      icon: 'none',
    })
  } catch (error) {
    uni.showToast({ title: describeError(error, t('importReview.enrichFailed')), icon: 'none' })
  } finally {
    enriching.value = false
  }
}

/** 保存元数据修正 */
async function saveMetadata() {
  const item = selectedItem.value
  if (!item || saving.value) return
  if (!title.value.trim() || !artists.value.trim()) {
    uni.showToast({ title: t('importReview.titleArtistRequired'), icon: 'none' })
    return
  }
  saving.value = true
  try {
    const updated = await updateImportItem(item.id, {
      title: title.value.trim(),
      artists: splitValues(artists.value),
      albumTitle: albumTitle.value.trim() || null,
      trackNumber: parseOptionalNumber(trackNumber.value),
      discNumber: parseOptionalNumber(discNumber.value),
      releaseYear: parseOptionalNumber(releaseYear.value),
      genres: splitValues(genres.value),
      lyrics: lyrics.value || null,
    })
    // 就地更新列表中的条目
    for (const batch of batches.value) {
      const idx = batch.items.findIndex((i) => i.id === updated.id)
      if (idx >= 0) {
        batch.items.splice(idx, 1, updated)
        break
      }
    }
    uni.showToast({ title: t('importReview.metadataSaved'), icon: 'success' })
  } catch {
    uni.showToast({ title: t('importReview.saveFailed'), icon: 'none' })
  } finally {
    saving.value = false
  }
}

/** 上传歌词文件（.lrc / .txt），后端解析后写入歌词字段 */
function handleUploadLyrics() {
  const item = selectedItem.value
  if (!item) return
  // #ifdef H5
  uni.chooseFile({
    count: 1,
    extension: ['.lrc', '.txt'],
    success: async (res) => {
      const path = extractTempFilePaths(res.tempFiles)[0]
      if (!path) return
      try {
        const updated = await uploadLyricsFile(item.id, path)
        for (const batch of batches.value) {
          const idx = batch.items.findIndex((i) => i.id === updated.id)
          if (idx >= 0) { batch.items.splice(idx, 1, updated); break }
        }
        lyrics.value = updated.metadata.lyrics ?? ''
        uni.showToast({ title: t('importReview.lyricsImported'), icon: 'success' })
      } catch (err) {
        uni.showToast({ title: t('importReview.lyricsUploadFailed', { message: (err as Error).message }), icon: 'none' })
      }
    },
  })
  // #endif
  // #ifdef APP-PLUS
  pickLocalFiles()
    .then(async (files) => {
      const path = files[0]?.path
      if (!path) return
      try {
        const updated = await uploadLyricsFile(item.id, path)
        for (const batch of batches.value) {
          const idx = batch.items.findIndex((i) => i.id === updated.id)
          if (idx >= 0) { batch.items.splice(idx, 1, updated); break }
        }
        lyrics.value = updated.metadata.lyrics ?? ''
        uni.showToast({ title: t('importReview.lyricsImported'), icon: 'success' })
      } catch (err) {
        uni.showToast({ title: t('importReview.lyricsUploadFailed', { message: (err as Error).message }), icon: 'none' })
      }
    })
    .catch((error) => {
      uni.showToast({ title: t('importReview.lyricsUploadFailed', { message: (error as Error).message }), icon: 'none' })
    })
  // #endif
  // #ifdef MP
  uni.chooseMessageFile({
    count: 1,
    type: 'file',
    extension: ['lrc', 'txt'],
    success: async (res) => {
      const path = extractTempFilePaths(res.tempFiles)[0]
      if (!path) return
      try {
        const updated = await uploadLyricsFile(item.id, path)
        for (const batch of batches.value) {
          const idx = batch.items.findIndex((i) => i.id === updated.id)
          if (idx >= 0) { batch.items.splice(idx, 1, updated); break }
        }
        lyrics.value = updated.metadata.lyrics ?? ''
        uni.showToast({ title: t('importReview.lyricsImported'), icon: 'success' })
      } catch (err) {
        uni.showToast({ title: t('importReview.lyricsUploadFailed', { message: (err as Error).message }), icon: 'none' })
      }
    },
  })
  // #endif
}

/** 发布入库 */
async function handlePublish() {
  const item = selectedItem.value
  if (!item || publishing.value) return
  // 用户上传的私有曲目：待审核转公开；管理员导入：可发布
  if (item.status !== ImportItemStatus.ReadyToPublish && item.status !== ImportItemStatus.PendingPublication) {
    uni.showToast({ title: t('importReview.saveMetadataFirst'), icon: 'none' })
    return
  }
  if (!publishCheck.value.ok) {
    uni.showToast({ title: t('importReview.missingFields', { fields: publishCheck.value.missing.join(t('importReview.valueSeparator')) }), icon: 'none' })
    return
  }
  const promoting = item.status === ImportItemStatus.PendingPublication
  publishing.value = true
  try {
    await publishImportItem(item.id)
    uni.showToast({ title: promoting ? t('importReview.promotedToPublic') : t('importReview.publishedToast'), icon: 'success' })
    await loadBatches()
    selectedItemId.value = ''
    targetRequest.value = null
  } catch (error) {
    // 仅当全局配置允许管理员覆盖时（后端返回 override_required），才引导强制关联
    if (
      error instanceof ApiError &&
      error.code === ErrorCodes.SongRequestMismatchOverrideRequired &&
      targetRequest.value
    ) {
      promptForceFulfill(item)
    } else {
      uni.showToast({
        title: describeError(error, t('importReview.publishFailed')),
        icon: 'none',
      })
    }
  } finally {
    publishing.value = false
  }
}

/** 曲目与需求不一致时：二次确认 + 填写原因后强制关联发布 */
function promptForceFulfill(item: LibraryImportItem) {
  const target = targetRequest.value
  if (!target) return
  uni.showModal({
    title: t('importReview.mismatchTitle'),
    content: t('importReview.mismatchContent', { title: target.title, artist: target.artistName }),
    editable: true,
    placeholderText: t('importReview.mismatchPlaceholder'),
    confirmText: t('importReview.forceLinkBtn'),
    success: async (res) => {
      if (!res.confirm) return
      const raw = (res as unknown as { content?: string }).content ?? ''
      const reason = raw.trim() || t('importReview.forceLinkDefaultReason')
      publishing.value = true
      try {
        await publishImportItem(item.id, { forceFulfill: true, overrideReason: reason })
        uni.showToast({ title: t('importReview.forceLinkPublished'), icon: 'success' })
        await loadBatches()
        selectedItemId.value = ''
        targetRequest.value = null
      } catch (err) {
        uni.showToast({ title: err instanceof ApiError ? err.message : t('importReview.forceLinkFailed'), icon: 'none' })
      } finally {
        publishing.value = false
      }
    },
  })
}

/** 拒绝条目 */
async function handleReject() {
  const item = selectedItem.value
  if (!item) return
  uni.showModal({
    title: t('importReview.rejectTitle'),
    content: t('importReview.rejectContent', { name: item.metadata.title || item.sourceFile.displayName }),
    confirmText: t('importReview.rejectBtn'),
    confirmColor: '#d0553a',
    success: async ({ confirm }) => {
      if (!confirm) return
      try {
        await rejectImportItem(item.id)
        uni.showToast({ title: t('importReview.rejectedToast'), icon: 'success' })
        await loadBatches()
        selectedItemId.value = ''
      } catch {
        uni.showToast({ title: t('importReview.operationFailed'), icon: 'none' })
      }
    },
  })
}

/** 解除当前条目与点歌需求的关联（批量上传误挂时使用） */
function handleUnlinkRequest() {
  const item = selectedItem.value
  if (!item || !item.songRequestId) return
  uni.showModal({
    title: t('importReview.unlinkTitle'),
    content: t('importReview.unlinkContent'),
    confirmText: t('importReview.unlinkConfirm'),
    success: async ({ confirm }) => {
      if (!confirm) return
      try {
        await unlinkImportItemSongRequest(item.id)
        uni.showToast({ title: t('importReview.unlinkedToast'), icon: 'success' })
        await loadBatches()
      } catch {
        uni.showToast({ title: t('importReview.operationFailed'), icon: 'none' })
      }
    },
  })
}

/** 格式化时长 */
function formatDuration(ms: number | null): string {
  if (!ms) return t('importReview.unknown')
  const totalSec = Math.round(ms / 1000)
  const min = Math.floor(totalSec / 60)
  const sec = totalSec % 60
  return `${min}:${sec.toString().padStart(2, '0')}`
}

/** 格式化文件大小 */
function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

/** 状态标签：按 importStatus.* i18n key 实时本地化，未知状态原样返回 */
function statusLabel(labels: Record<string, string>, status: string): string {
  const key = labels[status]
  return key ? t(key) : status
}

/** 状态样式映射 */
function statusClass(status: string): string {
  switch (status) {
    case 'published':
    case 'completed':
    case 'ready_to_publish':
      return 'status-ok'
    case 'pending_review':
      return 'status-warn'
    case 'rejected':
    case 'processing_failed':
    case 'failed':
    case 'completed_with_errors':
      return 'status-error'
    default:
      return 'status-info'
  }
}
</script>

<template>
  <scroll-view class="import-page" scroll-y :show-scrollbar="false">
    <!-- 页头 -->
    <view class="page-heading">
      <view class="heading-icon"><ShieldCheck :size="20" /></view>
      <view class="heading-copy">
        <text class="heading-title">{{ $t('importReview.pageTitle') }}</text>
        <text class="heading-subtitle">{{ $t('importReview.headingSubtitle') }}</text>
      </view>
    </view>

    <!-- 从需求池跳转：显示目标需求，发布后自动满足 -->
    <view v-if="targetRequest" class="target-banner">
      <view class="target-banner-title">{{ $t('importReview.targetBannerTitle') }}</view>
      <view class="target-banner-body">{{ $t('importReview.targetBannerBody', { title: targetRequest.title, artist: targetRequest.artistName }) }}</view>
      <view class="target-banner-hint">{{ $t('importReview.targetBannerHint') }}</view>
    </view>

    <!-- 上传按钮 -->
    <view class="upload-row">
      <view class="upload-button" :class="{ disabled: uploading }" @tap="handleUpload">
        <CloudUpload :size="18" />
        <text>{{ uploading ? $t('importReview.uploading') : $t('importReview.chooseAudioToUpload') }}</text>
      </view>
      <view class="refresh-button" @tap="loadBatches">
        <RefreshCw :size="17" />
      </view>
    </view>

    <view v-if="loading" class="state-message">{{ $t('importReview.loadingRecords') }}</view>
    <view v-else-if="loadError" class="state-message error"><AlertCircle :size="17" /><text>{{ loadError }}</text></view>
    <view v-else-if="!activeBatches.length" class="state-message">
      <Music2 :size="28" />
      <text>{{ $t('importReview.noRecords') }}</text>
    </view>

    <!-- 批次列表 -->
    <view v-else class="batch-list">
      <view v-for="batch in activeBatches" :key="batch.id" class="batch-card">
        <view class="batch-header">
          <view class="batch-title">
            <text class="batch-name">{{ $t('importReview.batchTitle', { count: batch.items.length }) }}</text>
            <text class="batch-time">{{ new Date(batch.createdAt).toLocaleString() }}</text>
          </view>
          <text class="batch-status" :class="statusClass(batch.status)">
            {{ statusLabel(ImportBatchStatusLabel, batch.status) }}
          </text>
        </view>

        <!-- 批次内条目列表 -->
        <view class="item-list">
          <view
            v-for="item in batch.items"
            :key="item.id"
            class="item-row"
            :class="{ active: selectedItemId === item.id, processing: item.status === ImportItemStatus.Uploaded || item.status === ImportItemStatus.Validating || item.status === ImportItemStatus.ParsingMetadata || item.status === ImportItemStatus.Transcoding }"
            @tap="selectItem(item)"
          >
            <view class="item-thumb">
              <image
                v-if="item.metadata.artworkAvailable"
                :src="`/api/v1/admin/library/import-items/${item.id}/preview`"
                mode="aspectFill"
                class="thumb-img"
              />
              <view v-else class="thumb-placeholder"><Music2 :size="18" /></view>
            </view>
            <view class="item-copy">
              <text class="item-title">{{ item.metadata.title || $t('importReview.untitled') }}</text>
              <text class="item-meta">
                {{ item.metadata.artists.join($t('importReview.valueSeparator')) || $t('importReview.missingArtist') }}
                <text v-if="item.metadata.durationMs"> · {{ formatDuration(item.metadata.durationMs) }}</text>
              </text>
            </view>
            <view class="item-right">
              <text class="item-status" :class="statusClass(item.status)">
                {{ statusLabel(ImportItemStatusLabel, item.status) }}
              </text>
              <!-- 与目标需求不匹配时提前提示，避免传完才发现传错歌 -->
              <text
                v-if="item.songRequestMatch && item.songRequestMatch.kind !== 'match'"
                class="match-flag"
                :class="item.songRequestMatch.kind"
              >
                {{ item.songRequestMatch.kind === 'fuzzy' ? $t('importReview.matchFuzzy') : $t('importReview.matchMismatch') }}
              </text>
              <view
                v-if="item.status === ImportItemStatus.Uploaded || item.status === ImportItemStatus.Validating || item.status === ImportItemStatus.ParsingMetadata || item.status === ImportItemStatus.Transcoding"
                class="processing-dot"
              ></view>
            </view>
          </view>
        </view>
      </view>
    </view>

    <!-- 审核详情面板 -->
    <view v-if="selectedItem" class="detail-panel">
      <view class="detail-heading">
        <text class="detail-title">{{ $t('importReview.reviewDetail') }}</text>
        <text class="detail-status" :class="statusClass(selectedItem.status)">
          {{ statusLabel(ImportItemStatusLabel, selectedItem.status) }}
        </text>
      </view>

      <!-- 元数据概览 -->
      <view class="meta-grid">
        <view class="meta-cell">
          <text class="meta-label">{{ $t('importReview.fileLabel') }}</text>
          <text class="meta-value">{{ selectedItem.sourceFile.displayName }}</text>
        </view>
        <view class="meta-cell">
          <text class="meta-label">{{ $t('importReview.sizeLabel') }}</text>
          <text class="meta-value">{{ formatSize(selectedItem.sourceFile.sizeBytes) }}</text>
        </view>
        <view class="meta-cell">
          <text class="meta-label">{{ $t('importReview.formatLabel') }}</text>
          <text class="meta-value">{{ selectedItem.metadata.container || '?' }} / {{ selectedItem.metadata.codec || '?' }}</text>
        </view>
        <view class="meta-cell">
          <text class="meta-label">{{ $t('importReview.bitrateLabel') }}</text>
          <text class="meta-value">{{ selectedItem.metadata.bitrateKbps ? selectedItem.metadata.bitrateKbps + ' kbps' : '?' }}</text>
        </view>
        <view class="meta-cell">
          <text class="meta-label">{{ $t('importReview.durationLabel') }}</text>
          <text class="meta-value">{{ formatDuration(selectedItem.metadata.durationMs) }}</text>
        </view>
        <view class="meta-cell">
          <text class="meta-label">{{ $t('importReview.transcodeLabel') }}</text>
          <text class="meta-value">{{ selectedItem.transcode.status || '-' }}</text>
        </view>
      </view>

      <!-- 告警 -->
      <view v-if="selectedItem.warnings.length" class="warning-box">
        <AlertCircle :size="16" />
        <text>{{ selectedItem.warnings.join($t('importReview.valueSeparator')) }}</text>
      </view>

      <!-- 试听播放器 -->
      <view v-if="selectedItem.status === ImportItemStatus.PendingReview || selectedItem.status === ImportItemStatus.ReadyToPublish || selectedItem.status === ImportItemStatus.PendingPublication" class="player-box">
        <text class="player-label">{{ $t('importReview.previewLabel') }}</text>
        <audio
          :src="getPreviewUrl(selectedItem.id)"
          controls
          class="audio-player"
        />
      </view>

      <!-- 元数据编辑表单 -->
      <view class="edit-form">
        <text class="form-heading">{{ $t('importReview.fixMetadata') }}</text>
        <label class="form-field"><text>{{ $t('importReview.fieldTitleRequired') }}</text><input v-model="title" maxlength="300" :placeholder="$t('importReview.titlePlaceholder')" /></label>
        <label class="form-field"><text>{{ $t('importReview.fieldArtistRequired') }}</text><input v-model="artists" :placeholder="$t('importReview.artistsPlaceholder')" maxlength="2000" /></label>
        <label class="form-field"><text>{{ $t('importReview.fieldAlbum') }}</text><input v-model="albumTitle" maxlength="300" :placeholder="$t('importReview.albumPlaceholder')" /></label>
        <view class="number-grid">
          <label class="form-field"><text>{{ $t('importReview.trackNumberLabel') }}</text><input v-model="trackNumber" type="number" /></label>
          <label class="form-field"><text>{{ $t('importReview.discNumberLabel') }}</text><input v-model="discNumber" type="number" /></label>
          <label class="form-field"><text>{{ $t('importReview.yearLabel') }}</text><input v-model="releaseYear" type="number" /></label>
        </view>
        <label class="form-field"><text>{{ $t('importReview.fieldGenre') }}</text><input v-model="genres" :placeholder="$t('importReview.genresPlaceholder')" maxlength="3000" /></label>
        <label class="form-field">
          <view class="field-label-row">
            <view class="lyrics-label">
              <text>{{ $t('importReview.lyricsLabel') }}</text>
              <text v-if="lyricsSourceLabel" class="lyrics-source" :class="{ auto: lyricsAutoMatched }">
                {{ lyricsSourceLabel }}
              </text>
            </view>
            <view v-if="!lyrics?.trim()" class="upload-lyrics-btn" @tap="handleUploadLyrics">
              <FileUp :size="13" /><text>{{ $t('importReview.uploadLyricsFile') }}</text>
            </view>
          </view>
          <textarea v-model="lyrics" :placeholder="$t('importReview.lyricsPlaceholder')" class="lyrics-textarea" />
        </label>

        <!-- 发布前完整性校验提示 -->
        <view v-if="selectedItem.status === ImportItemStatus.ReadyToPublish || selectedItem.status === ImportItemStatus.PendingPublication" class="publish-check">
          <view v-if="selectedItem.status === ImportItemStatus.PendingPublication" class="check-ok"><Check :size="14" /><text>{{ $t('importReview.pendingPublicationHint') }}</text></view>
          <view v-else-if="publishCheck.ok" class="check-ok"><Check :size="14" /><text>{{ $t('importReview.publishCheckOk') }}</text></view>
          <view v-else class="check-missing"><AlertCircle :size="14" /><text>{{ $t('importReview.publishCheckMissing', { fields: publishCheck.missing.join($t('importReview.valueSeparator')) }) }}</text></view>
        </view>

        <!-- 与目标需求的匹配校验结果：一致=绿、有差异=黄、不一致=红（红且 blocking 时发布必被拒） -->
        <view
          v-if="selectedItem.songRequestMatch"
          class="match-box"
          :class="selectedItem.songRequestMatch.kind"
        >
          <Check v-if="selectedItem.songRequestMatch.kind === 'match'" :size="16" />
          <AlertCircle v-else :size="16" />
          <text>{{ selectedItem.songRequestMatch.message }}</text>
        </view>

        <!-- 已关联点歌需求：可解绑（批量上传误挂时使用） -->
        <view v-if="selectedItem.songRequestId" class="linked-request">
          <text class="linked-request-text">{{ $t('importReview.linkedRequestHint') }}</text>
          <view class="unlink-button" @tap="handleUnlinkRequest"><text>{{ $t('importReview.unlinkBtn') }}</text></view>
        </view>

        <view class="action-row">
          <view
            v-if="selectedItem.status !== ImportItemStatus.Published"
            class="enrich-button"
            :class="{ disabled: enriching }"
            @tap="handleEnrich"
          >
            <Sparkles :size="17" /><text>{{ enriching ? $t('importReview.autoFilling') : $t('importReview.autoFillBtn') }}</text>
          </view>
          <view class="save-button" :class="{ disabled: saving }" @tap="saveMetadata">
            <Check :size="17" /><text>{{ saving ? $t('importReview.saving') : $t('importReview.saveMetadata') }}</text>
          </view>
          <view
            v-if="selectedItem.status === ImportItemStatus.ReadyToPublish || selectedItem.status === ImportItemStatus.PendingPublication"
            class="publish-button"
            :class="{ disabled: publishing || !publishCheck.ok }"
            @tap="handlePublish"
          >
            <Check :size="17" /><text>{{ publishing ? $t('importReview.publishing') : (selectedItem.status === ImportItemStatus.PendingPublication ? $t('importReview.promoteToLibrary') : $t('importReview.publishToLibrary')) }}</text>
          </view>
          <view
            v-if="selectedItem.status !== ImportItemStatus.Published"
            class="reject-button"
            @tap="handleReject"
          >
            <X :size="17" /><text>{{ $t('importReview.rejectBtn') }}</text>
          </view>
        </view>
      </view>
    </view>

    <view class="bottom-space"></view>
  </scroll-view>
</template>

<style lang="scss" scoped>
/* 曲库导入审核（方向 B · 玻璃质感）：深色底 + 彩色光晕 + 毛玻璃卡片 */
.import-page { height: 100vh; padding: 30rpx 34rpx 0; box-sizing: border-box; background: var(--sm-page-bg); }
.page-heading { display: flex; align-items: center; gap: 16rpx; padding: 10rpx 0 28rpx; }
.heading-icon { display: grid; place-items: center; width: 68rpx; height: 68rpx; border-radius: 18rpx; background: var(--sm-accent-soft); border: 1rpx solid var(--sm-border); color: var(--sm-accent); }
.heading-copy { display: flex; flex-direction: column; gap: 6rpx; }
.heading-title { color: var(--sm-text); font-size: 29rpx; font-weight: 800; }
.heading-subtitle { color: var(--sm-text-2); font-size: 17rpx; }

.upload-row { display: flex; gap: 12rpx; padding-bottom: 24rpx; }
.target-banner { margin-bottom: 22rpx; padding: 20rpx 24rpx; border: 1rpx solid var(--sm-accent-soft-border); border-radius: 18rpx; background: var(--sm-accent-soft); }
.target-banner-title { color: var(--sm-accent); font-size: 17rpx; font-weight: 800; }
.target-banner-body { margin-top: 8rpx; color: var(--sm-text); font-size: 22rpx; font-weight: 700; }
.target-banner-hint { margin-top: 8rpx; color: var(--sm-text-2); font-size: 16rpx; }
.upload-button { display: flex; flex: 1; align-items: center; justify-content: center; gap: 10rpx; height: 82rpx; border-radius: 16rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 18rpx; font-weight: 700; }
.upload-button.disabled { opacity: .55; }
.refresh-button { display: grid; place-items: center; width: 82rpx; height: 82rpx; border-radius: 16rpx; background: var(--sm-surface-3); color: var(--sm-text-2); border: 1rpx solid var(--sm-border); }

.state-message { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 12rpx; min-height: 240rpx; color: var(--sm-text-2); font-size: 18rpx; }
.state-message.error { color: var(--sm-danger); flex-direction: row; gap: 8rpx; }

.batch-list { display: flex; flex-direction: column; gap: 20rpx; }
.batch-card { border: 1rpx solid var(--sm-border); border-radius: 16rpx; background: var(--sm-surface); overflow: hidden; -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); }
.batch-header { display: flex; align-items: center; justify-content: space-between; padding: 20rpx 22rpx; border-bottom: 1rpx solid var(--sm-divider); }
.batch-title { display: flex; flex-direction: column; gap: 4rpx; }
.batch-name { color: var(--sm-text); font-size: 18rpx; font-weight: 700; }
.batch-time { color: var(--sm-text-3); font-size: 14rpx; }
.batch-status { padding: 6rpx 16rpx; border-radius: 20rpx; font-size: 14rpx; font-weight: 600; }

.item-list { display: flex; flex-direction: column; }
.item-row { display: flex; align-items: center; gap: 14rpx; padding: 16rpx 22rpx; border-bottom: 1rpx solid var(--sm-surface-2); }
.item-row:last-child { border-bottom: 0; }
.item-row.active { background: var(--sm-accent-soft); }
.item-row.processing { opacity: .7; }
.item-thumb { flex: none; width: 72rpx; height: 72rpx; border-radius: 12rpx; overflow: hidden; background: var(--sm-surface-3); }
.thumb-img { width: 100%; height: 100%; }
.thumb-placeholder { display: grid; place-items: center; width: 100%; height: 100%; color: var(--sm-text-3); }
.item-copy { display: flex; flex: 1; flex-direction: column; gap: 4rpx; min-width: 0; }
.item-title { overflow: hidden; color: var(--sm-text); font-size: 18rpx; font-weight: 700; text-overflow: ellipsis; white-space: nowrap; }
.item-meta { overflow: hidden; color: var(--sm-text-2); font-size: 14rpx; text-overflow: ellipsis; white-space: nowrap; }
.item-status { flex: none; padding: 5rpx 14rpx; border-radius: 16rpx; font-size: 13rpx; font-weight: 600; }
.item-right { display: flex; align-items: center; gap: 8rpx; }
/* 与目标需求的匹配标记：黄色=有差异、红色=不匹配（列表层提前预警） */
.match-flag { flex: none; padding: 4rpx 12rpx; border-radius: 14rpx; font-size: 12rpx; font-weight: 700; }
.match-flag.fuzzy { background: var(--sm-warn-soft); color: var(--sm-warn); }
.match-flag.mismatch { background: var(--sm-danger-soft); color: var(--sm-danger); }
.processing-dot { width: 8rpx; height: 8rpx; border-radius: 50%; background: var(--sm-accent); animation: pulse 1.2s ease-in-out infinite; }
@keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: .3; } }

.status-ok { background: var(--sm-accent-soft); color: var(--sm-accent); }
.status-warn { background: var(--sm-warn-soft); color: var(--sm-warn); }
.status-error { background: var(--sm-danger-soft); color: var(--sm-danger); }
.status-info { background: var(--sm-surface-3); color: var(--sm-text-2); }

.detail-panel { margin-top: 24rpx; padding: 24rpx; border: 1rpx solid var(--sm-border); border-radius: 16rpx; background: var(--sm-surface); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); }
.detail-heading { display: flex; align-items: center; justify-content: space-between; padding-bottom: 16rpx; border-bottom: 1rpx solid var(--sm-divider); }
.detail-title { color: var(--sm-text); font-size: 20rpx; font-weight: 800; }
.detail-status { padding: 6rpx 16rpx; border-radius: 20rpx; font-size: 14rpx; font-weight: 600; }

.meta-grid { display: grid; grid-template-columns: repeat(2, 1fr); gap: 14rpx; margin-top: 18rpx; }
.meta-cell { display: flex; flex-direction: column; gap: 4rpx; padding: 12rpx 14rpx; border-radius: 10rpx; background: var(--sm-surface-2); }
.meta-label { color: var(--sm-text-3); font-size: 13rpx; }
.meta-value { color: var(--sm-text); font-size: 16rpx; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

.warning-box { display: flex; align-items: center; gap: 8rpx; margin-top: 16rpx; padding: 12rpx 16rpx; border-radius: 10rpx; background: var(--sm-warn-soft); color: var(--sm-warn); font-size: 14rpx; }
/* 需求匹配校验结论：一致（绿）/ 有差异（黄）/ 不一致（红） */
.match-box { display: flex; align-items: flex-start; gap: 8rpx; margin-top: 16rpx; padding: 14rpx 16rpx; border-radius: 12rpx; font-size: 14rpx; line-height: 1.5; }
.match-box.match { background: var(--sm-accent-soft); color: var(--sm-accent); }
.match-box.fuzzy { background: var(--sm-warn-soft); color: var(--sm-warn); }
.match-box.mismatch { background: var(--sm-danger-soft); color: var(--sm-danger); }

.player-box { margin-top: 18rpx; padding: 16rpx; border-radius: 12rpx; background: var(--sm-surface-2); }
.player-label { display: block; margin-bottom: 10rpx; color: var(--sm-text-2); font-size: 15rpx; font-weight: 600; }
.audio-player { width: 100%; }

.edit-form { margin-top: 20rpx; }
.form-heading { color: var(--sm-text); font-size: 18rpx; font-weight: 800; padding-bottom: 12rpx; }
.form-field { display: flex; flex-direction: column; gap: 8rpx; margin-top: 14rpx; color: var(--sm-text-2); font-size: 15rpx; }
.form-field input { height: 72rpx; padding: 0 14rpx; border: 1rpx solid var(--sm-surface-3); border-radius: 10rpx; background: var(--sm-surface-2); color: var(--sm-text); font-size: 17rpx; }
.form-field input::placeholder { color: var(--sm-text-3); }
.lyrics-textarea { min-height: 200rpx; padding: 12rpx 14rpx; border: 1rpx solid var(--sm-surface-3); border-radius: 10rpx; background: var(--sm-surface-2); color: var(--sm-text); font-size: 15rpx; line-height: 1.6; resize: vertical; }
.field-label-row { display: flex; align-items: center; justify-content: space-between; }
.lyrics-label { display: flex; align-items: center; gap: 8rpx; }
.lyrics-source { padding: 2rpx 10rpx; border-radius: 999rpx; background: var(--sm-surface-3); color: var(--sm-text-2); font-size: 12rpx; font-weight: 600; }
.lyrics-source.auto { background: var(--sm-accent-soft); color: var(--sm-accent-2); }
.upload-lyrics-btn { display: flex; align-items: center; gap: 5rpx; padding: 6rpx 14rpx; border-radius: 8rpx; background: var(--sm-accent-soft); color: var(--sm-accent); font-size: 13rpx; font-weight: 600; }
.upload-lyrics-btn:active { opacity: .7; }
.publish-check { margin-top: 16rpx; }
.check-ok, .check-missing { display: flex; align-items: center; gap: 8rpx; padding: 12rpx 16rpx; border-radius: 10rpx; font-size: 14rpx; }
.check-ok { background: var(--sm-accent-soft); color: var(--sm-accent); }
.check-missing { background: var(--sm-danger-soft); color: var(--sm-danger); }
.number-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12rpx; }

.action-row { display: flex; gap: 12rpx; margin-top: 24rpx; }
.linked-request { display: flex; align-items: center; justify-content: space-between; gap: 16rpx; margin-top: 16rpx; padding: 16rpx 20rpx; border: 1rpx solid var(--sm-accent-soft-border); border-radius: 14rpx; background: var(--sm-accent-soft); }
.linked-request-text { flex: 1; color: var(--sm-accent); font-size: 16rpx; }
.unlink-button { flex: none; padding: 8rpx 22rpx; border-radius: 20rpx; background: var(--sm-surface); color: var(--sm-danger); font-size: 15rpx; font-weight: 700; }
.save-button { display: flex; flex: 1; align-items: center; justify-content: center; gap: 8rpx; height: 78rpx; border-radius: 12rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 17rpx; font-weight: 800; }
.save-button.disabled { opacity: .55; }
.enrich-button { display: flex; flex: 1; align-items: center; justify-content: center; gap: 8rpx; height: 78rpx; border-radius: 12rpx; background: var(--sm-info-soft, rgba(90,140,255,.16)); color: var(--sm-accent-2); font-size: 17rpx; font-weight: 800; }
.enrich-button.disabled { opacity: .55; }
.publish-button { display: flex; flex: 1; align-items: center; justify-content: center; gap: 8rpx; height: 78rpx; border-radius: 12rpx; background: var(--sm-accent-2); color: var(--sm-accent-2-ink); font-size: 17rpx; font-weight: 800; }
.publish-button.disabled { opacity: .55; }
.reject-button { display: flex; align-items: center; justify-content: center; gap: 8rpx; width: 140rpx; height: 78rpx; border-radius: 12rpx; background: var(--sm-danger-soft); color: var(--sm-danger); font-size: 17rpx; font-weight: 700; }

.bottom-space { height: 50rpx; }
</style>
