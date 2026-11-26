<script setup lang="ts">
/**
 * 上传本地音频页：选择本地音频文件（可携带同名 .lrc 旁挂歌词）上传到“我的曲库”。
 *
 * 行为：
 * - 上传前按能力开关（allowedFormats / maxUploadSizeBytes）做格式与大小校验；
 * - 预检只展示可能匹配的曲目；公共库命中不会自动引用或阻止上传用户自己的原件；
 * - 可选申请公共审核；私人重复升级、单独保留和拒绝在“待确认”区决定。
 *
 * 说明：旁挂 .lrc 应与音频同名（文件主干一致），后端按主干配对；不匹配的旁挂歌词会被忽略。
 * 进入参数：无。访问控制：onShow 时未登录直接跳登录页。
 * 对应后端接口：services/user-library（uploadFiles）；能力开关经 services/capabilities。
 */
import { computed, ref } from 'vue'
import { onHide, onLoad, onShow, onUnload } from '@dcloudio/uni-app'
import { useI18n } from 'vue-i18n'
import { Music4, Upload } from 'lucide-vue-next'
import { currentUser, isAuthenticated } from '@/services/session'
import { useCapabilities } from '@/services/capabilities'
import { getUploadBatch, precheckUploads, uploadFiles } from '@/services/user-library'
import { getLibraryRecycleBin, getPrivateLibraryReviews, resolvePrivateLibraryReview, restoreLibraryTrack } from '@/services/library-workflow'
import { describeError } from '@/services/errors'
import { captureApiContext, subscribeApiContextChange } from '@/services/http'
import { pickLocalFiles } from '@/utils/app-file-picker'
import type { ApiPrecheckResponse, ApiUserUploadResponse } from '@/types/music'
import type { ApiLibraryWorkflowReview, ApiLibraryWorkflowTrack, ApiUserUploadBatch, ApiUserUploadItem } from '@/types/api.generated'

// 已选择的待上传文件（沿用 uni.chooseFile 的 tempFile 结构）
type PickedFile = { path: string; name: string; size: number }
// 能力开关单例
const caps = useCapabilities()

const { t } = useI18n()

// 已选文件列表 / 上传中 / 上传结果 / 错误信息
const files = ref<PickedFile[]>([])
const uploading = ref(false)
const result = ref<ApiUserUploadResponse | null>(null)
const errorMessage = ref('')
const publicReview = ref(false)
const prechecking = ref(false)
const precheckError = ref('')
const precheckResult = ref<ApiPrecheckResponse | null>(null)
const activeSection = ref<'upload' | 'reviews' | 'recycle'>('upload')
const privateReviews = ref<ApiLibraryWorkflowReview[]>([])
const reviewLoading = ref(false)
const reviewError = ref('')
const reviewPage = ref(1)
const reviewPageSize = 10
const reviewTotal = ref(0)
const recycleItems = ref<ApiLibraryWorkflowTrack[]>([])
const recycleLoading = ref(false)
const recycleError = ref('')
const recyclePage = ref(1)
const recyclePageSize = 10
const recycleTotal = ref(0)
const restoringTrackId = ref<string | null>(null)
const resolvingReviewId = ref<string | null>(null)
onLoad((options) => {
  if (options?.section === 'reviews' || options?.section === 'recycle') activeSection.value = options.section
})
const batch = ref<ApiUserUploadBatch | null>(null)
const statusError = ref('')
const checkingStatus = ref(false)
const uploadedFiles = ref<PickedFile[]>([])
let pageActive = false
let pollTimer: ReturnType<typeof setTimeout> | undefined
let pollGeneration = 0
let lastUserId: string | null = null
let workflowGeneration = 0
const stopApiContextSubscription = subscribeApiContextChange(() => {
  stopPolling()
  workflowGeneration++
  result.value = null
  batch.value = null
  batchId.value = null
  uploadedFiles.value = []
  files.value = []
  uploading.value = false
  prechecking.value = false
  resolvingReviewId.value = null
  restoringTrackId.value = null
  privateReviews.value = []
  recycleItems.value = []
  reviewTotal.value = 0
  recycleTotal.value = 0
  statusError.value = ''
  reviewError.value = ''
  recycleError.value = ''
  reviewLoading.value = false
  recycleLoading.value = false
})
const pendingStatuses = ['uploaded', 'validating', 'parsing_metadata', 'transcoding']
const terminalStatuses = ['pending_publication', 'published', 'processing_failed', 'rejected', 'identity_rejected']
const failedItems = computed(() => batch.value?.items.filter((item) => item.status === 'processing_failed') ?? [])
const batchId = ref<string | null>(null)
const canRetryFiles = computed(() => failedItems.value.some((item) => uploadedFiles.value.some((file) => file.name === item.displayName)))
const processingSlow = computed(() => batch.value?.items.some((item) => pendingStatuses.includes(item.status)
  && Date.now() - Date.parse(item.updatedAt) > 60000) ?? false)

function storageKey() {
  return currentUser.value ? `sm-last-upload:${scopeStorageId()}` : null
}

/** Isolate pending batch IDs by origin and authenticated user without storing either in the key. */
function scopeStorageId() {
  const scope = captureApiContext().scope
  let hash = 2166136261
  for (let index = 0; index < scope.length; index++) {
    hash ^= scope.charCodeAt(index)
    hash = Math.imul(hash, 16777619)
  }
  return (hash >>> 0).toString(36)
}

function stopPolling() {
  pollGeneration++
  if (pollTimer !== undefined) clearTimeout(pollTimer)
  pollTimer = undefined
  checkingStatus.value = false
}

/** 单次查询完成后再安排下一次；页面离开后忽略尚未返回的请求。 */
async function refreshStatus() {
  stopPolling()
  const id = batchId.value
  if (!id || !pageActive) return
  const generation = pollGeneration
  checkingStatus.value = true
  try {
    const next = await getUploadBatch(id)
    if (!pageActive || generation !== pollGeneration) return
    batch.value = next
    statusError.value = ''
    if (next.items.some((item) => !terminalStatuses.includes(item.status))) {
      pollTimer = setTimeout(() => { void refreshStatus() }, 3000)
    }
  } catch (error) {
    if (!pageActive || generation !== pollGeneration) return
    statusError.value = t('libraryPage.statusLoadFailed', { message: describeError(error) })
  } finally {
    if (generation === pollGeneration) checkingStatus.value = false
  }
}

function statusLabel(status: string) {
  const keys: Record<string, string> = {
    uploaded: 'statusQueued', validating: 'statusValidating', parsing_metadata: 'statusMetadata',
    transcoding: 'statusTranscoding', pending_publication: 'statusReady', published: 'statusReady',
    processing_failed: 'statusFailed', rejected: 'statusRejected', identity_rejected: 'statusRejected',
  }
  return keys[status] ? t(`libraryPage.${keys[status]}`) : status
}

function itemStatusLabel(item: ApiUserUploadItem) {
  if (item.status === 'published' && !item.trackId) return t('libraryPage.statusPrivateReview')
  return statusLabel(item.status)
}

function failureMessage(item: ApiUserUploadItem) {
  const keys: Record<string, string> = {
    media_corrupt: 'mediaCorrupt', media_parse_failed: 'mediaParseFailed', transcode_failed: 'transcodeFailed',
    unsupported_media_format: 'unsupportedAudio', resource_not_found: 'uploadFileMissing',
    upload_too_large: 'uploadTooLarge', duplicate_private_upload: 'privateUploadConflict',
    media_processor_unavailable: 'processorUnavailable', media_processing_timeout: 'processingTimeout',
  }
  return item.errorCode && keys[item.errorCode] ? t(`libraryPage.${keys[item.errorCode]}`)
    : item.errorMessage || t('libraryPage.processingFailedRetry')
}

function prepareRetry() {
  const names = new Set(failedItems.value.map((item) => item.displayName))
  const stems = new Set([...names].map((name) => name.replace(/\.[^.]+$/, '').toLowerCase()))
  files.value = uploadedFiles.value.filter((file) => names.has(file.name)
    || (!isAudioFile(file.name) && stems.has(file.name.replace(/\.[^.]+$/, '').toLowerCase())))
}

// 登录拦截
onShow(() => {
  pageActive = true
  if (!isAuthenticated.value) {
    uni.redirectTo({ url: '/pages/auth/login' })
    return
  }
  const userId = currentUser.value?.id ?? null
  if (lastUserId !== userId) {
    stopPolling()
    result.value = null
    batch.value = null
    batchId.value = null
    uploadedFiles.value = []
    statusError.value = ''
    errorMessage.value = ''
    files.value = []
    publicReview.value = false
    precheckResult.value = null
    precheckError.value = ''
    privateReviews.value = []
    recycleItems.value = []
    reviewTotal.value = 0
    recycleTotal.value = 0
    reviewPage.value = 1
    recyclePage.value = 1
    lastUserId = userId
  }
  if (!batchId.value) {
    try {
      const key = storageKey()
      const saved = key ? uni.getStorageSync(key) : null
      if (typeof saved === 'string' && saved) batchId.value = saved
    } catch { /* 本地存储不可用不影响本次上传。 */ }
  }
  if (batchId.value) void refreshStatus()
  if (activeSection.value === 'reviews') void loadPrivateReviews()
  if (activeSection.value === 'recycle') void loadRecycleBin()
})
onHide(() => { pageActive = false; workflowGeneration++; reviewLoading.value = false; recycleLoading.value = false; stopPolling() })
onUnload(() => { pageActive = false; workflowGeneration++; reviewLoading.value = false; recycleLoading.value = false; stopPolling(); stopApiContextSubscription() })

// 是否允许上传（开关开启且已选择音频文件）
const canUpload = computed(() => caps.capabilities.userUploads && hasAudio.value && !uploading.value && !prechecking.value)

// 是否已选择至少一个音频文件（排除纯旁挂歌词）
const hasAudio = computed(() => files.value.some((file) => isAudioFile(file.name)))

async function checkSelectedMatches() {
  if (!hasAudio.value || prechecking.value) return
  prechecking.value = true
  const generation = workflowGeneration
  precheckError.value = ''
  precheckResult.value = null
  try {
    const next = await precheckUploads(files.value.filter((file) => isAudioFile(file.name)).map((file) => ({
      fileName: file.name,
      sizeBytes: file.size,
      title: file.name.replace(/\.[^.]+$/, ''),
      artist: null,
      durationMs: null,
    })))
    if (generation === workflowGeneration) precheckResult.value = next
  } catch (error) {
    if (generation === workflowGeneration) precheckError.value = describeError(error)
  } finally {
    if (generation === workflowGeneration) prechecking.value = false
  }
}

function matchFor(fileName: string) {
  return precheckResult.value?.results.find((item) => item.fileName === fileName)?.match ?? null
}

async function loadPrivateReviews() {
  if (reviewLoading.value) return
  const generation = workflowGeneration
  reviewLoading.value = true
  reviewError.value = ''
  try {
    let page = reviewPage.value
    let result = await getPrivateLibraryReviews('pending', page, reviewPageSize)
    if (!pageActive || generation !== workflowGeneration) return
    const lastPage = Math.max(1, Math.ceil(result.total / reviewPageSize))
    if (page > lastPage) {
      page = lastPage
      reviewPage.value = page
      result = await getPrivateLibraryReviews('pending', page, reviewPageSize)
      if (!pageActive || generation !== workflowGeneration) return
    }
    privateReviews.value = [...result.items]
    reviewTotal.value = result.total
  } catch (error) {
    if (!pageActive || generation !== workflowGeneration) return
    reviewError.value = describeError(error)
  } finally {
    if (generation === workflowGeneration) reviewLoading.value = false
  }
}

async function loadRecycleBin() {
  if (recycleLoading.value) return
  const generation = workflowGeneration
  recycleLoading.value = true
  recycleError.value = ''
  try {
    let page = recyclePage.value
    let result = await getLibraryRecycleBin(page, recyclePageSize)
    if (!pageActive || generation !== workflowGeneration) return
    const lastPage = Math.max(1, Math.ceil(result.total / recyclePageSize))
    if (page > lastPage) {
      page = lastPage
      recyclePage.value = page
      result = await getLibraryRecycleBin(page, recyclePageSize)
      if (!pageActive || generation !== workflowGeneration) return
    }
    recycleItems.value = [...result.items]
    recycleTotal.value = result.total
  } catch (error) {
    if (!pageActive || generation !== workflowGeneration) return
    recycleError.value = describeError(error)
  } finally {
    if (generation === workflowGeneration) recycleLoading.value = false
  }
}

function setSection(section: 'upload' | 'reviews' | 'recycle') {
  activeSection.value = section
  if (section === 'reviews') void loadPrivateReviews()
  if (section === 'recycle') void loadRecycleBin()
}

function setPublicReviewFromSwitch(event: Event) {
  publicReview.value = (event as unknown as { detail?: { value?: boolean } }).detail?.value === true
}

function decideReview(review: ApiLibraryWorkflowReview, decision: 'replace' | 'separate' | 'reject') {
  if (resolvingReviewId.value) return
  if (decision === 'separate' && review.reviewReason !== 'identity_uncertain') return
  if (decision === 'replace' && review.reviewReason !== 'quality_uncertain') return
  const title = review.title || review.originalContainer || t('libraryPage.unnamedUpload')
  const generation = workflowGeneration
  const copy = decision === 'replace' ? t('libraryPage.confirmReplace', { title })
    : decision === 'separate' ? t('libraryPage.confirmKeepSeparate', { title })
      : t('libraryPage.confirmReject', { title })
  uni.showModal({
    title: t('libraryPage.reviewDecision'),
    content: copy,
    confirmText: decision === 'replace' ? t('libraryPage.replace') : decision === 'separate' ? t('libraryPage.keepSeparate') : t('libraryPage.rejectReview'),
    success: async ({ confirm }) => {
      if (!confirm) return
      if (generation !== workflowGeneration) return
      resolvingReviewId.value = review.id
      try {
        await resolvePrivateLibraryReview(review.id, decision, decision === 'reject' ? t('libraryPage.userRejectedReason') : undefined)
        uni.showToast({ title: t('libraryPage.reviewUpdated'), icon: 'none' })
        await loadPrivateReviews()
        if (batchId.value) void refreshStatus()
      } catch (error) {
        uni.showToast({ title: describeError(error), icon: 'none' })
      } finally {
        resolvingReviewId.value = null
      }
    },
  })
}

function workflowDate(value?: string | null) {
  return value ? new Date(value).toLocaleString() : t('libraryPage.dateUnknown')
}

function changeWorkflowPage(kind: 'reviews' | 'recycle', delta: number) {
  if (kind === 'reviews') {
    reviewPage.value += delta
    void loadPrivateReviews()
  } else {
    recyclePage.value += delta
    void loadRecycleBin()
  }
}

function restoreTrack(track: ApiLibraryWorkflowTrack) {
  const generation = workflowGeneration
  uni.showModal({
    title: t('libraryPage.restoreTrack'),
    content: t('libraryPage.confirmRestore', { title: track.title }),
    success: async ({ confirm }) => {
      if (!confirm || generation !== workflowGeneration || restoringTrackId.value) return
      restoringTrackId.value = track.trackId
      try {
        await restoreLibraryTrack(track.trackId)
        uni.showToast({ title: t('libraryPage.trackRestored'), icon: 'none' })
        await loadRecycleBin()
      } catch (error) {
        uni.showToast({ title: describeError(error), icon: 'none' })
      } finally {
        restoringTrackId.value = null
      }
    },
  })
}

/**
 * 判断文件名是否为音频（非 .lrc/.txt 旁挂歌词）。
 * @param name 文件名
 * @returns 音频为 true
 */
function isAudioFile(name: string): boolean {
  const ext = name.split('.').pop()?.toLowerCase() ?? ''
  return ext !== 'lrc' && ext !== 'txt'
}

/**
 * 校验单文件：格式在允许列表内、大小未超限。
 * @param file 待校验文件
 * @returns 合法为 null，否则返回错误描述
 */
function validateFile(file: PickedFile): string | null {
  const ext = file.name.split('.').pop()?.toLowerCase() ?? ''
  const allowed = caps.capabilities.allowedFormats
  if (isAudioFile(file.name) && allowed.length && !allowed.includes(ext)) {
    return t('libraryPage.unsupportedFormat', { ext: `.${ext}`, allowed: allowed.join(' / ') })
  }
  const max = caps.capabilities.maxUploadSizeBytes
  if (max && file.size > max) {
    return t('libraryPage.fileTooLarge', {
      size: (file.size / 1024 / 1024).toFixed(1),
      max: (max / 1024 / 1024).toFixed(0),
    })
  }
  return null
}

/**
 * 把选择结果按格式/大小校验后并入待上传列表。
 * @param list 待加入的文件
 */
function appendPicked(list: PickedFile[]) {
  const valid: PickedFile[] = []
  for (const file of list) {
    const err = validateFile(file)
    if (err) {
      uni.showToast({ title: err, icon: 'none' })
      continue
    }
    valid.push(file)
  }
  if (valid.length) {
    files.value = [...files.value, ...valid]
    precheckResult.value = null
    precheckError.value = ''
  }
}

/**
 * 选择本地文件：唤起系统选择器（最多 9 个），逐文件校验后加入待上传列表。
 * 平台差异：H5 用 uni.chooseFile（官方仅支持 H5）；App 用原生 SAF Intent
 * （uni.chooseFile 在 App 端不会弹任何界面），复制到缓存目录后返回真实路径。
 * 关键副作用：非法文件弹 toast 不加入；合法文件加入 files。
 */
async function handleChoose() {
  if (uploading.value) return
  // #ifdef APP-PLUS
  try {
    appendPicked(await pickLocalFiles())
  } catch {
    uni.showToast({ title: t('libraryPage.pickFileFailed'), icon: 'none' })
  }
  // #endif
  // #ifndef APP-PLUS
  uni.chooseFile({
    count: 9,
    type: 'all',
    success: ({ tempFiles }) => {
      // uni 类型为 File | File[] | ChooseFileSuccessCallbackResultFile | ...，统一归一为数组
      const list = Array.isArray(tempFiles)
        ? tempFiles
        : tempFiles
          ? [tempFiles]
          : []
      appendPicked(
        list.map((file) => ({
          path: (file as { path?: string }).path ?? '',
          name: (file as { name?: string }).name ?? '',
          size: (file as { size?: number }).size ?? 0,
        })),
      )
    },
  })
  // #endif
}

/**
 * 从待上传列表移除单个文件。
 * @param index 文件下标
 */
function handleRemoveFile(index: number) {
  if (uploading.value) return
  files.value = files.value.filter((_, i) => i !== index)
  precheckResult.value = null
  precheckError.value = ''
}

/**
 * 执行上传：上传全部已选文件，成功后写入 result 并清空待上传列表。
 * 关键副作用：失败时写入 errorMessage；成功后清空 files 供再次上传。
 */
async function handleUpload() {
  if (!canUpload.value) return
  const generation = workflowGeneration
  uploading.value = true
  errorMessage.value = ''
  statusError.value = ''
  stopPolling()
  result.value = null
  batch.value = null
  batchId.value = null
  try {
    const selected = [...files.value]
    const response = await uploadFiles(selected, false, publicReview.value)
    if (generation !== workflowGeneration) return
    result.value = response
    uploadedFiles.value = selected
    batchId.value = response.batchId ?? null
    try {
      const key = storageKey()
      if (key) {
        if (batchId.value) uni.setStorageSync(key, batchId.value)
        else uni.removeStorageSync(key)
      }
    } catch { /* 上传与后台处理不依赖本地存储。 */ }
    files.value = []
    precheckResult.value = null
    publicReview.value = false
    if (batchId.value) void refreshStatus()
  } catch (error) {
    if (generation === workflowGeneration) {
      errorMessage.value = describeError(error, error instanceof Error ? error.message : t('uploadPage.uploadFailedRetry'))
    }
  } finally {
    if (generation === workflowGeneration) uploading.value = false
  }
}

/**
 * 完成后返回首页“曲库”tab 查看。
 * 上传页仅从首页曲库 tab 进入，返回即回到该 tab 且 onShow 自动刷新列表；
 * 若页面栈无上级（如直接进入），兜底重启到首页。
 */
function goBackToLibrary() {
  uni.navigateBack({
    fail: () => uni.reLaunch({ url: '/pages/home/index' }),
  })
}
</script>

<template>
  <view class="up-page">
    <view class="workflow-tabs">
      <button :class="{ selected: activeSection === 'upload' }" @tap="setSection('upload')">{{ $t('libraryPage.uploadTab') }}</button>
      <button :class="{ selected: activeSection === 'reviews' }" @tap="setSection('reviews')">{{ $t('libraryPage.reviewTab') }}</button>
      <button :class="{ selected: activeSection === 'recycle' }" @tap="setSection('recycle')">{{ $t('libraryPage.recycleTab') }}</button>
    </view>
    <view v-if="activeSection === 'reviews'" class="workflow-section">
      <view class="workflow-heading"><text class="result-title">{{ $t('libraryPage.reviewTab') }}</text><button :disabled="reviewLoading" @tap="loadPrivateReviews">{{ $t('app.retry') }}</button></view>
      <text v-if="reviewLoading" class="state-text">{{ $t('app.loading') }}</text>
      <view v-else-if="reviewError" class="up-error" role="alert">{{ reviewError }}</view>
      <text v-else-if="!privateReviews.length" class="state-text">{{ $t('libraryPage.reviewsEmpty') }}</text>
      <view v-for="review in privateReviews" :key="review.id" class="workflow-row">
        <text class="result-title">{{ review.title || $t('libraryPage.unnamedUpload') }}</text>
        <text class="result-line">{{ review.artists.join(' / ') }}</text>
        <text class="review-reason">{{ $t(review.reviewReason === 'identity_uncertain' ? 'libraryPage.identityUncertain' : 'libraryPage.qualityUncertain') }}</text>
        <view class="quality-comparison">
          <view><text class="quality-label">{{ $t('libraryPage.uploadedOriginal') }}</text><text class="result-line">{{ review.originalCodec || '—' }} · {{ review.originalBitrateKbps || '—' }} kbps</text><text class="result-line">{{ review.sampleRateHz || '—' }} Hz · {{ review.channels || '—' }} ch · {{ review.bitsPerSample || '—' }} bit</text></view>
          <view v-if="review.existingTrackId"><text class="quality-label">{{ $t('libraryPage.existingPrivate') }}</text><text class="result-line">{{ review.existingTrackTitle }} · {{ review.existingTrackArtists?.join(' / ') }}</text><text class="result-line">{{ review.existingCodec || '—' }} · {{ review.existingBitrateKbps || '—' }} kbps</text></view>
        </view>
        <text class="result-line">{{ $t('libraryPage.reviewExpires', { date: workflowDate(review.expiresAtUtc) }) }}</text>
        <view class="workflow-actions">
          <button v-if="review.reviewReason === 'quality_uncertain'" class="action-primary" :disabled="!!resolvingReviewId" @tap="decideReview(review, 'replace')">{{ $t('libraryPage.replace') }}</button>
          <button v-if="review.reviewReason === 'identity_uncertain'" :disabled="!!resolvingReviewId" @tap="decideReview(review, 'separate')">{{ $t('libraryPage.keepSeparate') }}</button>
          <button class="action-danger" :disabled="!!resolvingReviewId" @tap="decideReview(review, 'reject')">{{ $t('libraryPage.rejectReview') }}</button>
        </view>
      </view>
      <view v-if="reviewTotal > reviewPageSize" class="workflow-pagination">
        <button :disabled="reviewPage <= 1 || reviewLoading" @tap="changeWorkflowPage('reviews', -1)">‹</button><text>{{ reviewPage }} / {{ Math.ceil(reviewTotal / reviewPageSize) }}</text><button :disabled="reviewPage * reviewPageSize >= reviewTotal || reviewLoading" @tap="changeWorkflowPage('reviews', 1)">›</button>
      </view>
    </view>
    <view v-else-if="activeSection === 'recycle'" class="workflow-section">
      <view class="workflow-heading"><text class="result-title">{{ $t('libraryPage.recycleTab') }}</text><button :disabled="recycleLoading" @tap="loadRecycleBin">{{ $t('app.retry') }}</button></view>
      <text v-if="recycleLoading" class="state-text">{{ $t('app.loading') }}</text>
      <view v-else-if="recycleError" class="up-error" role="alert">{{ recycleError }}</view>
      <text v-else-if="!recycleItems.length" class="state-text">{{ $t('libraryPage.recycleEmpty') }}</text>
      <view v-for="track in recycleItems" :key="track.trackId" class="workflow-row recycle-row">
        <view class="workflow-track-copy"><text class="result-title">{{ track.title }}</text><text class="result-line">{{ track.artists.join(' / ') }}</text><text class="result-line">{{ $t('libraryPage.recycleExpires', { date: workflowDate(track.recycleExpiresAtUtc) }) }}</text></view>
        <button :disabled="!!restoringTrackId" @tap="restoreTrack(track)">{{ $t('libraryPage.restoreTrack') }}</button>
      </view>
      <view v-if="recycleTotal > recyclePageSize" class="workflow-pagination">
        <button :disabled="recyclePage <= 1 || recycleLoading" @tap="changeWorkflowPage('recycle', -1)">‹</button><text>{{ recyclePage }} / {{ Math.ceil(recycleTotal / recyclePageSize) }}</text><button :disabled="recyclePage * recyclePageSize >= recycleTotal || recycleLoading" @tap="changeWorkflowPage('recycle', 1)">›</button>
      </view>
    </view>
    <!-- 开关关闭提示 -->
    <view v-else-if="!caps.capabilities.userUploads" class="state-text">
      {{ $t('libraryPage.uploadDisabled') }}
    </view>

    <template v-else>
      <!-- 说明卡片 -->
      <view class="up-tip">
        <view class="tip-icon"><Music4 :size="20" /></view>
        <view class="tip-copy">
          <text class="tip-title">{{ $t('library.upload') }}</text>
          <text class="tip-sub">{{ $t('libraryPage.privacyNote') }}</text>
        </view>
      </view>

      <view class="public-review-option"><view class="tip-copy"><text class="tip-title">{{ $t('libraryPage.publicReviewOption') }}</text><text class="tip-sub">{{ $t('libraryPage.publicReviewScope') }}</text></view><switch :checked="publicReview" :disabled="uploading" @change="setPublicReviewFromSwitch" /></view>

      <!-- 已选文件列表 -->
      <view v-if="files.length" class="up-files">
        <view v-for="(file, index) in files" :key="file.path" class="file-row">
          <Music4 :size="18" class="file-icon" />
          <view class="file-copy">
            <text class="file-name">{{ file.name }}</text>
            <text class="file-size">{{ (file.size / 1024 / 1024).toFixed(2) }} MB</text>
            <text v-if="matchFor(file.name)" class="file-match">{{ $t(matchFor(file.name)?.isPublic ? 'libraryPage.publicMatch' : 'libraryPage.privateMatch', { title: matchFor(file.name)?.title }) }}</text>
          </view>
          <text class="file-remove" @tap.stop="handleRemoveFile(index)">{{ $t('uploadPage.removeFile') }}</text>
        </view>
      </view>

      <!-- 选择 / 上传操作 -->
      <button v-if="hasAudio" class="precheck-button" :disabled="prechecking || uploading" @tap="checkSelectedMatches">{{ prechecking ? $t('libraryPage.checkingStatus') : $t('libraryPage.checkMatches') }}</button>
      <view v-if="precheckError" class="up-error" role="alert">{{ precheckError }}</view>
      <view class="up-actions">
        <view class="btn btn-ghost" @tap="handleChoose"><Upload :size="17" /><text>{{ $t('libraryPage.chooseFiles') }}</text></view>
        <view class="btn btn-primary" :class="{ 'is-disabled': !canUpload }" @tap="handleUpload">
          {{ uploading ? $t('libraryPage.uploading') : precheckResult ? $t('libraryPage.uploadMyOriginal') : $t('libraryPage.startUpload') }}
        </view>
      </view>

      <!-- 错误 -->
      <view v-if="errorMessage" class="up-error">{{ errorMessage }}</view>

      <!-- 上传结果 -->
      <view v-if="result || batchId" class="up-result">
        <text v-if="batchId" class="result-title">{{ $t('libraryPage.uploadReceived') }}</text>
        <view v-if="result?.referenced.length" class="result-group">
          <text class="result-title">{{ $t('libraryPage.resultReferenced', { count: result.referenced.length }) }}</text>
          <text v-for="t in result.referenced" :key="t.trackId" class="result-line">· {{ t.title }}</text>
        </view>
        <view v-if="result?.queued.length && !batch" class="result-group">
          <text class="result-title">{{ $t('libraryPage.resultQueued', { count: result.queued.length }) }}</text>
          <text v-for="q in result.queued" :key="q.itemId" class="result-line">· {{ q.displayName }} —— {{ $t('libraryPage.queuedHint') }}</text>
        </view>
        <view v-if="batch" class="result-group">
          <view v-for="item in batch.items" :key="item.itemId" class="processing-item">
            <text class="result-line">{{ item.displayName }}</text>
            <text class="processing-status" :class="{ 'has-error': item.status === 'processing_failed' }">{{ itemStatusLabel(item) }}</text>
            <view v-if="item.status === 'published' && !item.trackId" class="private-review-hint"><text>{{ $t('libraryPage.privateReviewPendingHint') }}</text><button @tap="setSection('reviews')">{{ $t('libraryPage.reviewTab') }}</button></view>
            <text v-if="item.status === 'processing_failed'" class="processing-error">{{ failureMessage(item) }}</text>
            <text v-if="item.status === 'processing_failed' && item.errorCode" class="error-code">{{ $t('libraryPage.errorCode', { code: item.errorCode }) }}</text>
          </view>
          <text v-if="processingSlow" class="result-line">{{ $t('libraryPage.processingSlow') }}</text>
        </view>
        <view v-if="statusError" class="up-error" role="alert">{{ statusError }}</view>
        <view v-if="batchId" class="up-actions">
          <view class="btn btn-ghost" :class="{ 'is-disabled': checkingStatus }" @tap="!checkingStatus && refreshStatus()">
            {{ checkingStatus ? $t('libraryPage.checkingStatus') : $t('libraryPage.refreshStatus') }}
          </view>
          <view v-if="canRetryFiles" class="btn btn-ghost" @tap="prepareRetry">{{ $t('libraryPage.prepareRetry') }}</view>
        </view>
        <view v-if="result?.rejected.length" class="result-group">
          <text class="result-title">{{ $t('libraryPage.resultRejected', { count: result.rejected.length }) }}</text>
          <text v-for="r in result.rejected" :key="r.displayName" class="result-line">· {{ r.displayName }} —— {{ r.reason === 'private_track_of_another_user' ? $t('libraryPage.privateUploadConflict') : r.reason }}</text>
        </view>
        <view class="btn btn-primary" @tap="goBackToLibrary"><text>{{ $t('libraryPage.goToLibrary') }}</text></view>
      </view>
    </template>
  </view>
</template>

<style lang="scss" scoped>
/* 上传本地音频（方向 B · 玻璃质感） */
.up-page { min-height: 100vh; padding: 24rpx 40rpx 60rpx; background: var(--sm-page-bg); box-sizing: border-box; }
.workflow-tabs { display: flex; gap: 12rpx; padding: 12rpx 0 24rpx; }
.workflow-tabs button, .workflow-heading button, .workflow-actions button, .workflow-pagination button, .recycle-row > button, .precheck-button { margin: 0; padding: 12rpx 22rpx; min-height: 44px; border: 1rpx solid var(--sm-border); border-radius: var(--sm-radius-field); color: var(--sm-text); background: var(--sm-surface); font-size: 24rpx; line-height: 1.5; }
.workflow-tabs button { flex: 1; }
.workflow-tabs .selected, .workflow-actions .action-primary { color: var(--sm-accent); background: var(--sm-accent-soft); border-color: var(--sm-accent-soft-border); }
.workflow-section { padding-top: 12rpx; }
.workflow-heading { display: flex; justify-content: space-between; align-items: center; gap: 16rpx; }
.workflow-row { margin-top: 24rpx; padding: 28rpx; border: 1rpx solid var(--sm-border); border-radius: var(--sm-radius-card); background: var(--sm-surface); }
.quality-comparison { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 22rpx; margin: 18rpx 0; padding: 20rpx; border-radius: var(--sm-radius-field); background: var(--sm-surface-2); }
.quality-label { color: var(--sm-text); font-weight: 600; font-size: 24rpx; }
.workflow-actions, .workflow-pagination { display: flex; align-items: center; justify-content: center; gap: 12rpx; margin-top: 22rpx; flex-wrap: wrap; }
.workflow-actions button { flex: 1; }
.workflow-actions .action-danger { color: var(--sm-danger); }
.workflow-pagination { color: var(--sm-text-2); font-size: 24rpx; }
.recycle-row { display: flex; align-items: center; gap: 20rpx; }
.workflow-track-copy { flex: 1; min-width: 0; }
.public-review-option { display: flex; align-items: center; gap: 20rpx; margin-top: 24rpx; padding: 28rpx; border: 1rpx solid var(--sm-border); border-radius: var(--sm-radius-card); background: var(--sm-surface); }
.precheck-button { width: 100%; margin-top: 24rpx; }
.file-match { color: var(--sm-accent); font-size: 23rpx; line-height: 1.5; white-space: normal; }
.review-reason { display: block; margin-top: 12rpx; color: var(--sm-warning); font-size: 23rpx; line-height: 1.5; }
.private-review-hint { display: flex; align-items: center; justify-content: space-between; gap: 12rpx; margin-top: 12rpx; color: var(--sm-text-2); font-size: 22rpx; }
.private-review-hint button { padding: 8rpx 16rpx; border: 1px solid var(--sm-border); border-radius: var(--sm-radius-field); background: var(--sm-surface-2); color: var(--sm-accent); flex: none; }
.state-text { padding: 80rpx 20rpx; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }
.up-tip { display: flex; align-items: center; gap: 18rpx; margin-top: 8rpx; padding: 26rpx; border: 1rpx solid var(--sm-border); border-radius: 20rpx; background: var(--sm-surface); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); }
.tip-icon { display: grid; flex: none; place-items: center; width: 70rpx; height: 70rpx; border-radius: 18rpx; background: var(--sm-accent-soft); color: var(--sm-accent); }
.tip-copy { display: flex; flex: 1; flex-direction: column; gap: 7rpx; }
.tip-title { color: var(--sm-text); font-size: 21rpx; font-weight: 700; }
.tip-sub { color: var(--sm-text-2); font-size: 16rpx; line-height: 1.5; }
.up-files { margin-top: 28rpx; }
.file-row { display: flex; align-items: center; gap: 14rpx; padding: 20rpx 0; border-bottom: 1rpx solid var(--sm-divider); }
.file-icon { flex: none; color: var(--sm-text-2); }
.file-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 5rpx; }
.file-name { overflow: hidden; color: var(--sm-text); font-size: 19rpx; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.file-size { color: var(--sm-text-3); font-size: 15rpx; }
.file-remove { flex: none; color: var(--sm-danger); font-size: 16rpx; font-weight: 600; }
.up-actions { display: flex; gap: 20rpx; margin-top: 36rpx; }
.btn { display: flex; flex: 1; align-items: center; justify-content: center; gap: 12rpx; height: 92rpx; border-radius: 46rpx; font-size: 20rpx; font-weight: 700; }
.btn-ghost { border: 1rpx solid var(--sm-border); background: var(--sm-surface); color: var(--sm-text); }
.btn-primary { border: 1rpx solid var(--sm-accent-soft-border); background: var(--sm-accent-soft); color: var(--sm-accent); }
.btn-primary.is-disabled { opacity: .5; }
.up-error { margin-top: 24rpx; padding: 18rpx 20rpx; border: 1rpx solid var(--sm-danger-soft-border); border-radius: 16rpx; background: var(--sm-danger-soft); color: var(--sm-danger); font-size: 16rpx; }
.up-result { margin-top: 32rpx; }
.result-group { margin-bottom: 20rpx; }
.result-title { display: block; margin-bottom: 10rpx; color: var(--sm-text); font-size: 19rpx; font-weight: 700; }
.result-line { display: block; padding: 6rpx 0; color: var(--sm-text-2); font-size: 17rpx; }
.processing-item { margin-top: 18rpx; padding: 20rpx; border: 1px solid var(--sm-border); border-radius: var(--sm-radius-field); }
.processing-status { display: block; margin-top: 8rpx; color: var(--sm-accent); font-size: 24rpx; }
.processing-status.has-error, .processing-error { color: var(--sm-danger); }
.processing-error, .error-code { display: block; margin-top: 10rpx; font-size: 24rpx; line-height: 1.6; overflow-wrap: anywhere; }
.error-code { color: var(--sm-text-3); font-size: 22rpx; }
.up-result > .btn { margin-top: 24rpx; }
.up-tip { padding: 40rpx; border-radius: var(--sm-radius-card); }
.tip-title { font-size: 32rpx; }
.tip-sub, .file-name, .result-line { font-size: 24rpx; overflow-wrap: anywhere; }
.tip-copy { min-width: 0; }
.file-row { padding: 24rpx; margin-top: 16rpx; background: var(--sm-surface); border: 1px solid var(--sm-border); border-radius: var(--sm-radius-field); }
.btn { min-height: 48px; border-radius: var(--sm-radius-field); }
@media (min-width: 960px) { .up-page { max-width: 800px; margin: auto; } }
</style>
