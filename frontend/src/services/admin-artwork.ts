/**
 * 管理端封面图上传服务：把本地图片上传到后端并登记一条 Artwork 记录。
 *
 * 对齐后端 /api/v1/admin/artwork（POST，multipart/form-data，仅管理员）：
 * - 请求字段名固定为 "file"；
 * - 成功响应 { id, artworkUrl }，id 用于后续 PATCH 曲目/专辑/歌单的 artworkId。
 */
import { API_PREFIX, resolveUrl } from './http'
import { getAccessToken } from './tokens'

/** 上传封面后的返回：新封面 Id 与其读取地址（相对路径，显示时需经 resolveUrl 解析）。 */
export interface UploadArtworkResult {
  id: string // 新生成的 Artwork 记录 Id
  artworkUrl: string // 封面读取地址（/api/v1/artwork/{id}）
}

/**
 * 上传封面图片（支持 H5 / App / 小程序：filePath 为本地临时路径或 File 对象）。
 * @param filePath 本地图片路径（H5 下可传 File/Blob，App 下为 chooseImage 返回的 tempFilePath）
 * @returns 新封面的 Id 与读取地址
 * @throws 任何非 2xx 或网络失败时抛出带可读消息的 Error
 */
export function uploadArtwork(filePath: string | Blob): Promise<UploadArtworkResult> {
  return new Promise((resolve, reject) => {
    const url = resolveUrl(`${API_PREFIX}/admin/artwork`)
    const token = getAccessToken()
    uni.uploadFile({
      url,
      filePath: filePath as string,
      name: 'file',
      header: token ? { Authorization: `Bearer ${token}` } : {},
      success: (res) => {
        if (res.statusCode < 200 || res.statusCode >= 300) {
          let message = `上传失败（${res.statusCode}）`
          try {
            const body = JSON.parse(res.data)
            if (body?.error?.message) message = body.error.message
          } catch {
            // 解析失败则沿用状态码提示
          }
          reject(new Error(message))
          return
        }
        try {
          resolve(JSON.parse(res.data) as UploadArtworkResult)
        } catch {
          reject(new Error('上传成功，但响应解析失败'))
        }
      },
      fail: (err) => reject(new Error(err.errMsg || '上传失败')),
    })
  })
}
