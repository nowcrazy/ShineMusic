/**
 * 用户自定义图片主题服务：对接后端 /api/v1/me/theme 系列端点与远程同步编排。
 *
 * 端点契约：
 * - GET    /me/theme             读取当前用户自定义主题配置（未配置返回全空）
 * - POST   /me/theme/wallpaper   上传/更换壁纸（multipart，字段名 wallpaper，≤8MB，JPEG/PNG/WebP）
 * - GET    /me/theme/wallpaper   读取壁纸二进制（登录态；CSS 背景无法携带请求头，取回后转 objectURL）
 * - PUT    /me/theme             更新主题配置（色板/遮罩/启用开关）
 * - DELETE /me/theme             删除自定义主题（壁纸 + 配置，幂等）
 *
 * 多端同步：后端 enabled 是启用状态的唯一事实源。登录/会话恢复成功后由
 * session 模块调用 syncRemoteTheme()：远端启用 → 拉取配置与壁纸并应用；
 * 远端停用而本地处于 custom → 回退默认预设主题。
 */
import { t } from '@/locale'
import { API_PREFIX, http, refreshAccessToken, resolveUrl } from './http'
import { getAccessToken } from './tokens'
import {
  applyCustomTheme,
  applyTheme,
  currentTheme,
  DEFAULT_THEME,
  MAX_MASK_OPACITY,
  resetCustomTheme,
  type CustomThemeConfig,
} from './theme'

/** 后端下发的自定义主题配置 DTO */
export interface ApiUserTheme {
  accent: string | null
  accent2: string | null
  accent3: string | null
  maskOpacity: number
  enabled: boolean
  wallpaperUrl: string | null
}

/** 读取当前用户自定义主题配置 */
export function fetchMyTheme(): Promise<ApiUserTheme> {
  return http.get<ApiUserTheme>(`${API_PREFIX}/me/theme`)
}

/**
 * 上传/更换壁纸。
 * @param filePath 本地图片临时路径（uni.chooseImage 返回的 tempFilePaths[0]）
 * @returns 上传成功后的完整主题配置
 *
 * 实现说明：
 * - H5 用原生 fetch + FormData（uni.uploadFile 对 blob: 临时路径的兼容性不稳，
 *   且其 fail 回调拿不到状态码）；401 时走与 http 层一致的单飞刷新后重试一次——
 *   常规 JSON 请求由 http 层自动刷新，文件上传这条手动带令牌的路径必须自己补齐，
 *   否则访问令牌过期时上传会静默失败（本地预览正常、服务端没存上）。
 * - 非 H5 仍走 uni.uploadFile（原生文件路径可用）。
 */
export async function uploadWallpaper(filePath: string): Promise<ApiUserTheme> {
  // #ifdef H5
  if (typeof fetch === 'undefined') throw new Error(t('services.uploadNotSupported'))
  const blob = await (await fetch(filePath)).blob()
  const send = () => {
    const form = new FormData()
    form.append('wallpaper', blob, 'wallpaper')
    const headers: Record<string, string> = {}
    const token = getAccessToken()
    if (token) headers.Authorization = `Bearer ${token}`
    return fetch(resolveUrl(`${API_PREFIX}/me/theme/wallpaper`), {
      method: 'POST',
      headers,
      body: form,
    })
  }
  let response = await send()
  if (response.status === 401 && (await refreshAccessToken())) {
    response = await send()
  }
  const body = await response.json().catch(() => null)
  if (response.status < 200 || response.status >= 300) {
    const detail = (body as { error?: { message?: string } } | null)?.error
    throw new Error(detail?.message || t('services.uploadFailedWithStatus', { status: response.status }))
  }
  return body as ApiUserTheme
  // #endif
  // #ifndef H5
  return new Promise((resolve, reject) => {
    const token = getAccessToken()
    const header: Record<string, string> = {}
    if (token) header.Authorization = `Bearer ${token}`
    uni.uploadFile({
      url: resolveUrl(`${API_PREFIX}/me/theme/wallpaper`),
      name: 'wallpaper',
      filePath,
      header,
      success: (res) => {
        try {
          const body = typeof res.data === 'string' ? JSON.parse(res.data) : res.data
          if (res.statusCode < 200 || res.statusCode >= 300) {
            const detail = (body as { error?: { message?: string } })?.error
            reject(new Error(detail?.message || t('services.uploadFailedWithStatus', { status: res.statusCode })))
            return
          }
          resolve(body as ApiUserTheme)
        } catch (error) {
          reject(new Error(error instanceof Error ? error.message : t('services.uploadResponseParseFailed')))
        }
      },
      fail: (err) => reject(new Error(err.errMsg || t('services.uploadFailed'))),
    })
  })
  // #endif
}

/** 更新主题配置（色板/遮罩/启用开关；颜色传 null 表示清除该槽位） */
export function updateThemeConfig(config: CustomThemeConfig): Promise<ApiUserTheme> {
  const mask = Math.max(0, Math.min(MAX_MASK_OPACITY, config.maskOpacity))
  return http.put<ApiUserTheme>(`${API_PREFIX}/me/theme`, {
    accent: config.accent,
    accent2: config.accent2,
    accent3: config.accent3,
    maskOpacity: mask,
    enabled: config.enabled,
  })
}

/** 删除自定义主题（服务端壁纸 + 配置；本地注入样式与缓存一并清理） */
export async function deleteMyTheme(): Promise<void> {
  await http.delete<void>(`${API_PREFIX}/me/theme`)
  resetCustomTheme()
}

/** 把后端 DTO 转为本地主题配置形态 */
function toConfig(remote: ApiUserTheme): CustomThemeConfig {
  const mask = Number.isFinite(remote.maskOpacity)
    ? Math.max(0, Math.min(MAX_MASK_OPACITY, remote.maskOpacity))
    : 0.45
  return {
    accent: remote.accent ?? null,
    accent2: remote.accent2 ?? null,
    accent3: remote.accent3 ?? null,
    maskOpacity: mask,
    enabled: remote.enabled,
  }
}

/**
 * 以当前登录态请求壁纸二进制并转为 objectURL（仅 H5、仅本次会话有效）。
 * 401 时走单飞刷新后重试一次（与 http 层行为对齐）。
 * 每次调用都会产生新的 objectURL，由调用方负责生命周期（传入 applyCustomTheme 会被接管）。
 * 失败（未登录/无壁纸/网络异常）返回 null，不抛错。
 */
export async function fetchWallpaperObjectUrl(): Promise<string | null> {
  // #ifdef H5
  try {
    if (typeof document === 'undefined') return null
    const send = async () => {
      const headers: Record<string, string> = {}
      const token = getAccessToken()
      if (token) headers.Authorization = `Bearer ${token}`
      return fetch(resolveUrl(`${API_PREFIX}/me/theme/wallpaper`), { headers })
    }
    let response = await send()
    if (response.status === 401 && (await refreshAccessToken())) {
      response = await send()
    }
    if (!response.ok) return null
    const blob = await response.blob()
    return URL.createObjectURL(blob)
  } catch {
    return null
  }
  // #endif
  // #ifndef H5
  return null
  // #endif
}

/**
 * 远程同步自定义主题（登录/会话恢复成功后调用；失败静默，不阻断启动）。
 * - 远端启用：拉取配置与壁纸并应用（账号多端同步的核心路径）；
 * - 远端停用而本地处于 custom：回退默认预设主题；
 * - 远端停用且本地非 custom：无动作（本地预设选择优先）。
 */
export async function syncRemoteTheme(): Promise<void> {
  try {
    const remote = await fetchMyTheme()
    if (remote.enabled) {
      const wallpaperUrl = remote.wallpaperUrl ? await fetchWallpaperObjectUrl() : null
      applyCustomTheme(toConfig(remote), wallpaperUrl)
    } else if (currentTheme.value === 'custom') {
      applyTheme(DEFAULT_THEME)
    }
  } catch {
    // 未登录/网络失败：保持本地状态不变
  }
}
