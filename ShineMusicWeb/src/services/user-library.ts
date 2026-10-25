/**
 * 用户上传与“我的曲库”服务（扫码导入场景）。
 *
 * 对齐后端 /api/v1/me 下的端点：
 * - 我的曲库： GET    /me/library           （列出当前用户引用的曲目，含私有与公开）
 *             POST   /me/library           （批量引用公共曲目/本人私有曲目）
 *             DELETE /me/library/{trackId} （移除引用）
 * - 上传：     POST   /me/uploads/precheck  （上传前批量预检，命中已有曲目可直接引用）
 *             POST   /me/uploads           （上传本地音频 + 可选旁挂 .lrc 歌词）
 *
 * 与 services/library.ts（收藏 /me/favorites、歌单 /me/playlists）相互独立：
 * 本模块对应“引用曲目”（UserLibraryItem），曲目本体全局唯一、不重复存储。
 * 上传响应用 Referenced / Queued / Rejected 三分桶：
 * - Referenced：内容哈希命中已有曲库音频，秒级建引用（不重复存储、不入队）；
 * - Queued：新歌进入处理队列，转码 + 歌词自动匹配完成后私有入库（仅上传者可听）；
 * - Rejected：他人私有曲目等无法引用的文件。
 */
import { ref } from 'vue'
import type {
  ApiPrecheckRequest,
  ApiPrecheckResponse,
  ApiUserLibraryItem,
  ApiUserUploadResponse,
} from '@/types/music'
import { API_PREFIX, http, resolveUrl } from './http'
import { getAccessToken } from './tokens'
import { t } from '@/locale'

// ---- 我的曲库状态（module 级单例，全局共享） ----
// 已引用曲目列表（后端 UserLibraryItemDto 原样，页面侧按需映射）
export const myLibraryItems = ref<ApiUserLibraryItem[]>([])
// 列表是否已加载完成
const myLibraryLoaded = ref(false)
// 是否正在加载中（防重入）
export const myLibraryLoading = ref(false)

/**
 * 上传前预检：把本地文件指纹批量发给服务端，返回每个文件命中的已有曲目。
 *
 * @param files 本地文件指纹数组
 * @returns 每个文件的预检结果（match 为命中曲目，未命中为 null）
 * @throws 网络或后端错误
 */
export async function precheckUploads(files: ApiPrecheckRequest['files']): Promise<ApiPrecheckResponse> {
  return http.post<ApiPrecheckResponse>(`${API_PREFIX}/me/uploads/precheck`, { files })
}

/**
 * 上传本地音频（可携带同名旁挂 .lrc 歌词）。
 *
 * 使用 uni.uploadFile 走 multipart/form-data；鉴权头由模块注入。
 * 文件对象沿用 uni.chooseFile 的 tempFile：{ path, name, size, type }，
 * .lrc 旁挂歌词应与音频同名（文件主干一致），后端按主干配对。
 *
 * @param files 待上传文件数组（音频 + 可选 .lrc/.txt 旁挂歌词）
 * @returns 上传响应（Referenced / Queued / Rejected）
 * @throws 网络或后端错误（非 2xx 时抛出，消息取自后端 error.message）
 */
export function uploadFiles(
  files: Array<{ path: string; name: string }>,
): Promise<ApiUserUploadResponse> {
  return new Promise((resolve, reject) => {
    const token = getAccessToken()
    const header: Record<string, string> = {}
    if (token) header.Authorization = `Bearer ${token}`
    uni.uploadFile({
      url: resolveUrl(`${API_PREFIX}/me/uploads`),
      name: 'files',
      files: files.map((file) => ({ name: file.name, uri: file.path })),
      header,
      success: (res) => {
        try {
          const body = typeof res.data === 'string' ? JSON.parse(res.data) : res.data
          if (res.statusCode < 200 || res.statusCode >= 300) {
            const detail = (body as { error?: { message?: string } })?.error
            reject(new Error(detail?.message || t('catalogErrors.uploadFailedWithStatus', { status: res.statusCode })))
            return
          }
          resolve(body as ApiUserUploadResponse)
        } catch (error) {
          reject(new Error(error instanceof Error ? error.message : t('catalogErrors.uploadResponseParseFailed')))
        }
      },
      fail: (err) => reject(new Error(err.errMsg || t('catalogErrors.uploadFailed'))),
    })
  })
}

/**
 * 批量加入曲库：预检命中公共曲库（或本人私有曲目）的歌曲可直接引用，无需上传文件。
 *
 * @param trackIds 目标曲目 ID 数组
 * @returns 成功时无返回值（后端 204 NoContent）
 * @throws 网络或后端错误
 */
export async function addToLibrary(trackIds: string[]) {
  return http.post<void>(`${API_PREFIX}/me/library`, { trackIds })
}

/**
 * 拉取当前用户的“我的曲库”列表。
 *
 * 默认幂等：已加载或加载中且非 force 时直接返回，避免重复请求。
 *
 * @param force 是否强制重新加载（忽略已加载状态）
 * @returns 无返回值，结果写入 myLibraryItems
 * @throws 网络或后端错误
 */
export async function loadMyLibrary(force = false) {
  if ((myLibraryLoaded.value || myLibraryLoading.value) && !force) return
  myLibraryLoading.value = true
  try {
    const result = await http.get<{
      items: ApiUserLibraryItem[]
      page: number
      pageSize: number
      total: number
    }>(`${API_PREFIX}/me/library?pageSize=200`)
    myLibraryItems.value = result.items
    myLibraryLoaded.value = true
  } finally {
    myLibraryLoading.value = false
  }
}

/**
 * 从我的曲库移除引用（不影响曲目本体与其他用户的引用）。
 *
 * @param trackId 目标曲目 ID
 * @returns 成功时无返回值（后端 204 NoContent）
 * @throws 网络或后端错误
 */
export async function removeFromLibrary(trackId: string) {
  await http.delete(`${API_PREFIX}/me/library/${trackId}`)
  myLibraryItems.value = myLibraryItems.value.filter((item) => item.trackId !== trackId)
}

/**
 * 清空本地“我的曲库”状态（登出或切换用户时调用）。
 *
 * @returns 无返回值，仅重置本地 ref
 */
export function resetMyLibrary() {
  myLibraryItems.value = []
  myLibraryLoaded.value = false
}

/**
 * 用户上传与我的曲库相关组合式 API，供 setup 中解构使用。
 *
 * @returns 我的曲库状态与方法的集合
 */
export function useMyLibrary() {
  return {
    myLibraryItems,
    myLibraryLoaded,
    myLibraryLoading,
    loadMyLibrary,
    addToLibrary,
    removeFromLibrary,
    resetMyLibrary,
    precheckUploads,
    uploadFiles,
  }
}
