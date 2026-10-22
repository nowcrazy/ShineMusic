/**
 * 管理端曲库导入与审核服务：上传音频、查询导入批次/条目、试听、修正元数据、发布入库、拒绝。
 *
 * 对齐后端 /api/v1/admin/library 系列端点：
 * - POST   /imports                       （multipart 上传一批音频文件）
 * - GET    /imports                       （批次列表，支持 status 过滤与分页）
 * - GET    /imports/{id}                  （批次详情，含全部条目）
 * - GET    /import-items/{id}             （单条目详情）
 * - GET    /import-items/{id}/preview     （试听转码后音频流）
 * - PATCH  /import-items/{id}             （修正元数据：标题/艺人/专辑/曲序/碟序/年份/流派）
 * - POST   /import-items/{id}/publish     （审核通过，正式入库）
 * - DELETE /import-items/{id}             （拒绝，删除隔离区文件）
 */
import type { ApiPageResult } from '@/types/music'
import { t } from '@/locale'
import { getAccessToken } from './tokens'
import { API_PREFIX, http, resolveUrl } from './http'

/** 导入源文件信息 */
export interface ImportSourceFile {
  displayName: string
  sizeBytes: number
  container: string | null
  codec: string | null
}

/** 解析出的音频元数据 */
export interface ImportMetadata {
  title: string | null
  artists: string[]
  albumTitle: string | null
  trackNumber: number | null
  discNumber: number | null
  releaseYear: number | null
  genres: string[]
  durationMs: number | null
  container: string | null
  codec: string | null
  bitrateKbps: number | null
  artworkAvailable: boolean
  lyrics: string | null
  /** 歌词来源：embedded / sidecar / lrclib / manual；无歌词时为 null */
  lyricsSource: string | null
  /** 歌词匹配置信度 0~1；外部源按相似度打分，其余为 1 */
  lyricsConfidence: number | null
}

/** 转码状态 */
export interface ImportTranscode {
  status: string | null
  outputFormat: string | null
  bitrateKbps: number | null
  errorCode: string | null
}

/** 与目标点歌需求的匹配结果（后端读取时实时计算，元数据改动后立即刷新） */
export interface SongRequestMatch {
  /** 结论：match=一致 / fuzzy=细微差异 / mismatch=不一致 */
  kind: 'match' | 'fuzzy' | 'mismatch'
  /** 待上架曲目的展示串（标题 - 艺人） */
  trackDisplay: string
  /** 目标需求的展示串（标题 - 艺人） */
  requestDisplay: string
  /** 是否会在发布时被强制阻断（由全局配置决定，true 表示管理员也无法覆盖） */
  blocking: boolean
  /** 给管理员看的中文提示 */
  message: string
}

/** 单个导入条目 */
export interface LibraryImportItem {
  id: string
  status: string
  sourceFile: ImportSourceFile
  metadata: ImportMetadata
  transcode: ImportTranscode
  warnings: string[]
  createdAt: string
  updatedAt: string
  /** 关联的点歌需求 id；null 表示未关联 */
  songRequestId: string | null
  /** 与关联需求的匹配结果；未关联或元数据未解析时为 null */
  songRequestMatch: SongRequestMatch | null
}

/** 导入批次 */
export interface LibraryImportBatch {
  id: string
  status: string
  items: LibraryImportItem[]
  createdAt: string
}

/** 审核流程的状态常量（与后端 LibraryImportItemStatuses 对齐） */
export const ImportItemStatus = {
  Uploaded: 'uploaded',
  Validating: 'validating',
  ParsingMetadata: 'parsing_metadata',
  Transcoding: 'transcoding',
  PendingReview: 'pending_review',
  ReadyToPublish: 'ready_to_publish',
  PendingPublication: 'pending_publication',
  Published: 'published',
  Rejected: 'rejected',
  ProcessingFailed: 'processing_failed',
} as const

/** 状态标签（值为 importStatus.* i18n key，界面用 t() 实时本地化） */
export const ImportItemStatusLabel: Record<string, string> = {
  uploaded: 'importStatus.uploaded',
  validating: 'importStatus.validating',
  parsing_metadata: 'importStatus.parsingMetadata',
  transcoding: 'importStatus.transcoding',
  pending_review: 'importStatus.pendingReview',
  ready_to_publish: 'importStatus.readyToPublish',
  pending_publication: 'importStatus.pendingPublication',
  published: 'importStatus.published',
  rejected: 'importStatus.rejected',
  processing_failed: 'importStatus.processingFailed',
}

/** 批次状态标签（值为 importStatus.* i18n key，界面用 t() 实时本地化） */
export const ImportBatchStatusLabel: Record<string, string> = {
  queued: 'importStatus.queued',
  processing: 'importStatus.processing',
  completed: 'importStatus.completed',
  completed_with_errors: 'importStatus.completedWithErrors',
  failed: 'importStatus.failed',
  cancelled: 'importStatus.cancelled',
}

/**
 * 上传一批音频文件到导入队列。
 * 使用 uni.uploadFile 发送 multipart/form-data，文件字段名为 files。
 *
 * @param filePaths 本地文件路径数组（uni.chooseMessageFile / chooseFile 返回的 tempFilePath）
 * @param songRequestId 可选：关联的点歌需求 id，发布后自动标记该需求为已满足
 * @returns 新建的导入批次
 */
export async function uploadImportFiles(filePaths: string[], songRequestId?: string): Promise<LibraryImportBatch> {
  if (filePaths.length === 0) throw new Error(t('services.selectAtLeastOneAudioFile'))
  // uni.uploadFile 仅支持单文件，逐个上传会产生多个批次；
  // 这里取第一个文件上传，后续可扩展为多文件分批或后端支持多文件。
  // 实际上后端一次请求可接收多文件，但 uni.uploadFile 限制单文件，
  // 因此循环调用上传接口会创建多个批次——为保持单批次，这里只传第一个文件。
  const filePath = filePaths[0]
  return new Promise((resolve, reject) => {
    uni.uploadFile({
      url: resolveUrl(`${API_PREFIX}/admin/library/imports`),
      filePath,
      name: 'files',
      header: buildAuthHeader(),
      formData: songRequestId ? { songRequestId } : undefined,
      success: (res) => {
        try {
          const body = JSON.parse(res.data)
          if (res.statusCode >= 200 && res.statusCode < 300) {
            resolve(body as LibraryImportBatch)
          } else {
            const detail = body?.error ?? {}
            reject(new Error(detail.message || t('services.uploadFailedWithStatus', { status: res.statusCode })))
          }
        } catch {
          reject(new Error(t('services.uploadResponseParseFailed')))
        }
      },
      fail: (err) => reject(new Error(err.errMsg || t('importReview.networkError'))),
    })
  })
}

/**
 * 一次性上传「音频 + 同名歌词」的混合批次（仅 H5 可用）。
 *
 * 与 uploadImportFiles 的差异：用 fetch + FormData 在一次请求里携带多个文件，
 * 后端会把 .lrc/.txt 按文件名主干与音频配对，让歌词自动挂到对应曲目上。
 * 非 H5 端受 uni.uploadFile 单文件限制，仍需逐个上传。
 *
 * @param files 浏览器原生 File 对象数组（来自 uni.chooseFile 的 tempFiles）
 * @param songRequestId 可选：关联的点歌需求 id
 * @returns 新建的导入批次
 */
export async function uploadImportFilesBatch(files: File[], songRequestId?: string): Promise<LibraryImportBatch> {
  if (files.length === 0) throw new Error(t('services.selectAtLeastOneFile'))
  const form = new FormData()
  for (const file of files) form.append('files', file, file.name)
  if (songRequestId) form.append('songRequestId', songRequestId)

  // 不手动设置 Content-Type，交由浏览器生成带 boundary 的 multipart 头。
  const response = await fetch(resolveUrl(`${API_PREFIX}/admin/library/imports`), {
    method: 'POST',
    headers: buildAuthHeader(),
    body: form,
  })
  const body = await response.json().catch(() => null)
  if (!response.ok) {
    throw new Error(body?.error?.message ?? t('services.uploadFailedWithStatus', { status: response.status }))
  }
  return body as LibraryImportBatch
}

/**
 * 获取导入批次列表。
 *
 * @param status 按批次状态过滤（可选）
 * @param page 页码
 * @param pageSize 每页条数
 */
export async function getImportBatches(
  status?: string,
  page = 1,
  pageSize = 20,
): Promise<ApiPageResult<LibraryImportBatch>> {
  const params = new URLSearchParams()
  if (status) params.set('status', status)
  params.set('page', String(page))
  params.set('pageSize', String(pageSize))
  return http.get<ApiPageResult<LibraryImportBatch>>(`${API_PREFIX}/admin/library/imports?${params.toString()}`)
}

/** 获取单个批次详情 */
export async function getImportBatch(batchId: string): Promise<LibraryImportBatch> {
  return http.get<LibraryImportBatch>(`${API_PREFIX}/admin/library/imports/${batchId}`)
}

/** 获取单个导入条目详情 */
export async function getImportItem(itemId: string): Promise<LibraryImportItem> {
  return http.get<LibraryImportItem>(`${API_PREFIX}/admin/library/import-items/${itemId}`)
}

/** 获取试听地址（转码后的 m4a 流）。
 *  <audio> 元素无法携带 Authorization 头，因此将访问令牌以 query string 形式附加，
 *  后端 JWT 中间件仅对 /preview、/stream 路径接受该方式。 */
export function getPreviewUrl(itemId: string): string {
  const token = getAccessToken()
  const url = resolveUrl(`${API_PREFIX}/admin/library/import-items/${itemId}/preview`)
  return token ? `${url}?access_token=${encodeURIComponent(token)}` : url
}

/**
 * 上传歌词文件（.lrc / .txt）到指定导入条目，后端解析内容并写入 lyrics 字段。
 * 使用 uni.uploadFile 发送 multipart/form-data，文件字段名为 file。
 * @param itemId 导入条目 ID
 * @param filePath 本地歌词文件路径
 * @returns 更新后的导入条目
 */
export function uploadLyricsFile(itemId: string, filePath: string): Promise<LibraryImportItem> {
  return new Promise((resolve, reject) => {
    uni.uploadFile({
      url: resolveUrl(`${API_PREFIX}/admin/library/import-items/${itemId}/lyrics-file`),
      filePath,
      name: 'file',
      header: buildAuthHeader(),
      success: (res) => {
        try {
          const body = JSON.parse(res.data)
          if (res.statusCode >= 200 && res.statusCode < 300) {
            resolve(body as LibraryImportItem)
          } else {
            const detail = body?.error ?? {}
            reject(new Error(detail.message || t('services.uploadFailedWithStatus', { status: res.statusCode })))
          }
        } catch {
          reject(new Error(t('services.uploadResponseParseFailed')))
        }
      },
      fail: (err) => reject(new Error(err.errMsg || t('importReview.networkError'))),
    })
  })
}

/** 修正导入条目的元数据（albumTitle / lyrics 传 null 表示清空该字段） */
export async function updateImportItem(
  itemId: string,
  update: {
    title?: string
    artists?: string[]
    albumTitle?: string | null
    trackNumber?: number | null
    discNumber?: number | null
    releaseYear?: number | null
    genres?: string[]
    lyrics?: string | null
  },
): Promise<LibraryImportItem> {
  return http.patch<LibraryImportItem>(`${API_PREFIX}/admin/library/import-items/${itemId}`, update)
}

/**
 * 审核通过，正式发布入库。
 * 若条目关联了点歌需求且曲目与需求不一致，后端默认拒绝；
 * 传入 forceFulfill + overrideReason 可强制关联（需管理员确认）。
 */
export async function publishImportItem(
  itemId: string,
  options: { forceFulfill?: boolean; overrideReason?: string } = {},
): Promise<{ trackId: string; status: string }> {
  const params = new URLSearchParams()
  if (options.forceFulfill) params.set('forceFulfill', 'true')
  if (options.overrideReason) params.set('overrideReason', options.overrideReason)
  const query = params.toString()
  return http.post<{ trackId: string; status: string }>(
    `${API_PREFIX}/admin/library/import-items/${itemId}/publish${query ? `?${query}` : ''}`,
  )
}

/** 拒绝导入条目，删除隔离区文件 */
export async function rejectImportItem(itemId: string): Promise<void> {
  return http.delete<void>(`${API_PREFIX}/admin/library/import-items/${itemId}`)
}

/**
 * 解除导入条目与点歌需求的关联。
 * 用于批量上传时误把多个条目挂到同一需求上，发布前逐条解绑。
 */
export async function unlinkImportItemSongRequest(itemId: string): Promise<LibraryImportItem> {
  return http.delete<LibraryImportItem>(
    `${API_PREFIX}/admin/library/import-items/${itemId}/song-request`,
  )
}

/**
 * 软删除导入条目（从列表移除，记录不物理删除）。
 * 仅对终态条目（已拒绝/已发布/处理失败）生效。
 */
export async function dismissImportItem(itemId: string): Promise<void> {
  return http.post<void>(`${API_PREFIX}/admin/library/import-items/${itemId}/dismiss`)
}

/** 构造鉴权头（供 uni.uploadFile 使用） */
function buildAuthHeader(): Record<string, string> {
  const token = getAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}
