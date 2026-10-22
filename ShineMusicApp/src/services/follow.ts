/**
 * 关注/粉丝服务。
 *
 * 对齐后端 /api/v1 下的端点：
 * - 我的关注：GET    /me/following                （鉴权）
 * - 我的粉丝：GET    /me/followers                （鉴权）
 * - 关注某人：PUT    /me/following/{userId}       （鉴权，幂等）
 * - 取消关注：DELETE /me/following/{userId}       （鉴权，幂等）
 * - 关注状态：GET    /users/{userId}/follow-summary（鉴权，本人匿名均返回计数）
 */
import type { ApiFollowState, ApiPageResult, ApiUserBrief } from '@/types/music'
import { API_PREFIX, http } from './http'

/**
 * 获取当前用户的关注列表（分页）。
 * @param page 页码（默认 1）
 * @param pageSize 每页条数（默认 20）
 */
export async function getFollowing(page = 1, pageSize = 20) {
  return http.get<ApiPageResult<ApiUserBrief>>(
    `${API_PREFIX}/me/following?page=${page}&pageSize=${pageSize}`,
  )
}

/**
 * 获取当前用户的粉丝列表（分页）。
 * @param page 页码（默认 1）
 * @param pageSize 每页条数（默认 20）
 */
export async function getFollowers(page = 1, pageSize = 20) {
  return http.get<ApiPageResult<ApiUserBrief>>(
    `${API_PREFIX}/me/followers?page=${page}&pageSize=${pageSize}`,
  )
}

/**
 * 关注某用户（幂等）。
 * @param userId 目标用户 id
 */
export async function followUser(userId: string) {
  return http.put(`${API_PREFIX}/me/following/${userId}`)
}

/**
 * 取消关注某用户（幂等）。
 * @param userId 目标用户 id
 */
export async function unfollowUser(userId: string) {
  return http.delete(`${API_PREFIX}/me/following/${userId}`)
}

/**
 * 获取某用户的关注状态与粉丝/关注数。
 * @param userId 目标用户 id
 * @returns 是否已关注 + 该用户粉丝数 + 该用户关注数
 */
export async function getFollowSummary(userId: string) {
  return http.get<ApiFollowState>(`${API_PREFIX}/users/${userId}/follow-summary`)
}