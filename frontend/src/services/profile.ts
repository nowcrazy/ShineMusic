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
import { API_PREFIX, http, resolveUrl } from './http'
import type { ApiMe } from '@/types/music'

/** 修改展示昵称（1-100 字符）。返回更新后的用户资料。 */
export function updateDisplayName(displayName: string): Promise<ApiMe> {
  return http.patch<ApiMe>(`${API_PREFIX}/me`, { displayName })
}

/**
 * 上传/更换头像。
 * @param filePath 本地图片临时路径（uni.chooseImage 返回的 tempFilePaths[0]）
 * @returns 上传成功响应 { avatarUrl }（带版本参数）
 */
export function uploadAvatar(filePath: string): Promise<{ avatarUrl: string }> {
  return new Promise((resolve, reject) => {
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
            reject(new Error(detail?.message || `上传失败：${res.statusCode}`))
            return
          }
          resolve(body as { avatarUrl: string })
        } catch (error) {
          reject(new Error(error instanceof Error ? error.message : '上传响应解析失败'))
        }
      },
      fail: (err) => reject(new Error(err.errMsg || '上传失败')),
    })
  })
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
