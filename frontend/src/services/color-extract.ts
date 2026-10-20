/**
 * 颜色工具与壁纸取色服务（H5）。
 *
 * 两类能力：
 * 1. 颜色换算工具：hex/hsl/rgba 互转，供自定义主题令牌生成使用（theme.ts）。
 * 2. extractPalette：从图片中提取三色强调色板（主色 + 两个辅色）。
 *    算法：图片缩到 96px 画布逐像素采样，按 24 个色相桶聚合，
 *    以「出现频次 × 饱和度」评分选出主导色相，再统一校准到
 *    「中等亮度 + 高饱和」区间，保证作为 UI 强调色足够醒目、文字可读。
 *    颜色过暗/过亮/低饱和的像素不参与强调色统计，但仍计入整体底色。
 * 仅 H5 端可用（依赖 DOM canvas）；提取失败时回退到默认色板，不阻断流程。
 */

/** 从图片提取出的色板：主色必有，辅色可为 null（算法兜底后一般都有值） */
export interface ExtractedPalette {
  accent: string
  accent2: string | null
  accent3: string | null
}

/** 取色失败时的兜底色板（与默认主题 glass 的强调色一致） */
export const FALLBACK_PALETTE: ExtractedPalette = {
  accent: '#5be07a',
  accent2: '#4a8cff',
  accent3: '#b06bff',
}

/** #RRGGBB → { r, g, b }；非法输入返回 null */
export function hexToRgb(hex: string): { r: number; g: number; b: number } | null {
  const match = /^#?([0-9a-fA-F]{6})$/.exec(hex.trim())
  if (!match) return null
  const value = parseInt(match[1], 16)
  return { r: (value >> 16) & 0xff, g: (value >> 8) & 0xff, b: value & 0xff }
}

/** { r, g, b } → #RRGGBB（小写） */
export function rgbToHex(r: number, g: number, b: number): string {
  const clamp = (v: number) => Math.max(0, Math.min(255, Math.round(v)))
  const to2 = (v: number) => clamp(v).toString(16).padStart(2, '0')
  return `#${to2(r)}${to2(g)}${to2(b)}`
}

/** { r, g, b } → { h(0-360), s(0-1), l(0-1) } */
export function rgbToHsl(r: number, g: number, b: number): { h: number; s: number; l: number } {
  const rn = r / 255
  const gn = g / 255
  const bn = b / 255
  const max = Math.max(rn, gn, bn)
  const min = Math.min(rn, gn, bn)
  const l = (max + min) / 2
  if (max === min) return { h: 0, s: 0, l }
  const d = max - min
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min)
  let h: number
  if (max === rn) h = ((gn - bn) / d + (gn < bn ? 6 : 0)) * 60
  else if (max === gn) h = ((bn - rn) / d + 2) * 60
  else h = ((rn - gn) / d + 4) * 60
  return { h, s, l }
}

/** { h(0-360), s(0-1), l(0-1) } → #RRGGBB */
export function hslToHex(h: number, s: number, l: number): string {
  const hn = ((h % 360) + 360) % 360
  const sn = Math.max(0, Math.min(1, s))
  const ln = Math.max(0, Math.min(1, l))
  const c = (1 - Math.abs(2 * ln - 1)) * sn
  const x = c * (1 - Math.abs(((hn / 60) % 2) - 1))
  const m = ln - c / 2
  let rgb: [number, number, number]
  if (hn < 60) rgb = [c, x, 0]
  else if (hn < 120) rgb = [x, c, 0]
  else if (hn < 180) rgb = [0, c, x]
  else if (hn < 240) rgb = [0, x, c]
  else if (hn < 300) rgb = [x, 0, c]
  else rgb = [c, 0, x]
  return rgbToHex((rgb[0] + m) * 255, (rgb[1] + m) * 255, (rgb[2] + m) * 255)
}

/** hex → { h, s, l }；非法输入返回 null */
export function hexToHsl(hex: string): { h: number; s: number; l: number } | null {
  const rgb = hexToRgb(hex)
  if (!rgb) return null
  return rgbToHsl(rgb.r, rgb.g, rgb.b)
}

/** hex + alpha → rgba() 字符串（供 CSS 令牌使用） */
export function withAlpha(hex: string, alpha: number): string {
  const rgb = hexToRgb(hex)
  if (!rgb) return `rgba(91, 224, 122, ${alpha})`
  return `rgba(${rgb.r}, ${rgb.g}, ${rgb.b}, ${alpha})`
}

/** 色相旋转：h 偏移 delta 度后的 hex 颜色（s/l 保持） */
export function hueRotate(hex: string, delta: number): string {
  const hsl = hexToHsl(hex)
  if (!hsl) return hex
  return hslToHex(hsl.h + delta, hsl.s, hsl.l)
}

/** 两色相间的最短环距（0-180 度） */
function hueDistance(a: number, b: number): number {
  const d = Math.abs(a - b) % 360
  return d > 180 ? 360 - d : d
}

/** 依据相对亮度选择强调色上的文字/图形颜色（深墨或纯白），保证对比度 */
export function accentInk(hex: string): string {
  const rgb = hexToRgb(hex)
  if (!rgb) return '#0d1a10'
  const luminance =
    (0.2126 * rgb.r + 0.7152 * rgb.g + 0.0722 * rgb.b) / 255
  return luminance > 0.55 ? '#10131a' : '#ffffff'
}

/** 校准强调色：把采样到的颜色推到「中等亮度 + 高饱和」区间，保证 UI 上醒目 */
function tuneAccent(h: number, s: number, l: number): string {
  return hslToHex(h, Math.max(0.45, Math.min(0.85, s)), Math.max(0.42, Math.min(0.62, l)))
}

/**
 * 从图片提取三色强调色板。
 * @param imageSrc 图片地址（本地 blob URL 或同源地址；跨域地址会被 canvas 污染拦截）
 * @returns 色板；任何失败都回退 FALLBACK_PALETTE（不抛错）
 */
export async function extractPalette(imageSrc: string): Promise<ExtractedPalette> {
  try {
    const image = await loadImage(imageSrc)
    // 缩到最长边 96px：像素足够统计色相分布，又把采样开销控制在毫秒级。
    const scale = Math.min(1, 96 / Math.max(image.naturalWidth, image.naturalHeight))
    const width = Math.max(8, Math.round(image.naturalWidth * scale))
    const height = Math.max(8, Math.round(image.naturalHeight * scale))
    const canvas = document.createElement('canvas')
    canvas.width = width
    canvas.height = height
    const ctx = canvas.getContext('2d', { willReadFrequently: true })
    if (!ctx) return { ...FALLBACK_PALETTE }
    ctx.drawImage(image, 0, 0, width, height)
    const { data } = ctx.getImageData(0, 0, width, height)

    // 24 个色相桶（每桶 15°）：聚合出现频次与饱和度均值，同时记录整体平均色。
    interface HueBin {
      count: number
      hueSum: number
      satSum: number
      lightSum: number
    }
    const bins: HueBin[] = Array.from({ length: 24 }, () => ({ count: 0, hueSum: 0, satSum: 0, lightSum: 0 }))
    let baseR = 0
    let baseG = 0
    let baseB = 0
    let baseCount = 0

    for (let i = 0; i < data.length; i += 4) {
      const alpha = data[i + 3]
      if (alpha < 128) continue
      const r = data[i]
      const g = data[i + 1]
      const b = data[i + 2]
      baseR += r
      baseG += g
      baseB += b
      baseCount++
      const { h, s, l } = rgbToHsl(r, g, b)
      // 过暗/过亮/灰阶像素无法充当强调色，跳过强调统计。
      if (l < 0.1 || l > 0.95 || s < 0.15) continue
      const bin = Math.floor(h / 15) % 24
      bins[bin].count++
      bins[bin].hueSum += h
      bins[bin].satSum += s
      bins[bin].lightSum += l
    }

    if (baseCount === 0) return { ...FALLBACK_PALETTE }

    // 评分 = 频次 × (0.25 + 平均饱和度)：高饱和的主导色相优先。
    const ranked = bins
      .map((bin, index) => ({
        index,
        count: bin.count,
        hue: bin.count > 0 ? bin.hueSum / bin.count : index * 15 + 7.5,
        sat: bin.count > 0 ? bin.satSum / bin.count : 0,
        light: bin.count > 0 ? bin.lightSum / bin.count : 0.5,
      }))
      .filter((bin) => bin.count >= Math.max(8, (width * height) * 0.008))
      .sort((a, b) => b.count * (0.25 + b.sat) - a.count * (0.25 + a.sat))

    const accent = tuneAccent(
      ranked[0]?.hue ?? 110,
      ranked[0]?.sat ?? 0.6,
      ranked[0]?.light ?? 0.5,
    )

    // 第二/第三色：从剩余桶中找与已选颜色色相距离 > 40° 的最大桶；找不到就色相旋转兜底。
    const accentHsl = hexToHsl(accent)!
    const picked: number[] = [accentHsl.h]
    const pickDistinct = (): string => {
      const candidate = ranked.find(
        (bin) => picked.every((hue) => hueDistance(hue, bin.hue) > 40),
      )
      if (candidate) {
        picked.push(candidate.hue)
        return tuneAccent(candidate.hue, candidate.sat, candidate.light)
      }
      const rotated = hueRotate(accent, 35 + picked.length * 40)
      picked.push(hexToHsl(rotated)!.h)
      return rotated
    }
    const accent2 = pickDistinct()
    const accent3 = pickDistinct()

    return { accent, accent2, accent3 }
  } catch {
    // 图片解码失败/跨域污染/无 canvas：统一回退默认色板。
    return { ...FALLBACK_PALETTE }
  }
}

/** 加载图片（blob URL / 同源地址），解码完成后 resolve */
function loadImage(src: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const image = new Image()
    image.onload = () => resolve(image)
    image.onerror = () => reject(new Error('image decode failed'))
    image.src = src
  })
}
