/**
 * 管理端邀请服务：创建邀请、生成注册码、查询邀请列表、重发、撤销。
 *
 * 对齐后端 /api/v1/admin/invitations 系列端点：
 * - POST   /invitations                      （创建邀请并发送邮件）
 * - POST   /invitations/code                 （生成注册码/CDK）
 * - GET    /invitations                      （分页查询邀请列表）
 * - POST   /invitations/{id}/resend          （重发邀请）
 * - DELETE /invitations/{id}                 （撤销邀请）
 */
import type { ApiPageResult } from '@/types/music'
import { API_PREFIX, http } from './http'

/** 邀请记录 DTO */
export interface Invitation {
  id: string
  email: string | null // CDK/注册码邀请在生成时无邮箱，为 null
  status: string // created / sent / issued / delivery_failed / redeemed / expired / revoked
  createdAt: string
  expiresAt: string
  sentAt: string | null
  redeemedAt: string | null
}

/** 注册码生成响应：明文注册码（仅展示一次）+ 邀请记录 */
export interface InvitationCode {
  code: string
  invitation: Invitation
}

/**
 * 创建邀请并向指定邮箱发送邀请邮件。
 * @param email 受邀邮箱
 * @returns 新建的邀请记录
 */
export async function createInvitation(email: string): Promise<Invitation> {
  return http.post<Invitation>(`${API_PREFIX}/admin/invitations`, { email })
}

/**
 * 生成注册码/CDK：不绑定邮箱，注册时由用户自填邮箱。
 * @returns 明文注册码（仅此一次）+ 邀请记录（状态 issued）
 */
export async function createInvitationCode(): Promise<InvitationCode> {
  return http.post<InvitationCode>(`${API_PREFIX}/admin/invitations/code`)
}

/**
 * 分页查询邀请列表。
 * @param status 可选状态过滤
 * @param page 页码
 * @param pageSize 每页条数
 */
export async function getInvitations(
  status?: string,
  page = 1,
  pageSize = 50,
): Promise<ApiPageResult<Invitation>> {
  const params = new URLSearchParams()
  if (status) params.set('status', status)
  params.set('page', String(page))
  params.set('pageSize', String(pageSize))
  return http.get<ApiPageResult<Invitation>>(`${API_PREFIX}/admin/invitations?${params.toString()}`)
}

/**
 * 重发邀请：换发新令牌并重新发送邮件。
 * @param invitationId 邀请 ID
 * @returns 更新后的邀请记录
 */
export async function resendInvitation(invitationId: string): Promise<Invitation> {
  return http.post<Invitation>(`${API_PREFIX}/admin/invitations/${invitationId}/resend`)
}

/**
 * 撤销邀请。
 * @param invitationId 邀请 ID
 */
export async function revokeInvitation(invitationId: string): Promise<void> {
  return http.delete<void>(`${API_PREFIX}/admin/invitations/${invitationId}`)
}
