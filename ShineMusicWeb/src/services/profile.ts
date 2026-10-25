/**
 * 个人资料服务：昵称修改、头像上传/删除、密码修改。
 *
 * 对齐后端 /api/v1/me 下的端点：
 * - PATCH  /me          修改展示昵称
 * - POST   /me/avatar   上传/更换头像（multipart，字段名 avatar，≤2MB，JPEG/PNG/WebP）
 * - GET    /me/avatar   读取当前用户头像（鉴权）
 * - DELETE /me/avatar   删除头像
 * - POST   /me/password 修改登录密码（旧密码 + 新密码）
 *
 * 头像走 uni.uploadFile（multipart/form-data）；其余为 JSON 请求经 http 层。
 * 头像 URL 由 /me 响应下发（带 ?v= 版本参数），上传/删除成功后调用方需重新拉取
 * 用户资料（loadCurrentUser）以刷新全局状态（抽屉头像、资料页头像同步更新）。
 */
import { getAccessToken } from './tokens'
import { API_PREFIX, http, refreshAccessToken, resolveUrl } from './http'
import { t } from '@/locale'
import type { ApiMe } from '@/types/music'

/** 修改展示昵称（1-100 字符）。返回更新后的用户资料。 */
export function updateDisplayName(displayName: string): Promise<ApiMe> {
  return http.patch<ApiMe>(`${API_PREFIX}/me`, { displayName })
}

/**
 * 上传/更换头像。
 * @param filePath 本地图片路径（建议先经 utils/image-compress 压缩到 512px）
 * @returns 上传成功响应 { avatarUrl }（带版本参数）
 *
 * 认证：手动带 Authorization，401 时单飞刷新后重试一次（与 http 层行为对齐）——
 * 否则访问令牌过期时上传会静默失败。
 */
export async function uploadAvatar(filePath: string): Promise<{ avatarUrl: string }> {
  // #ifdef H5
  // H5 端走 fetch + FormData：压缩产物是 blob URL，uni.uploadFile 对 blob 路径兼容性不稳
  const blob = await (await fetch(filePath)).blob()
  const send = () => {
    const form = new FormData()
    form.append('avatar', blob, 'avatar.jpg')
    const headers: Record<string, string> = {}
    const token = getAccessToken()
    if (token) headers.Authorization = `Bearer ${token}`
    return fetch(resolveUrl(`${API_PREFIX}/me/avatar`), { method: 'POST', headers, body: form })
  }
  let response = await send()
  if (response.status === 401 && (await refreshAccessToken())) response = await send()
  const body = await response.json().catch(() => null)
  if (response.status < 200 || response.status >= 300) {
    const detail = (body as { error?: { message?: string } } | null)?.error
    throw new Error(detail?.message || t('services.uploadFailedWithStatus', { status: response.status }))
  }
  return body as { avatarUrl: string }
  // #endif

  // #ifndef H5
  const sendOnce = () =>
    new Promise<{ avatarUrl: string }>((resolve, reject) => {
      const token = getAccessToken()
      const header: Record<string, string> = {}
      if (token) header.Authorization = `Bearer ${token}`
      uni.uploadFile({
        url: resolveUrl(`${API_PREFIX}/me/avatar`),
        name: 'avatar',
        filePath,
        header,
        success: (res) => {
          try {
            const body = typeof res.data === 'string' ? JSON.parse(res.data) : res.data
            if (res.statusCode < 200 || res.statusCode >= 300) {
              const detail = (body as { error?: { message?: string } })?.error
              const error = new Error(detail?.message || t('services.uploadFailedWithStatus', { status: res.statusCode }))
              ;(error as { status?: number }).status = res.statusCode
              reject(error)
              return
            }
            resolve(body as { avatarUrl: string })
          } catch (error) {
            reject(new Error(error instanceof Error ? error.message : t('services.uploadResponseParseFailed')))
          }
        },
        fail: (err) => reject(new Error(err.errMsg || t('socialErrors.uploadFailed'))),
      })
    })
  try {
    return await sendOnce()
  } catch (error) {
    const status = (error as { status?: number }).status
    if (status === 401 && (await refreshAccessToken())) return sendOnce()
    throw error
  }
  // #endif
}

/** 删除当前头像（幂等：无头像也返回成功）。 */
export function deleteAvatar(): Promise<void> {
  return http.delete<void>(`${API_PREFIX}/me/avatar`)
}

/**
 * 修改登录密码（需提供当前密码；新密码强度由服务端 Identity 策略校验）。
 * 成功后当前会话保持有效，无需重新登录。
 */
export function changePassword(currentPassword: string, newPassword: string): Promise<void> {
  return http.post<void>(`${API_PREFIX}/me/password`, { currentPassword, newPassword })
}
