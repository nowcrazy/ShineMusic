/**
 * 管理端用户服务：用户列表检索、角色管理、重置密码、禁用/启用。
 *
 * 对齐后端 /api/v1/admin/users（仅管理员）：
 * - GET  /admin/users?query=&page=&pageSize=  用户分页列表
 * - POST /admin/users/{id}/role               修改角色（user/tempadmin/admin，互斥）
 * - POST /admin/users/{id}/reset-password     重置密码（并解除锁定、吊销刷新令牌）
 * - POST /admin/users/{id}/lock               禁用账号
 * - POST /admin/users/{id}/unlock             解除禁用
 */
import { API_PREFIX, http } from './http'

/** 内置角色：user 普通用户 / tempadmin 临时管理员（3 天）/ admin 管理员 */
export type AdminRole = 'user' | 'tempadmin' | 'admin'

export interface AdminUserItem {
  id: string
  email: string | null
  userName: string | null
  displayName: string
  roles: string[]
  /** 临时管理员有效期截止（ISO UTC）；仅 tempadmin 有值 */
  tempAdminUntilUtc: string | null
  createdAtUtc: string
  lockoutEnabled: boolean
  lockoutEnd: string | null
  accessFailedCount: number
  isLockedOut: boolean
}

export interface AdminUserList {
  items: AdminUserItem[]
  page: number
  pageSize: number
  total: number
}

/** 用户分页列表（query 模糊匹配邮箱/用户名/昵称）。 */
export function getAdminUsers(query?: string, page = 1, pageSize = 20): Promise<AdminUserList> {
  const params = new URLSearchParams()
  if (query?.trim()) params.set('query', query.trim())
  params.set('page', String(page))
  params.set('pageSize', String(pageSize))
  return http.get<AdminUserList>(`${API_PREFIX}/admin/users?${params.toString()}`)
}

/** 修改用户角色（互斥替换）；tempadmin 自修改起 3 天内视同管理员，到期自动降级为普通用户。 */
export function setUserRole(userId: string, role: AdminRole): Promise<void> {
  return http.post<void>(`${API_PREFIX}/admin/users/${userId}/role`, { role })
}

/** 重置用户密码（同时解除锁定并吊销全部刷新令牌，强制重新登录）。 */
export function resetUserPassword(userId: string, newPassword: string): Promise<void> {
  return http.post<void>(`${API_PREFIX}/admin/users/${userId}/reset-password`, { newPassword })
}

/** 禁用账号（长期锁定 + 吊销刷新令牌）。 */
export function lockUser(userId: string): Promise<void> {
  return http.post<void>(`${API_PREFIX}/admin/users/${userId}/lock`)
}

/** 解除禁用/锁定。 */
export function unlockUser(userId: string): Promise<void> {
  return http.post<void>(`${API_PREFIX}/admin/users/${userId}/unlock`)
}
