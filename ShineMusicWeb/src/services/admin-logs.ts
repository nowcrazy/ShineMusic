/**
 * 管理端日志服务：登录审计与操作审计查询。
 *
 * 对齐后端 /api/v1/admin/logs（仅管理员）：
 * - GET /admin/logs/login?result=&query=&page=&pageSize=       登录审计
 * - GET /admin/logs/operations?query=&page=&pageSize=          操作审计
 */
import { API_PREFIX, http } from './http'

export interface LoginAuditItem {
  id: string
  login: string
  userId: string | null
  result: 'success' | 'failure'
  reason: string
  ip: string
  clientType: string | null
  deviceInfo: string | null
  createdAtUtc: string
}

export interface OperationAuditItem {
  id: string
  actorUserId: string
  actorName: string | null
  action: string
  resourceType: string
  resourceId: string
  detailsJson: string | null
  ip: string | null
  clientType: string | null
  deviceInfo: string | null
  createdAtUtc: string
}

export interface AuditPage<T> {
  items: T[]
  page: number
  pageSize: number
  total: number
}

/** 登录失败原因 → 中文标签。 */
export const LoginReasonLabel: Record<string, string> = {
  ok: '成功',
  account_not_found: '账号不存在',
  bad_password: '密码错误',
  account_locked: '账号已锁定',
  missing_credentials: '凭证缺失',
}

/** 客户端类型 → 中文标签。 */
export const ClientTypeLabel: Record<string, string> = {
  'web-admin': 'PC 后台',
  'web-app': 'PC 网页',
  app: 'App',
  unknown: '未知',
}

/** 登录审计分页列表。result 过滤 success / failure。 */
export function getLoginAuditLogs(
  result?: string,
  query?: string,
  page = 1,
  pageSize = 20,
): Promise<AuditPage<LoginAuditItem>> {
  const params = new URLSearchParams()
  if (result) params.set('result', result)
  if (query?.trim()) params.set('query', query.trim())
  params.set('page', String(page))
  params.set('pageSize', String(pageSize))
  return http.get<AuditPage<LoginAuditItem>>(`${API_PREFIX}/admin/logs/login?${params.toString()}`)
}

/** 操作审计分页列表。 */
export function getOperationAuditLogs(
  query?: string,
  page = 1,
  pageSize = 20,
): Promise<AuditPage<OperationAuditItem>> {
  const params = new URLSearchParams()
  if (query?.trim()) params.set('query', query.trim())
  params.set('page', String(page))
  params.set('pageSize', String(pageSize))
  return http.get<AuditPage<OperationAuditItem>>(`${API_PREFIX}/admin/logs/operations?${params.toString()}`)
}
