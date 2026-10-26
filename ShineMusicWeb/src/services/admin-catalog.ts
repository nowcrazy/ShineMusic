/**
 * 管理端曲目目录服务：管理员浏览/检索/编辑曲目元数据。
 *
 * 对齐后端 /api/v1 下的端点：
 * - GET   /admin/tracks          （曲目列表，支持检索、缺元数据、重复项筛选）
 * - PATCH /admin/tracks/{id}     （更新曲目元数据）
 */
import type { ApiAdminTrackQuery, ApiAdminTrackUpdate, ApiCreateLyric, ApiLyric, ApiPageResult, ApiTrack, ApiUpdateLyric } from '@/types/music'
import type { ApiTrackDeleteResult } from '@/types/api.generated'
import { API_PREFIX, http } from './http'

/**
 * 管理端获取曲目列表，支持关键字检索与条件筛选。
 *
 * GET /admin/tracks?q=&artistId=&albumId=&missingMetadata=&duplicates=&page=&pageSize=
 *
 * @param query 查询条件：关键字、艺术家/专辑 id、是否缺元数据、是否重复项、分页
 * @returns 曲目分页结果
 */
export async function getAdminTracks(query: ApiAdminTrackQuery = {}) {
  const params = new URLSearchParams()
  if (query.q?.trim()) params.set('q', query.q.trim())
  if (query.artistId) params.set('artistId', query.artistId)
  if (query.albumId) params.set('albumId', query.albumId)
  if (query.missingMetadata) params.set('missingMetadata', 'true')
  if (query.duplicates) params.set('duplicates', 'true')
  params.set('page', String(query.page ?? 1))
  params.set('pageSize', String(query.pageSize ?? 50))
  return http.get<ApiPageResult<ApiTrack>>(`${API_PREFIX}/admin/tracks?${params.toString()}`)
}

/**
 * 管理端更新曲目元数据。
 *
 * PATCH /admin/tracks/{trackId}
 *
 * @param trackId 曲目 id
 * @param update 待更新的字段（标题、艺术家、专辑、封面等）
 * @returns 更新后的曲目
 */
export async function updateAdminTrack(trackId: string, update: ApiAdminTrackUpdate) {
  return http.patch<ApiTrack>(`${API_PREFIX}/admin/tracks/${trackId}`, update)
}

/**
 * 管理端删除曲目：同时清理关联的音频、封面文件与曲目记录。
 *
 * DELETE /admin/tracks/{trackId}
 */
export function deleteAdminTrack(trackId: string) {
  return http.delete<ApiTrackDeleteResult>(`${API_PREFIX}/admin/tracks/${trackId}`)
}

// ===== 歌词管理 =====
// 一首曲目可挂多份歌词（多语言/多格式），管理端可增删改。

/** 获取指定曲目的全部歌词列表 */
export function getTrackLyrics(trackId: string): Promise<ApiLyric[]> {
  return http.get<ApiLyric[]>(`${API_PREFIX}/admin/tracks/${trackId}/lyrics`)
}

/** 为指定曲目新增一份歌词 */
export function createTrackLyric(trackId: string, body: ApiCreateLyric): Promise<ApiLyric> {
  return http.post<ApiLyric>(`${API_PREFIX}/admin/tracks/${trackId}/lyrics`, body)
}

/** 更新已有歌词 */
export function updateLyric(lyricId: string, body: ApiUpdateLyric): Promise<ApiLyric> {
  return http.put<ApiLyric>(`${API_PREFIX}/admin/lyrics/${lyricId}`, body)
}

/** 删除歌词 */
export function deleteLyric(lyricId: string): Promise<void> {
  return http.delete<void>(`${API_PREFIX}/admin/lyrics/${lyricId}`)
}
