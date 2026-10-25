/**
 * 上传前图片压缩：把长边缩到指定像素并重编码，显著减小上传体积。
 *
 * 背景：头像服务端上限 2MB，而手机原图常见 3-8MB；公网（Tailscale Funnel）
 * 上行带宽有限时，几 MB 的图能传几分钟，期间极易被客户端中断
 * （后端表现为 FormFile.CopyToAsync 的 TaskCanceledException）。
 * 头像展示区域很小，512px 足够清晰，压缩后通常只有几十 KB。
 *
 * 实现：App 走 uni.compressImage（原生压缩）；H5 走 canvas 缩放后转 blob URL。
 * 任何失败都回退原图，由服务端大小校验兜底。
 */

export interface CompressOptions {
  /** 输出最长边像素（默认 512） */
  maxEdge?: number
  /** 编码质量 0-100（默认 80） */
  quality?: number
}

/**
 * 压缩图片用于上传。
 * @param src 本地图片路径（uni.chooseImage 返回的 tempFilePath）
 * @returns 压缩后的临时路径（失败时为原路径）
 */
export async function compressForUpload(src: string, options: CompressOptions = {}): Promise<string> {
  const maxEdge = options.maxEdge ?? 512
  const quality = options.quality ?? 80
  try {
    // #ifdef APP-PLUS
    const info = await new Promise<{ width: number; height: number }>((resolve, reject) => {
      uni.getImageInfo({
        src,
        success: (res) => resolve({ width: res.width, height: res.height }),
        fail: reject,
      })
    })
    if (!info.width || !info.height) return src
    const appScale = Math.min(1, maxEdge / Math.max(info.width, info.height))
    const compressed = await new Promise<{ tempFilePath: string }>((resolve, reject) => {
      // 类型声明里 width/height 为字符串（底层按像素解析），这里显式转字符串
      uni.compressImage({
        src,
        quality,
        width: String(Math.max(1, Math.round(info.width * appScale))),
        height: String(Math.max(1, Math.round(info.height * appScale))),
        success: (res) => resolve(res),
        fail: reject,
      })
    })
    return compressed.tempFilePath || src
    // #endif

    // #ifdef H5
    const image = await new Promise<HTMLImageElement>((resolve, reject) => {
      const element = new Image()
      element.onload = () => resolve(element)
      element.onerror = reject
      element.src = src
    })
    const scale = Math.min(1, maxEdge / Math.max(image.naturalWidth, image.naturalHeight))
    const width = Math.max(1, Math.round(image.naturalWidth * scale))
    const height = Math.max(1, Math.round(image.naturalHeight * scale))
    const canvas = document.createElement('canvas')
    canvas.width = width
    canvas.height = height
    const ctx = canvas.getContext('2d') as CanvasRenderingContext2D
    ctx.imageSmoothingQuality = 'high'
    ctx.drawImage(image, 0, 0, width, height)
    const blob = await new Promise<Blob | null>((resolve) =>
      canvas.toBlob(resolve, 'image/jpeg', quality / 100),
    )
    return blob ? URL.createObjectURL(blob as Blob) : src
    // #endif
  } catch {
    // 压缩失败不阻断：按原图上传，超限时由服务端返回明确错误
  }
  return src
}
