/**
 * 管理端运营歌单服务：歌单的创建、信息修改、删除与曲目编排。
 *
 * 对齐后端 /api/v1/admin/collections 下的端点（仅管理员）：
 * - POST   /admin/collections            创建歌单（标题 + 曲目数组 + 可选描述/封面）
 * - PUT    /admin/collections/{id}       修改歌单信息（标题/描述/发布态/封面，部分更新）
 * - DELETE /admin/collections/{id}       删除歌单
 * - PUT    /admin/collections/{id}/tracks 整体替换曲目（按数组顺序重排）
 *
 * 前台读取复用 services/catalog 的 getCollections/getCollection（匿名端点）。
 */
import type { ApiCollectionSummary, ApiTrack, ApiPageResult } from '@/types/music'
import { API_PREFIX, http } from './http'

/** 创建歌单入参 */
export interface CreateCollectionInput {
  title: string // 歌单标题（必填，≤100 字符）
  description?: string | null // 推荐语（可选，≤500 字符）
  trackIds: string[] // 曲目 ID 数组（顺序即歌单内顺序，≤200 首）
  artworkId?: string | null // 封面 ID（可选；未指定时自动取首曲封面）
}

/** 修改歌单信息入参（部分更新，仅传需变更的字段） */
export interface UpdateCollectionInput {
  title?: string // 新标题（可选）
  description?: string | null // 新描述（可选，传空串清空）
  isPublished?: boolean // 发布态（可选；false 后前台不可见）
  artworkId?: string | null // 新封面 ID（可选）
}

/** 创建运营歌单，返回新建歌单摘要。 */
export function createCollection(input: CreateCollectionInput): Promise<ApiCollectionSummary> {
  return http.post<ApiCollectionSummary>(`${API_PREFIX}/admin/collections`, input)
}

/** 修改歌单信息（部分更新），返回更新后的歌单摘要。 */
export function updateCollection(collectionId: string, input: UpdateCollectionInput): Promise<ApiCollectionSummary> {
  return http.put<ApiCollectionSummary>(`${API_PREFIX}/admin/collections/${collectionId}`, input)
}

/** 删除歌单（含集合内曲目关联，曲目本体不受影响）。 */
export function deleteCollection(collectionId: string): Promise<void> {
  return http.delete<void>(`${API_PREFIX}/admin/collections/${collectionId}`)
}

/** 整体替换歌单曲目（按数组顺序重排），返回更新后的歌单摘要。 */
export function replaceCollectionTracks(collectionId: string, trackIds: string[]): Promise<ApiCollectionSummary> {
  return http.put<ApiCollectionSummary>(`${API_PREFIX}/admin/collections/${collectionId}/tracks`, { trackIds })
}

/**
 * 管理端检索曲目（供歌单编排时挑选曲目）。
 * 复用前台目录搜索端点（已发布曲目）。
 *
 * @param query 关键字
 * @param page 页码（默认 1）
 * @param pageSize 每页数量（默认 50）
 */
export function searchTracksForCollection(query: string, page = 1, pageSize = 50): Promise<ApiPageResult<ApiTrack>> {
  const params = new URLSearchParams()
  if (query.trim()) params.set('q', query.trim())
  params.set('page', String(page))
  params.set('pageSize', String(pageSize))
  return http.get<ApiPageResult<ApiTrack>>(`${API_PREFIX}/tracks?${params.toString()}`)
}
