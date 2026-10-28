/**
 * 管理端外部音源抓歌服务：搜索 Audius/Jamendo → 创建下载任务 → 跟踪任务 → 重试失败任务。
 *
 * 对齐后端 /api/v1/admin/source-downloads 系列端点（仅管理员，受功能开关 SourceDownload 控制）：
 * - GET  /search?query=&provider=&limit=   搜索音源平台（provider=all 聚合各启用源）
 * - POST /tasks                            批量创建下载任务（去重后入队）
 * - GET  /tasks?status=&page=&pageSize=    任务分页列表
 * - GET  /tasks/{id}                       任务详情
 * - POST /tasks/{id}/retry                 重试失败任务
 */
import type {
  ApiCreateSourceDownloadTasksResult,
  ApiExternalDownloadTask,
  ApiExternalDownloadTaskList,
  ApiSourceSearch,
  ApiSourceSearchItem,
} from '@/types/api.generated'
import { API_PREFIX, http, request } from './http'

export type SourceProviderName = 'audius' | 'jamendo' | 'ytmusic' | 'soundcloud' | 'kuwo' | 'kugou' | 'netease';
export type SourceProviderFilter = 'all' | SourceProviderName

/** 音源展示名（界面徽章用；未知名称回退原始 id） */
export const SourceProviderLabel: Record<string, string> = {
  audius: 'Audius',
  jamendo: 'Jamendo',
  ytmusic: 'YT Music',
  soundcloud: 'SoundCloud',
  kuwo: '酷我',
  kugou: '酷狗',
  netease: '网易云',
}

export type SourceSearchItem = ApiSourceSearchItem
export type ExternalDownloadTask = ApiExternalDownloadTask

/** 任务状态常量（与后端 ExternalDownloadTaskStatuses 对齐） */
export const SourceTaskStatus = {
  Queued: 'queued',
  Downloading: 'downloading',
  ImportQueued: 'import_queued',
  Completed: 'completed',
  Failed: 'failed',
} as const

/** 状态标签（值为 sourceDownload.status.* i18n key，界面用 t() 实时本地化） */
export const SourceTaskStatusLabel: Record<string, string> = {
  queued: 'sourceDownload.status.queued',
  downloading: 'sourceDownload.status.downloading',
  import_queued: 'sourceDownload.status.importQueued',
  completed: 'sourceDownload.status.completed',
  failed: 'sourceDownload.status.failed',
}

/** 音质徽章标签（值为 sourceDownload.quality.* i18n key） */
export const SourceQualityLabel: Record<string, string> = {
  lossless: 'sourceDownload.quality.lossless',
  high: 'sourceDownload.quality.high',
  standard: 'sourceDownload.quality.standard',
}

/** 搜索音源平台。provider 缺省为 all（并行聚合各启用源，单源失败见 providerErrors）。
 *  聚合搜索可能触发多个外部源串行请求，超时放大到 90s。 */
export async function searchSources(
  query: string,
  provider: SourceProviderFilter = 'all',
  limit = 20,
): Promise<ApiSourceSearch> {
  const params = new URLSearchParams()
  params.set('query', query)
  params.set('provider', provider)
  params.set('limit', String(limit))
  return request<ApiSourceSearch>('GET', `${API_PREFIX}/admin/source-downloads/search?${params.toString()}`, undefined, {
    timeoutMs: 90000,
  })
}

/**
 * 批量创建下载任务，返回新建任务与被跳过的重复曲目。
 * @param items 搜索结果条目（从中提取 providerTrackId 与元数据）。
 * 元数据随请求传入，避免后端再次调用已失效的平台详情接口（如酷我 getNewMuiseByRid）。
 */
export async function createDownloadTasks(
  items: SourceSearchItem[],
): Promise<ApiCreateSourceDownloadTasksResult> {
  if (items.length === 0) throw new Error('请先选择要下载的曲目')
  const provider = items[0].provider
  const providerTrackIds = items.map((item) => item.providerTrackId)
  const metadata: Record<string, ApiSourceSearchItem> = {}
  for (const item of items) metadata[item.providerTrackId] = item
  return http.post<ApiCreateSourceDownloadTasksResult>(`${API_PREFIX}/admin/source-downloads/tasks`, {
    provider,
    providerTrackIds,
    metadata,
  })
}

/** 任务分页列表（可按状态过滤、按曲名/艺人/专辑搜关键词）。 */
export async function getDownloadTasks(
  status?: string,
  page = 1,
  pageSize = 20,
  q?: string,
): Promise<ApiExternalDownloadTaskList> {
  const params = new URLSearchParams()
  if (status) params.set('status', status)
  if (q) params.set('q', q)
  params.set('page', String(page))
  params.set('pageSize', String(pageSize))
  return http.get<ApiExternalDownloadTaskList>(`${API_PREFIX}/admin/source-downloads/tasks?${params.toString()}`)
}

/** 任务详情。 */
export async function getDownloadTask(taskId: string): Promise<ExternalDownloadTask> {
  return http.get<ExternalDownloadTask>(`${API_PREFIX}/admin/source-downloads/tasks/${taskId}`)
}

/** 重试失败任务（仅 failed 状态可重试）。 */
export async function retryDownloadTask(taskId: string): Promise<ExternalDownloadTask> {
  return http.post<ExternalDownloadTask>(`${API_PREFIX}/admin/source-downloads/tasks/${taskId}/retry`)
}
