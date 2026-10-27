/**
 * 管理端消息队列监控服务。
 *
 * 对齐后端 /api/v1/admin/mq 系列端点（仅管理员）：
 * - GET /admin/mq/queues  查询 RabbitMQ 全部队列的积压、处理中、消费者与收发速率
 *   （数据来自 RabbitMQ Management HTTP API，插件不可达时后端返回 502 upstream_unavailable）
 */
import type { ApiMessageQueueStatus } from '@/types/api.generated'
import { API_PREFIX, http } from './http'

/** 查询 RabbitMQ 全部队列状态。 */
export function getMessageQueues(): Promise<ApiMessageQueueStatus[]> {
  return http.get<ApiMessageQueueStatus[]>(`${API_PREFIX}/admin/mq/queues`)
}
