/**
 * 每日推荐服务。
 *
 * 对齐后端端点：GET /api/v1/recommendations/daily?limit=30（匿名可访问）。
 * - 携带有效 JWT 时返回该用户的个性化推荐（基于播放次数/跳过/反馈/收藏）；
 * - 未登录或冷启动时返回全局热度 + 最新兜底列表（personalized=false）。
 *
 * 后端保证：默认 30 首、互不重复、同一自然日内结果稳定。
 */
import type { ApiTrack, Track } from '@/types/music'
import { API_PREFIX, http } from './http'
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

/**
 * 获取每日推荐。
 * @param limit 返回条数（后端夹紧到 1-50，默认 30）
 * @returns 推荐结果（曲目已映射为界面 Track）
 * @throws 网络或后端错误（由调用方决定兜底策略）
 */
export async function getDailyRecommendations(limit = 30): Promise<DailyRecommendation> {
  const data = await http.get<ApiDailyRecommendation>(
    `${API_PREFIX}/recommendations/daily?limit=${limit}`,
  )
  return {
    date: data.date,
    personalized: data.personalized,
    tracks: data.tracks.map(mapTrack),
  }
}
