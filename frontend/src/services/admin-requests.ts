/**
 * 管理端点歌需求服务：查看需求队列、推进状态、拒绝（附原因）。
 *
 * 对齐后端 /api/v1/admin/song-requests 系列端点：
 * - GET   /admin/song-requests?status=&sort=&page=&pageSize=
 * - PATCH /admin/song-requests/{id}    （status / adminNote / matchedTrackId）
 */
import type { ApiPageResult, ApiSongRequest, SongRequestStatus } from '@/types/music'
import { API_PREFIX, http } from './http'

export interface AdminRequestQuery {
  /** 按状态过滤；空字符串/undefined 表示全部 */
  status?: SongRequestStatus | ''
  /** 排序：votes（投票优先）或 createdAt（最新提交） */
  sort?: 'votes' | 'createdAt'
  page?: number
  pageSize?: number
}

/** 获取管理端需求队列（仅管理员） */
export async function getAdminSongRequests(query: AdminRequestQuery = {}) {
  const params = new URLSearchParams()
  if (query.status) params.set('status', query.status)
  params.set('sort', query.sort ?? 'votes')
  params.set('page', String(query.page ?? 1))
  params.set('pageSize', String(query.pageSize ?? 50))
  return http.get<ApiPageResult<ApiSongRequest>>(
    `${API_PREFIX}/admin/song-requests?${params.toString()}`,
  )
}

/** 修改需求状态（推进/拒绝）；拒绝时必须提供 adminNote 作为原因 */
export async function updateSongRequestStatus(
  requestId: string,
  input: { status: SongRequestStatus; adminNote?: string; matchedTrackId?: string | null },
) {
  return http.patch<ApiSongRequest>(`${API_PREFIX}/admin/song-requests/${requestId}`, input)
}

/**
 * 重新开启一条已结束的需求（declined/withdrawn → open）。
 * 原因必填：会写入时间线并通知提交人，同时保留原拒绝/撤回原因作为历史。
 *
 * POST /admin/song-requests/{id}/reopen
 */
export async function reopenSongRequest(requestId: string, reason: string) {
  return http.post<ApiSongRequest>(`${API_PREFIX}/admin/song-requests/${requestId}/reopen`, { reason })
}
