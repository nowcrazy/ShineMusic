/**
 * 管理端消息队列监控服务。
 *
 * 对齐后端 /api/v1/admin/mq 系列端点（仅管理员）：
 * - GET /admin/mq/queues  查询 RabbitMQ 全部队列的积压、处理中、消费者与收发速率
 *   （数据来自 RabbitMQ Management HTTP API，插件不可达时后端返回 502 upstream_unavailable）
 */
import type {
  ApiFailedMessageOutboxList,
  ApiMessageDeadLetterList,
  ApiMessageQueueStatus,
  ApiMessageQueueTaskList,
} from '@/types/api.generated'
import { API_PREFIX, http } from './http'

/** 查询 RabbitMQ 全部队列状态。 */
export function getMessageQueues(): Promise<ApiMessageQueueStatus[]> {
  return http.get<ApiMessageQueueStatus[]>(`${API_PREFIX}/admin/mq/queues`)
}

/** 查询指定业务队列中等待或处理中的任务明细。 */
export function getMessageQueueTasks(
  queue: string,
  state: 'all' | 'waiting' | 'processing',
  limit = 50,
  page = 1,
): Promise<ApiMessageQueueTaskList> {
  const query = new URLSearchParams({ queue, state, limit: String(limit), page: String(page) })
  return http.get<ApiMessageQueueTaskList>(`${API_PREFIX}/admin/mq/tasks?${query.toString()}`)
}

function buildReliabilityQuery(queue: string, page: number, pageSize: number) {
  const query = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  if (queue) query.set('queue', queue)
  return query.toString()
}

export function getMessageDeadLetters(queue = '', page = 1, pageSize = 20): Promise<ApiMessageDeadLetterList> {
  return http.get<ApiMessageDeadLetterList>(
    `${API_PREFIX}/admin/mq/dead-letters?${buildReliabilityQuery(queue, page, pageSize)}`,
  )
}

export function replayMessageDeadLetter(id: string): Promise<void> {
  return http.post<void>(`${API_PREFIX}/admin/mq/dead-letters/${id}/replay`)
}

export function getFailedMessageOutbox(queue = '', page = 1, pageSize = 20): Promise<ApiFailedMessageOutboxList> {
  return http.get<ApiFailedMessageOutboxList>(
    `${API_PREFIX}/admin/mq/dead-letters/outbox/failed?${buildReliabilityQuery(queue, page, pageSize)}`,
  )
}

export function retryFailedMessageOutbox(id: string): Promise<void> {
  return http.post<void>(`${API_PREFIX}/admin/mq/dead-letters/outbox/${id}/retry`)
}
