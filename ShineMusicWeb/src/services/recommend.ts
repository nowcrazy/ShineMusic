/**
 * 每日推荐服务。
 *
 * 对齐后端端点：GET /api/v1/recommendations/daily?limit=30（匿名可访问）。
 * - 携带有效 JWT 时返回该用户的个性化推荐（基于播放次数/跳过/反馈/收藏）；
 * - 未登录或冷启动时返回全局热度 + 最新兜底列表（personalized=false）。
 *
 * 后端保证：默认 30 首、互不重复、同一自然日内结果稳定。
 *
 * 缓存策略：模块级内存缓存 + stale-while-revalidate。
 * 同一自然日内结果不变，跨日时静默刷新即可。
 */
import type { ApiTrack, Track } from '@/types/music'
import { API_PREFIX, assertSessionRevisionCurrent, captureSessionRevision, http } from './http'
import { mapTrack } from './catalog'

/** 每日推荐结果视图模型。 */
export interface DailyRecommendation {
  date: string // 推荐日期 yyyy-MM-dd
  personalized: boolean // 是否为个性化结果
  tracks: Track[] // 映射后的曲目列表
}

/** 后端返回的每日推荐 DTO。 */
interface ApiDailyRecommendation {
  date: string
  personalized: boolean
  tracks: ApiTrack[]
}

// ============ 模块级内存缓存（stale-while-revalidate）============
let _dailyCache: DailyRecommendation | null = null
let _dailyInFlight: Promise<DailyRecommendation> | null = null

async function fetchDailyRaw(limit?: number): Promise<DailyRecommendation> {
  const revision = captureSessionRevision()
  const path = limit == null
    ? `${API_PREFIX}/recommendations/daily`
    : `${API_PREFIX}/recommendations/daily?limit=${limit}`
  const data = await http.get<ApiDailyRecommendation>(
    path,
    false,
  )
  assertSessionRevisionCurrent(revision)
  return {
    date: data.date,
    personalized: data.personalized,
    tracks: data.tracks.map(mapTrack),
  }
}

async function refreshDailySilently(limit?: number): Promise<void> {
  const revision = captureSessionRevision()
  try {
    const result = await fetchDailyRaw(limit)
    assertSessionRevisionCurrent(revision)
    _dailyCache = result
  } catch {
    // 静默失败
  }
}

/**
 * 获取每日推荐（带模块级内存缓存）。
 * - 首次调用：发请求，缓存后返回。
 * - 后续调用：立即返回缓存，后台静默刷新。
 * - 后端保证同一自然日内结果稳定，所以缓存跨次调用完全安全。
 * - 跨日时刷新会自动替换缓存。
 */
export async function getDailyRecommendations(limit?: number): Promise<DailyRecommendation> {
  const revision = captureSessionRevision()
  if (_dailyCache) {
    void refreshDailySilently(limit)
    return _dailyCache
  }
  if (!_dailyInFlight) {
    const promise = fetchDailyRaw(limit).finally(() => {
      if (_dailyInFlight === promise) _dailyInFlight = null
    })
    _dailyInFlight = promise
  }
  const result = await _dailyInFlight
  assertSessionRevisionCurrent(revision)
  _dailyCache = result
  return _dailyCache
}

/** 同步读取每日推荐缓存（页面先用它填充，避免 loading 闪烁）。 */
export function peekDailyCache(): DailyRecommendation | null {
  return _dailyCache
}

/** 丢弃上一会话的推荐及单飞入口；旧执行只会拒绝，不会回填。 */
export function resetDailyRecommendations(): void {
  _dailyCache = null
  _dailyInFlight = null
}
