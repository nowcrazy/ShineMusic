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
 * 上传前预检只提供可能的匹配线索，是否为重复由服务器在处理实际文件后决定；
 * 命中公共曲库不会阻止用户保存自己的原件。主动申请公开与私人曲库更新由独立审核流程处理。
 */
import { ref } from 'vue'
import type {
  ApiPrecheckRequest,
  ApiPrecheckResponse,
  ApiUserLibraryItem,
  ApiUserUploadResponse,
} from '@/types/music'
import type { ApiUserUploadBatch } from '@/types/api.generated'
import { API_PREFIX, ApiContextChangedError, ApiError, assertApiContextCurrent, assertApiRequestOrigin, captureApiContext, http, refreshAccessToken, resolveUrl, trackApiRequest } from './http'
import { getAccessToken } from './tokens'
import { t } from '@/locale'

// ---- 我的曲库状态（module 级单例，全局共享） ----
// 已引用曲目列表（后端 UserLibraryItemDto 原样，页面侧按需映射）
export const myLibraryItems = ref<ApiUserLibraryItem[]>([])
// 列表是否已加载完成
const myLibraryLoaded = ref(false)
// 是否正在加载中（防重入）
export const myLibraryLoading = ref(false)
let myLibraryGeneration = 0

/**
 * 上传前预检：把本地文件名和可用元信息发给服务端，仅返回可能匹配的曲目供展示。
 *
 * @param files 本地文件指纹数组
 * @returns 每个文件的参考匹配结果；不能据此自动跳过原件上传或建曲库引用
 * @throws 网络或后端错误
 */
export async function precheckUploads(files: ApiPrecheckRequest['files']): Promise<ApiPrecheckResponse> {
  const context = captureApiContext()
  const result = await http.post<ApiPrecheckResponse>(`${API_PREFIX}/me/uploads/precheck`, { files })
  assertApiContextCurrent(context)
  return result
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
  retry = false,
  participatesInPublicReview = false,
): Promise<ApiUserUploadResponse> {
  const context = captureApiContext()
  return new Promise((resolve, reject) => {
    let settled = false
    let unregister: () => void = () => {}
    let task: ReturnType<typeof uni.uploadFile> | undefined
    const finish = (handler: (value: any) => void, value: any) => {
      if (settled) return
      settled = true
      unregister()
      try { assertApiContextCurrent(context) } catch { reject(new ApiContextChangedError()); return }
      handler(value)
    }
    const token = getAccessToken()
    const header: Record<string, string> = {}
    if (token) header.Authorization = `Bearer ${token}`
    const target = resolveUrl(`${API_PREFIX}/me/uploads`)
    assertApiRequestOrigin(context, target)
    task = uni.uploadFile({
      url: target,
      name: 'files',
      files: files.map((file) => ({ name: file.name, uri: file.path })),
      header,
      formData: { participatesInPublicReview: String(participatesInPublicReview) },
      timeout: 120000,
      success: async (res) => {
        try {
          assertApiContextCurrent(context)
          if (res.statusCode === 401 && !retry && await refreshAccessToken()) {
            assertApiContextCurrent(context)
            finish(resolve, await uploadFiles(files, true, participatesInPublicReview))
            return
          }
          let body: unknown
          try {
            body = typeof res.data === 'string' ? JSON.parse(res.data) : res.data
          } catch {
            if (res.statusCode >= 200 && res.statusCode < 300) {
              throw new Error(t('services.uploadSuccessButParseFailed'))
            }
          }
          if (res.statusCode < 200 || res.statusCode >= 300) {
            const detail = (body as { error?: { code?: string; message?: string; traceId?: string } })?.error
            finish(reject, new ApiError(res.statusCode, detail?.code || (res.statusCode === 413 ? 'upload_too_large' : 'internal_error'),
              detail?.message || t('services.uploadFailedWithStatus', { status: res.statusCode }), detail?.traceId))
            return
          }
          const response = body as Partial<ApiUserUploadResponse> | null
          if (!response || !Array.isArray(response.referenced) || !Array.isArray(response.queued) || !Array.isArray(response.rejected)) {
            throw new Error(t('services.uploadSuccessButParseFailed'))
          }
          finish(resolve, body as ApiUserUploadResponse)
        } catch (error) {
          finish(reject, error instanceof Error ? error : new Error(t('services.uploadResponseParseFailed')))
        }
      },
      fail: (err) => {
        finish(reject, /timeout|timed out/i.test(err.errMsg || '')
          ? new Error(t('libraryPage.uploadTimeout'))
          : new ApiError(0, 'network_error', err.errMsg || t('services.networkError')))
      },
    })
    unregister = trackApiRequest(context, () => {
      task?.abort?.()
      finish(reject, new ApiContextChangedError())
    })
    if (settled) unregister()
  })
}

/** 查询本人的上传批次，区分文件已接收与后台处理完成。 */
export function getUploadBatch(batchId: string): Promise<ApiUserUploadBatch> {
  const context = captureApiContext()
  return http.get<ApiUserUploadBatch>(`${API_PREFIX}/me/uploads/${encodeURIComponent(batchId)}`).then((result) => {
    assertApiContextCurrent(context)
    return result
  })
}

/**
 * 批量引用曲库中的曲目。该显式操作不会因上传预检而自动触发。
 *
 * @param trackIds 目标曲目 ID 数组
 * @returns 成功时无返回值（后端 204 NoContent）
 * @throws 网络或后端错误
 */
export async function addToLibrary(trackIds: string[]) {
  const context = captureApiContext()
  const result = await http.post<void>(`${API_PREFIX}/me/library`, { trackIds })
  assertApiContextCurrent(context)
  return result
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
  const generation = myLibraryGeneration
  const context = captureApiContext()
  myLibraryLoading.value = true
  try {
    const result = await http.get<{
      items: ApiUserLibraryItem[]
      page: number
      pageSize: number
      total: number
    }>(`${API_PREFIX}/me/library?pageSize=200`)
    assertApiContextCurrent(context)
    if (generation === myLibraryGeneration) {
      myLibraryItems.value = result.items
      myLibraryLoaded.value = true
    }
  } finally {
    if (generation === myLibraryGeneration) myLibraryLoading.value = false
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
  const context = captureApiContext()
  const generation = myLibraryGeneration
  await http.delete(`${API_PREFIX}/me/library/${trackId}`)
  assertApiContextCurrent(context)
  if (generation === myLibraryGeneration) {
    myLibraryItems.value = myLibraryItems.value.filter((item) => item.trackId !== trackId)
  }
}

/**
 * 清空本地“我的曲库”状态（登出或切换用户时调用）。
 *
 * @returns 无返回值，仅重置本地 ref
 */
export function resetMyLibrary() {
  myLibraryGeneration++
  myLibraryItems.value = []
  myLibraryLoaded.value = false
  myLibraryLoading.value = false
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
