/**
 * 密码强度评分工具（0-4 级）：前端实时强度条 + 修改密码门槛判定。
 *
 * 评分口径与后端 `ShineMusic.Api.Services.PasswordStrength` 完全一致，必须同步修改：
 *   - 长度 < 8：即便字符种类多也视为不安全，最多 1 级；
 *   - 长度 8~11 加 1 分，长度 >=12 加 2 分；
 *   - 字符种类（小写/大写/数字/特殊）每多一种额外加分，封顶 4 级。
 */

/** 最高强度等级，与后端 PasswordStrength.MaxLevel 一致。 */
export const PASSWORD_MAX_LEVEL = 4

/** 每个强度等级在强度条中的百分比宽度（0~100）。 */
const LEVEL_FILL: Record<number, number> = { 0: 8, 1: 30, 2: 55, 3: 78, 4: 100 }

/**
 * 计算密码强度等级（0-4，越大越强）。
 */
export function estimatePasswordStrength(password: string): number {
  const pwd = password ?? ''
  let hasLower = false
  let hasUpper = false
  let hasDigit = false
  let hasSpecial = false
  for (const ch of pwd) {
    if (ch >= 'a' && ch <= 'z') hasLower = true
    else if (ch >= 'A' && ch <= 'Z') hasUpper = true
    else if (ch >= '0' && ch <= '9') hasDigit = true
    else hasSpecial = true
  }
  const classes =
    (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSpecial ? 1 : 0)

  if (pwd.length < 8) return classes >= 3 ? 1 : 0

  const score = (pwd.length >= 12 ? 2 : 1) + (classes - 1)
  return Math.min(PASSWORD_MAX_LEVEL, Math.max(0, score))
}

/** 强度等级对应的强度条填充宽度（百分比），供进度条 style.width 使用。 */
export function passwordStrengthFill(password: string): number {
  return LEVEL_FILL[estimatePasswordStrength(password)] ?? 0
}