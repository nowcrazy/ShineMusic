/**
 * 点歌请求服务：用户提交想听的歌曲、浏览点歌列表、点赞支持。
 *
 * 对齐后端 /api/v1 下的端点：
 * - 点歌列表：GET    /song-requests              （匿名可访问）
 * - 我的点歌：GET    /me/song-requests
 * - 提交点歌：POST   /song-requests
 * - 点赞：     PUT    /song-requests/{id}/votes/me  （幂等；点赞不可取消）
 * - 撤回需求：DELETE /song-requests/{id}
 *
 * 注意：投票一经提交不可撤销，因此不提供 unvote 方法。
 */
import type { ApiPageResult, ApiSongRequest, SongRequestStatus } from '@/types/music'
import { API_PREFIX, http } from './http'

/**
 * 点歌列表查询参数。
 */
export interface RequestQuery {
  /** 排序方式：按投票数(votes)或创建时间(createdAt)，默认 votes */
  sort?: 'votes' | 'createdAt'
  /** 页码，从 1 开始 */
  page?: number
  /** 每页条数 */
  pageSize?: number
}

/**
 * 获取点歌列表（匿名可访问，无需登录）。
 *
 * GET /song-requests?sort=&page=&pageSize=
 * 默认 pageSize=20，按投票数排序。
 *
 * @param query 查询参数（sort/page/pageSize）
 * @returns 点歌请求分页结果
 */
// 匿名可浏览需求列表
export async function getSongRequests(query: RequestQuery = {}) {
  const params = new URLSearchParams()
  params.set('sort', query.sort ?? 'votes')
  params.set('page', String(query.page ?? 1))
  params.set('pageSize', String(query.pageSize ?? 20))
  return http.get<ApiPageResult<ApiSongRequest>>(
    `${API_PREFIX}/song-requests?${params.toString()}`,
    false,
  )
}

/**
 * 获取当前用户提交的点歌列表。
 *
 * GET /me/song-requests?page=&pageSize=
 * 默认 pageSize=50。
 *
 * @param query 查询参数（page/pageSize，无 sort）
 * @returns 当前用户的点歌请求分页结果
 */
export async function getMySongRequests(query: RequestQuery = {}) {
  const params = new URLSearchParams()
  params.set('page', String(query.page ?? 1))
  params.set('pageSize', String(query.pageSize ?? 50))
  return http.get<ApiPageResult<ApiSongRequest>>(`${API_PREFIX}/me/song-requests?${params}`)
}

/**
 * 提交一条点歌请求。
 *
 * POST /song-requests
 *
 * @param input 点歌内容（标题、歌手、专辑、备注）
 * @returns 创建成功的点歌请求
 */
export async function createSongRequest(input: {
  title: string
  artistName: string
  albumTitle?: string
  note?: string
}) {
  return http.post<ApiSongRequest>(`${API_PREFIX}/song-requests`, input)
}

/**
 * 为某条点歌请求点赞（幂等，重复调用安全）。
 *
 * PUT /song-requests/{requestId}/votes/me
 *
 * 产品规则：一人一条需求只能点一次赞，且**不可取消**（服务端已移除取消投票端点）。
 *
 * @param requestId 点歌请求 id
 */
// 幂等：重复投票安全
export async function voteSongRequest(requestId: string) {
  return http.put(`${API_PREFIX}/song-requests/${requestId}/votes/me`)
}

/**
 * 撤回自己提交的点歌需求（软撤回，置为 withdrawn 终态）。
 * 仅提交人可撤回，且仅限 open 状态；已受理/已结束的需求不可撤回。
 *
 * DELETE /song-requests/{requestId}
 *
 * @param requestId 点歌请求 id
 */
export async function withdrawSongRequest(requestId: string) {
  return http.delete(`${API_PREFIX}/song-requests/${requestId}`)
}

/** 需求时间线上的单个事件 */
export interface SongRequestTimelineItem {
  id: string
  /** 事件类型：submitted/planned/in_progress/fulfilled/declined/withdrawn/reopened */
  kind: string
  fromStatus: string | null
  toStatus: string | null
  /** 操作人身份：user/admin/system */
  actorKind: string
  /** 备注（拒绝原因/重新开启原因）；非提交人且非管理员时为 null */
  note: string | null
  createdAt: string
}

/**
 * 获取某条需求的处理时间线（按时间正序）。
 * 匿名可访问；带登录态时若为提交人本人或管理员，事件备注（拒绝原因等）才会返回。
 *
 * GET /song-requests/{requestId}/timeline
 */
export async function getSongRequestTimeline(requestId: string) {
  return http.get<SongRequestTimelineItem[]>(
    `${API_PREFIX}/song-requests/${requestId}/timeline`,
  )
}

/** 时间线事件类型的中文标签 */
export const SONG_REQUEST_EVENT_LABEL: Record<string, string> = {
  submitted: '提交需求',
  planned: '已纳入计划',
  in_progress: '开始处理',
  fulfilled: '已入库',
  declined: '未收录',
  withdrawn: '已撤回',
  reopened: '重新开启',
}

/**
 * 点歌请求状态的中文标签与样式色调映射，供 UI 展示使用。
 */
export const SONG_REQUEST_STATUS: Record<SongRequestStatus, { label: string; tone: string }> = {
  open: { label: '待安排', tone: 'open' },
  planned: { label: '已计划', tone: 'planned' },
  in_progress: { label: '处理中', tone: 'progress' },
  fulfilled: { label: '已入库', tone: 'fulfilled' },
  declined: { label: '暂不收录', tone: 'declined' },
  withdrawn: { label: '已撤回', tone: 'withdrawn' },
}
