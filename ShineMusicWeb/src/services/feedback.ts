/**
 * 收听行为与显式反馈服务：为个性化推荐提供行为数据。
 *
 * 对齐后端 /api/v1 端点：
 * - 收听事件：POST   /me/play-events                （play / complete / skip）
 * - 反馈：    GET    /me/feedback                   （拉取我的反馈）
 *             PUT    /me/feedback/{trackId}          （like / dislike / not_interested）
 *             DELETE /me/feedback/{trackId}          （清除反馈）
 *
 * 设计：
 * - reportPlayEvent 为 fire-and-forget，未登录或失败均静默忽略，绝不阻塞/影响播放；
 * - 反馈使用乐观更新 + 失败回滚，与 library.toggleFavorite 一致；
 * - feedbackMap 为 module 级单例，供 UI 快速读取（如列表标记“不感兴趣”）。
 */
import { ref } from 'vue'
import { API_PREFIX, http } from './http'
import { isAuthenticated } from './session'

/** 收听事件类型。 */
export type PlayEventType = 'play' | 'complete' | 'skip'

/** 显式反馈类型。 */
export type FeedbackKind = 'like' | 'dislike' | 'not_interested'

/** 本地反馈缓存：trackId -> 反馈类型（module 级单例，全局共享）。 */
export const feedbackMap = ref<Map<string, FeedbackKind>>(new Map())

/** 后端返回的反馈项 DTO。 */
interface ApiUserFeedback {
  trackId: string
  kind: FeedbackKind
  updatedAtUtc: string
}

/**
 * 上报一条收听事件（fire-and-forget）。
 * 未登录或请求失败时静默忽略，不抛出、不阻塞。
 * @param trackId 曲目 ID
 * @param eventType 事件类型 play / complete / skip
 * @param positionMs 事件发生时的播放进度（毫秒），可选
 */
export function reportPlayEvent(trackId: string, eventType: PlayEventType, positionMs?: number): void {
  if (!isAuthenticated.value || !trackId) return
  void http
    .post(`${API_PREFIX}/me/play-events`, {
      trackId,
      eventType,
      positionMs: typeof positionMs === 'number' ? Math.max(0, Math.round(positionMs)) : null,
    })
    .catch(() => {
      /* 行为上报失败静默忽略，不影响播放体验 */
    })
}

/**
 * 设置对某曲目的反馈（乐观更新，失败回滚）。
 * @param trackId 曲目 ID
 * @param kind 反馈类型
 */
export async function setFeedback(trackId: string, kind: FeedbackKind): Promise<void> {
  const prev = feedbackMap.value.get(trackId)
  const next = new Map(feedbackMap.value)
  next.set(trackId, kind)
  feedbackMap.value = next
  try {
    await http.put(`${API_PREFIX}/me/feedback/${trackId}`, { kind })
  } catch (error) {
    const rollback = new Map(feedbackMap.value)
    if (prev) rollback.set(trackId, prev)
    else rollback.delete(trackId)
    feedbackMap.value = rollback
    throw error
  }
}

/**
 * 清除对某曲目的反馈（乐观更新，失败回滚）。
 * @param trackId 曲目 ID
 */
export async function clearFeedback(trackId: string): Promise<void> {
  const prev = feedbackMap.value.get(trackId)
  if (!prev) return
  const next = new Map(feedbackMap.value)
  next.delete(trackId)
  feedbackMap.value = next
  try {
    await http.delete(`${API_PREFIX}/me/feedback/${trackId}`)
  } catch (error) {
    const rollback = new Map(feedbackMap.value)
    rollback.set(trackId, prev)
    feedbackMap.value = rollback
    throw error
  }
}

/**
 * 切换“不感兴趣”状态。
 * @param trackId 曲目 ID
 * @returns 操作后是否处于“不感兴趣”状态
 */
export async function toggleNotInterested(trackId: string): Promise<boolean> {
  if (feedbackMap.value.get(trackId) === 'not_interested') {
    await clearFeedback(trackId)
    return false
  }
  await setFeedback(trackId, 'not_interested')
  return true
}

/**
 * 拉取当前用户的反馈列表并写入本地缓存（登录后调用；失败静默）。
 */
export async function loadFeedback(): Promise<void> {
  if (!isAuthenticated.value) return
  try {
    const items = await http.get<ApiUserFeedback[]>(`${API_PREFIX}/me/feedback`)
    const map = new Map<string, FeedbackKind>()
    for (const item of items) map.set(item.trackId, item.kind)
    feedbackMap.value = map
  } catch {
    /* 忽略：反馈拉取失败不影响主流程 */
  }
}

/** 清空本地反馈缓存（登出/切换用户时调用）。 */
export function resetFeedback(): void {
  feedbackMap.value = new Map()
}
