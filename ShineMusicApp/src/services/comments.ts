/**
 * 曲目评论服务：拉取评论、发表评论/回复、删除评论、点赞/取消点赞。
 *
 * 对齐后端 /api/v1 下的端点：
 * - 评论列表：GET    /tracks/{trackId}/comments      （匿名可访问）
 * - 发表评论：POST   /tracks/{trackId}/comments      （需登录）
 * - 删除评论：DELETE /comments/{commentId}           （本人/管理员）
 * - 点赞：    PUT    /comments/{commentId}/like      （需登录，幂等）
 * - 取消：    DELETE /comments/{commentId}/like      （需登录，幂等）
 */
import type { ApiComment, ApiCreateComment, ApiPageResult } from '@/types/music'
import { API_PREFIX, http } from './http'

/** 点赞/取消点赞后的最新状态（对齐后端 CommentLikeResult）。 */
export interface CommentLikeState {
  likeCount: number
  likedByMe: boolean
}

/**
 * 获取某曲目的顶层评论（分页，含每个顶层评论的嵌套回复）。
 * GET /tracks/{trackId}/comments?page=&pageSize=（匿名）
 * @param trackId 曲目 id
 * @param page 页码（默认 1）
 * @param pageSize 每页条数（默认 20）
 */
export async function getComments(trackId: string, page = 1, pageSize = 20) {
  return http.get<ApiPageResult<ApiComment>>(
    `${API_PREFIX}/tracks/${trackId}/comments?page=${page}&pageSize=${pageSize}`,
    false,
  )
}

/**
 * 发表评论或回复。
 * POST /tracks/{trackId}/comments（鉴权）
 * @param trackId 曲目 id
 * @param content 评论文本（1~500 字符）
 * @param parentId 父评论 id（回复时传入，须为同一曲目的顶层评论）
 */
export async function createComment(trackId: string, content: string, parentId?: string) {
  const body: ApiCreateComment = { content, parentId: parentId ?? null }
  return http.post<ApiComment>(`${API_PREFIX}/tracks/${trackId}/comments`, body)
}

/**
 * 删除本人评论（管理员可删除任意评论）。
 * DELETE /comments/{commentId}
 * @param commentId 评论 id
 */
export async function deleteComment(commentId: string) {
  return http.delete(`${API_PREFIX}/comments/${commentId}`)
}

/**
 * 点赞某条评论（幂等），返回最新点赞数与是否已点赞。
 * PUT /comments/{commentId}/like
 * @param commentId 评论 id
 */
export async function likeComment(commentId: string) {
  return http.put<CommentLikeState>(`${API_PREFIX}/comments/${commentId}/like`)
}

/**
 * 取消点赞某条评论（幂等），返回最新点赞数与是否已点赞。
 * DELETE /comments/{commentId}/like
 * @param commentId 评论 id
 */
export async function unlikeComment(commentId: string) {
  return http.delete<CommentLikeState>(`${API_PREFIX}/comments/${commentId}/like`)
}