/**
 * 认证令牌持久化模块：内存缓存 + 平台本地存储双写。
 *
 * 本地存储键（shine.auth.* 命名空间，避免与其他键冲突）：
 * - shine.auth.accessToken ：短期访问令牌（Bearer，随业务请求发送）
 * - shine.auth.refreshToken：长期刷新令牌（仅 /auth/refresh、/auth/logout 使用）
 *
 * 平台存储映射（uni.getStorageSync / setStorageSync 统一封装，各端均为同步 API）：
 * - H5：底层为 window.localStorage；
 * - App：底层为原生持久化存储（plus.storage / 系统持久化）；
 * - 小程序：底层为微信等宿主的 Storage。
 * 内存副本用于收敛读写入口，供 http 鉴权头注入逻辑同步取用。
 * 安全约束：密码与明文令牌不得写入日志或发送到非后端域名。
 */
const ACCESS_TOKEN_KEY = 'shine.auth.accessToken'
const REFRESH_TOKEN_KEY = 'shine.auth.refreshToken'

// 进程内令牌缓存；'' 表示当前无令牌
let accessToken = ''
let refreshToken = ''

/**
 * 从平台本地存储恢复令牌到内存（App 启动时由 restoreSession 调用一次）。
 * 存储中不存在对应键时 uni.getStorageSync 返回 ''，此处以 ?? 兜底为空串。
 */
export function restoreTokens() {
  accessToken = uni.getStorageSync(ACCESS_TOKEN_KEY) ?? ''
  refreshToken = uni.getStorageSync(REFRESH_TOKEN_KEY) ?? ''
}

/**
 * 读取内存中的访问令牌。
 * @returns access token；未登录/未恢复时为 ''
 */
export function getAccessToken() {
  return accessToken
}

/**
 * 读取内存中的刷新令牌。
 * @returns refresh token；不存在时为 ''，http 层据此判断是否具备 401 刷新前提
 */
export function getRefreshToken() {
  return refreshToken
}

/**
 * 判断本地是否存在任一凭证（不校验是否过期，过期由 401 刷新流程处理）。
 * @returns 存在 access 或 refresh 令牌即返回 true
 */
export function hasSession() {
  return Boolean(accessToken || refreshToken)
}

/**
 * 保存新令牌对：同时更新内存与平台本地存储（登录成功、刷新成功时调用）。
 * @param access 新的 access token
 * @param refresh 新的 refresh token（刷新轮换后可能与旧值不同）
 */
export function saveTokens(access: string, refresh: string) {
  accessToken = access
  refreshToken = refresh
  uni.setStorageSync(ACCESS_TOKEN_KEY, access)
  uni.setStorageSync(REFRESH_TOKEN_KEY, refresh)
}

/**
 * 清除全部令牌：重置内存并移除平台存储键。
 * 副作用：登出、刷新失败、会话恢复失败时调用，调用方需同步重置用户状态。
 */
export function clearTokens() {
  accessToken = ''
  refreshToken = ''
  uni.removeStorageSync(ACCESS_TOKEN_KEY)
  uni.removeStorageSync(REFRESH_TOKEN_KEY)
}
