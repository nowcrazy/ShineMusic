/**
 * 通用格式化工具函数：时长、日期、文件大小。
 *
 * 纯函数，无副作用，可在任意端（H5/小程序/App）使用。
 */

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
 * 将 ISO 日期字符串格式化为 YYYY-MM-DD。
 *
 * @param iso ISO 8601 日期字符串
 * @returns 形如 "2026-10-16" 的字符串；解析失败返回空串
 */
export function formatDate(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(
    date.getDate(),
  ).padStart(2, '0')}`
}

/**
 * 将 ISO 日期时间字符串格式化为本地 YYYY-MM-DD HH:mm:ss（日志/审计列表用）。
 *
 * @param iso ISO 8601 日期时间字符串
 * @returns 形如 "2026-10-25 16:30:05" 的字符串；解析失败返回空串
 */
export function formatDateTime(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
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
