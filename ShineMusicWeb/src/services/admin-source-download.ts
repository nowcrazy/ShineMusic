/**
 * 管理端外部音源抓歌服务：搜索 Audius/Jamendo → 创建下载任务 → 跟踪任务 → 重试失败任务。
 *
 * 对齐后端 /api/v1/admin/source-downloads 系列端点（仅管理员，受功能开关 SourceDownload 控制）：
 * - GET  /search?query=&provider=&limit=&mode=  精确搜索歌曲、歌手或专辑（provider=all 聚合各启用源）
 * - POST /tasks                            批量创建下载任务（去重后入队）
 * - GET  /tasks?status=&page=&pageSize=&sortBy=&sortDirection=    任务分页列表
 * - GET  /tasks/{id}                       任务详情
 * - POST /tasks/{id}/retry                 重试失败任务
 */
import type {
  ApiCreateSourceDownloadTasksResult,
  ApiExternalDownloadTask,
  ApiExternalDownloadTaskList,
  ApiSourceSearch,
  ApiSourceSearchItem,
  ApiSwitchSourceResponse,
} from '@/types/api.generated'
import { API_PREFIX, http, request } from './http'

export type SourceProviderName = 'audius' | 'jamendo' | 'kuwo' | 'kugou' | 'migu' | 'netease';
export type SourceProviderFilter = 'all' | SourceProviderName
export type SourceSearchMode = 'title' | 'artist' | 'artist-track' | 'artist-album' | 'album'

/** 音源展示名（界面徽章用；未知名称回退原始 id） */
export const SourceProviderLabel: Record<string, string> = {
  audius: 'Audius',
  jamendo: 'Jamendo',
  ytmusic: 'YT Music',
  kuwo: 'sourceDownload.providerKuwo',
  kugou: 'sourceDownload.providerKugou',
  migu: 'sourceDownload.providerMigu',
  netease: 'sourceDownload.providerNetease',
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

/** 按精确条件搜索曲目或专辑。provider 缺省为 all（并行聚合各启用源，单源失败见 providerErrors）。
 *  聚合搜索可能触发多个外部源串行请求，超时放大到 90s。 */
export async function searchSources(
  query: string,
  provider: SourceProviderFilter = 'all',
  limit = 20,
  mode: SourceSearchMode = 'title',
): Promise<ApiSourceSearch> {
  const params = new URLSearchParams()
  params.set('query', query)
  params.set('provider', provider)
  params.set('limit', String(limit))
  params.set('mode', mode)
  return request<ApiSourceSearch>('GET', `${API_PREFIX}/admin/source-downloads/search?${params.toString()}`, undefined, {
    timeoutMs: 90000,
  })
}

/** 音质排序权重（与后端 SourceQualities.Rank 对齐，值越小越好）。 */
const qualityRank: Record<string, number> = {
  lossless: 0,
  high: 1,
  standard: 2,
}

/** 按音质从好到坏排序（未知音质排最后）。 */
function sortByQuality(items: SourceSearchItem[]): SourceSearchItem[] {
  return [...items].sort((a, b) => (qualityRank[a.quality] ?? 3) - (qualityRank[b.quality] ?? 3))
}

/** 归一化标题+艺人作为同曲分组键。 */
function songKey(item: SourceSearchItem): string {
  return `${item.title.trim().toLowerCase()}|${item.artists.join(',').trim().toLowerCase()}`
}

/**
 * 批量创建下载任务，返回新建任务与被跳过的重复曲目。
 * 多候选换源模式：当选中的条目跨 provider 时，按音质排序后用 Candidates 模式提交——
 * 下载阶段自动按音质从好到坏逐个尝试，失败换源直到成功或全部耗尽（标注原因）。
 * 同一曲名+艺人的跨 provider 结果分到同一组，组装为多候选换源任务。
 * 不同曲目分到不同组，逐组提交。
 * @param items 搜索结果条目（可跨 provider，同曲不同源会组装为多候选换源任务）。
 */
export async function createDownloadTasks(
  items: SourceSearchItem[],
): Promise<ApiCreateSourceDownloadTasksResult> {
  if (items.length === 0) throw new Error('No tracks were selected for download.')

  // 按同曲分组：同一 (归一化标题+艺人) 的条目为一组，可能跨 provider。
  const groups = new Map<string, SourceSearchItem[]>()
  for (const item of items) {
    const key = songKey(item)
    const list = groups.get(key) ?? []
    list.push(item)
    groups.set(key, list)
  }

  const allCreated: ApiExternalDownloadTask[] = []
  const allSkipped: string[] = []

  for (const [, groupItems] of groups) {
    // 单 provider 单条：旧模式。
    if (groupItems.length === 1) {
      const item = groupItems[0]
      const metadata: Record<string, ApiSourceSearchItem> = { [item.providerTrackId]: item }
      const result = await http.post<ApiCreateSourceDownloadTasksResult>(`${API_PREFIX}/admin/source-downloads/tasks`, {
        provider: item.provider,
        providerTrackIds: [item.providerTrackId],
        metadata,
      })
      allCreated.push(...result.created)
      allSkipped.push(...result.skippedDuplicates)
      continue
    }

    // 同一 provider 多条：旧批量模式。
    const sameProvider = groupItems.every(i => i.provider === groupItems[0].provider)
    if (sameProvider) {
      const provider = groupItems[0].provider
      const providerTrackIds = groupItems.map((item) => item.providerTrackId)
      const metadata: Record<string, ApiSourceSearchItem> = {}
      for (const item of groupItems) metadata[item.providerTrackId] = item
      const result = await http.post<ApiCreateSourceDownloadTasksResult>(`${API_PREFIX}/admin/source-downloads/tasks`, {
        provider,
        providerTrackIds,
        metadata,
      })
      allCreated.push(...result.created)
      allSkipped.push(...result.skippedDuplicates)
      continue
    }

    // 跨 provider 同曲：多候选换源模式，按音质排序。
    const sorted = sortByQuality(groupItems)
    const primary = sorted[0]
    const metadata: Record<string, ApiSourceSearchItem> = {}
    for (const item of sorted) metadata[item.providerTrackId] = item
    const result = await http.post<ApiCreateSourceDownloadTasksResult>(`${API_PREFIX}/admin/source-downloads/tasks`, {
      provider: primary.provider,
      providerTrackIds: [primary.providerTrackId],
      metadata,
      candidates: sorted,
    })
    allCreated.push(...result.created)
    allSkipped.push(...result.skippedDuplicates)
  }

  return { created: allCreated, skippedDuplicates: allSkipped }
}

/** 换源：对已失败任务换一个音源平台重新创建下载任务。 */
export async function switchSource(taskId: string, provider?: string): Promise<ApiSwitchSourceResponse> {
  return http.post<ApiSwitchSourceResponse>(`${API_PREFIX}/admin/source-downloads/tasks/${taskId}/switch-source`, {
    provider: provider ?? null,
  })
}

/** 任务分页列表（可按状态过滤、按曲名/艺人/专辑搜关键词）。 */
export async function getDownloadTasks(
  status?: string,
  page = 1,
  pageSize = 20,
  q?: string,
  sortBy = 'createdAt',
  sortDirection = 'desc',
): Promise<ApiExternalDownloadTaskList> {
  const params = new URLSearchParams()
  if (status) params.set('status', status)
  if (q) params.set('q', q)
  params.set('page', String(page))
  params.set('pageSize', String(pageSize))
  params.set('sortBy', sortBy)
  params.set('sortDirection', sortDirection)
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
