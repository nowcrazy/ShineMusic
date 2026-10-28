/**
 * 通用格式化工具函数：时长、日期、文件大小。
 *
 * 纯函数，无副作用，可在任意端（H5/小程序/App）使用。
 * 日期统一按北京时间（Asia/Shanghai）展示，不随客户端系统时区变化。
 */

/** 展示时区：固定北京时间。 */
const DISPLAY_TIME_ZONE = 'Asia/Shanghai'

/**
 * 解析后端时间字符串。
 *
 * 后端统一以 UTC 存储，个别字段可能缺少时区后缀；缺少时按 UTC 补齐后再解析，
 * 避免被浏览器当作本地时间而整体偏移 8 小时。
 */
function parseUtcDate(iso: string): Date | null {
  const hasZone = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(iso)
  const date = new Date(hasZone ? iso : `${iso}Z`)
  return Number.isNaN(date.getTime()) ? null : date
}

/** 按北京时间拆出各时间字段；hourCycle 用 h23，避免 zh-CN 把 0 点输出成 24。 */
function partsInBeijing(date: Date, options: Intl.DateTimeFormatOptions): Record<string, string> {
  return Object.fromEntries(
    new Intl.DateTimeFormat('zh-CN', {
      ...options,
      timeZone: DISPLAY_TIME_ZONE,
      hourCycle: 'h23',
    })
      .formatToParts(date)
      .map((part) => [part.type, part.value]),
  )
}

/**
 * 将秒数格式化为 m:ss 时长字符串。
 *
 * @param seconds 秒数
 * @returns 形如 "3:05" 的字符串；非法值返回 "0:00"
 */
export function formatTime(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds <= 0) return '0:00'
  const minutes = Math.floor(seconds / 60)
  const remainder = Math.floor(seconds % 60).toString().padStart(2, '0')
  return `${minutes}:${remainder}`
}

/**
 * 将 ISO 日期字符串格式化为北京时间 YYYY-MM-DD。
 *
 * @param iso ISO 8601 日期字符串
 * @returns 形如 "2026-10-16" 的字符串；解析失败返回空串
 */
export function formatDate(iso: string): string {
  const date = parseUtcDate(iso)
  if (!date) return ''
  const parts = partsInBeijing(date, { year: 'numeric', month: '2-digit', day: '2-digit' })
  return `${parts.year}-${parts.month}-${parts.day}`
}

/**
 * 将 ISO 日期时间字符串格式化为北京时间 YYYY-MM-DD HH:mm:ss（日志/审计列表用）。
 *
 * @param iso ISO 8601 日期时间字符串
 * @returns 形如 "2026-10-25 16:30:05" 的字符串；解析失败返回空串
 */
export function formatDateTime(iso: string): string {
  const date = parseUtcDate(iso)
  if (!date) return ''
  const parts = partsInBeijing(date, {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })
  return `${parts.year}-${parts.month}-${parts.day} ${parts.hour}:${parts.minute}:${parts.second}`
}

/**
 * 将字节数格式化为人类可读的文件大小（KB/MB）。
 *
 * @param bytes 字节数，可为 null/undefined
 * @returns 小于 1MB 显示 KB（四舍五入），否则显示 MB（保留 1 位小数）；空值返回空串
 */
export function formatBytes(bytes: number | null | undefined): string {
  if (!bytes) return ''
  if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${Math.round(bytes / 1024)} KB`
}
