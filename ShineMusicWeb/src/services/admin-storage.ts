/**
 * 管理端存储治理服务：审计存储目录与数据库引用的一致性，并清理孤儿文件。
 *
 * 对齐后端 /api/v1/admin/storage 系列端点（仅管理员）：
 * - GET  /admin/storage/audit    返回引用/磁盘/缺失/孤儿统计 + 按曲目分组的文件明细
 * - GET  /admin/storage/file     按存储键读取文件内容（图片/音频预览，blob 拉取）
 * - POST /admin/storage/cleanup  删除孤儿文件与空目录
 */
import type { ApiStorageAudit, ApiStorageCleanup } from '@/types/api.generated'
import { API_PREFIX, http, refreshAccessToken, resolveUrl } from './http'
import { getAccessToken } from './tokens'

/** 执行存储审计（只读）。 */
export function getStorageAudit(): Promise<ApiStorageAudit> {
  return http.get<ApiStorageAudit>(`${API_PREFIX}/admin/storage/audit`)
}

/** 清理孤儿文件与空目录，返回删除数量与释放字节数。 */
export function cleanupStorage(): Promise<ApiStorageCleanup> {
  return http.post<ApiStorageCleanup>(`${API_PREFIX}/admin/storage/cleanup`)
}

/**
 * 按存储键拉取库内文件内容（blob），供治理页图片/音频预览。
 * <img>/<audio> 元素无法携带 Authorization 头，故用 fetch 带鉴权拉取后由调用方转 objectURL；
 * 401 时复用统一单飞刷新重试一次。
 */
export async function fetchStorageFileBlob(storageKey: string): Promise<Blob> {
  const params = new URLSearchParams({ key: storageKey })
  const send = () =>
    fetch(resolveUrl(`${API_PREFIX}/admin/storage/file?${params.toString()}`), {
      headers: buildAuthHeader(),
    })
  let response = await send()
  if (response.status === 401 && (await refreshAccessToken())) {
    response = await send()
  }
  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new Error(body?.error?.message ?? `文件读取失败（HTTP ${response.status}）`)
  }
  return response.blob()
}

/** 构造鉴权头 */
function buildAuthHeader(): Record<string, string> {
  const token = getAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}
