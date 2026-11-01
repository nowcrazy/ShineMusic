/**
 * 管理端个性化推荐策略服务。
 *
 * - GET /admin/recommendations/strategy 查询当前与次日生效策略
 * - PUT /admin/recommendations/strategy 安排次日策略
 */
import type {
  ApiRecommendationStrategyAdmin,
  ApiRecommendationStrategyValues,
} from '@/types/api.generated'
import { API_PREFIX, http } from './http'

/** 次日生效的策略配置。 */
export type UpdateRecommendationStrategyInput = ApiRecommendationStrategyValues

export function getRecommendationStrategy(): Promise<ApiRecommendationStrategyAdmin> {
  return http.get<ApiRecommendationStrategyAdmin>(`${API_PREFIX}/admin/recommendations/strategy`)
}

export function updateRecommendationStrategy(
  input: UpdateRecommendationStrategyInput,
): Promise<ApiRecommendationStrategyAdmin> {
  return http.put<ApiRecommendationStrategyAdmin>(`${API_PREFIX}/admin/recommendations/strategy`, input)
}
