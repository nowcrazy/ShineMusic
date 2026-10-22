/**
 * 站内信服务：未读角标、消息列表、标记已读。
 *
 * 对齐后端 /api/v1/me/notifications 系列端点：
 * - GET    /me/notifications?unreadOnly=&page=&pageSize=
 * - GET    /me/notifications/unread-count
 * - POST   /me/notifications/{id}/read
 * - POST   /me/notifications/read-all
 * - DELETE /me/notifications/{id}
 *
 * 未读数用模块级 ref 暴露：抽屉/角标/消息页共享同一份状态，任意一处刷新即全局同步。
 */
import { ref } from 'vue'
import type { ApiPageResult } from '@/types/music'
import { API_PREFIX, http } from './http'
import { isAuthenticated } from './session'

/** 站内信列表项 */
export interface ApiNotification {
  id: string
  /** 消息类型：song_request.declined / song_request.fulfilled / song_request.reopened 等 */
  kind: string
  title: string
  body: string | null
  /** 点击后跳转的前端页面路径；null 表示不可跳转 */
  linkUrl: string | null
  resourceType: string | null
  resourceId: string | null
  isRead: boolean
  createdAt: string
}

/** 全局未读消息数（0 表示无未读，角标隐藏） */
export const unreadCount = ref(0)

/**
 * 拉取消息列表。
 * @param query unreadOnly=只看未读；page/pageSize 分页
 */
export async function getNotifications(
  query: { unreadOnly?: boolean; page?: number; pageSize?: number } = {},
) {
  const params = new URLSearchParams()
  if (query.unreadOnly) params.set('unreadOnly', 'true')
  params.set('page', String(query.page ?? 1))
  params.set('pageSize', String(query.pageSize ?? 20))
  return http.get<ApiPageResult<ApiNotification>>(
    `${API_PREFIX}/me/notifications?${params.toString()}`,
  )
}

/**
 * 刷新全局未读数（失败静默：角标不应打断用户操作）。
 * 未登录时不发请求，直接把计数归零。
 */
export async function refreshUnreadCount() {
  // 未登录不发请求（否则匿名用户每次进首页都吃一个 401），直接把计数归零
  if (!isAuthenticated.value) {
    unreadCount.value = 0
    return
  }
  try {
    const result = await http.get<{ count: number }>(
      `${API_PREFIX}/me/notifications/unread-count`,
    )
    unreadCount.value = result.count
  } catch {
    // 网络异常：保持上一次的值，不打扰用户
  }
}

/** 标记单条消息为已读，并本地同步未读数 */
export async function markNotificationRead(notificationId: string) {
  await http.post(`${API_PREFIX}/me/notifications/${notificationId}/read`)
  unreadCount.value = Math.max(0, unreadCount.value - 1)
}

/** 标记全部已读 */
export async function markAllNotificationsRead() {
  await http.post(`${API_PREFIX}/me/notifications/read-all`)
  unreadCount.value = 0
}

/** 删除单条消息 */
export async function deleteNotification(notificationId: string) {
  await http.delete(`${API_PREFIX}/me/notifications/${notificationId}`)
}

/** 退出登录时清空本地未读状态 */
export function resetNotifications() {
  unreadCount.value = 0
}
