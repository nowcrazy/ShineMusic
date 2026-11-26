/**
 * 会话（认证）状态模块：邀请验证、邀请注册、登录、登出、令牌刷新与会话恢复。
 *
 * 对接后端 /api/v1 契约：
 * - POST /auth/invitations/validate 校验邀请令牌（匿名）
 * - POST /auth/register              凭邀请令牌注册（匿名，不直接发凭证）
 * - POST /auth/login                 邮箱 + 密码登录，返回双令牌（匿名）
 * - POST /auth/refresh               用 refresh token 换新 access token（匿名）
 * - POST /auth/logout                服务端注销 refresh token（匿名，令牌随请求体上送）
 * - GET  /me                         获取当前登录用户资料（鉴权）
 *
 * 状态以模块级 reactive 单例持有，通过 computed 导出供组件订阅；
 * 模块加载时即向 http 层注册 401 刷新处理器，刷新采用单飞 Promise，
 * 多个并发请求遇到 401 时只触发一次 /auth/refresh。
 */
import type { ApiAuthTokens, ApiInvitationValidation, ApiMe } from '@/types/music'
import { t } from '@/locale'
import { ApiContextChangedError, ApiError, assertApiContextCurrent, captureApiContext, http, invalidateApiSession, setApiUserId, setAuthenticationFailureHandler, setAuthenticationUnavailableHandler, setAuthRequestGuard, setRefreshHandler, subscribeApiContextChange } from './http'
import { syncRemoteTheme } from './user-theme'
import { clearTokens, getRefreshToken, hasSession, restoreTokens, saveTokens } from './tokens'
import { resetUserApiState } from './user-state'
import { isAuthenticated, sessionState as state } from './session-state'
export { currentUser, isAuthenticated, isAdmin, sessionStatus } from './session-state'

let sessionVersion = 0
let tokensRestored = false
let restoring: { version: number; promise: Promise<void> } | null = null
let retryTimer: ReturnType<typeof setTimeout> | null = null
let retryDelay = 1000
let recoveryStarted = false

setAuthRequestGuard(() => state.status !== 'unavailable' && state.status !== 'unknown')
setAuthenticationFailureHandler(() => endSession())
setAuthenticationUnavailableHandler(() => { if (hasSession()) suspendSession() })

function stopRecoveryTimer() {
  if (retryTimer !== null) clearTimeout(retryTimer)
  retryTimer = null
}

/** Synchronous, complete local logout; network completion must never clear a later login. */
export function endSession(): void {
  sessionVersion++
  tokensRestored = true
  stopRecoveryTimer()
  retryDelay = 1000
  clearTokens()
  state.user = null
  state.status = 'anonymous'
  resetUserApiState()
  invalidateApiSession()
}

function scheduleRecovery() {
  if (retryTimer !== null || state.status !== 'unavailable' || !hasSession()) return
  retryTimer = setTimeout(() => {
    retryTimer = null
    void resumeSession()
  }, retryDelay)
  retryDelay = Math.min(retryDelay * 2, 30000)
}

function suspendSession() {
  // Expire pending private work without deleting the recoverable credentials.
  sessionVersion++
  state.user = null
  state.status = 'unavailable'
  resetUserApiState()
  invalidateApiSession()
  scheduleRecovery()
}

/** Retry after connectivity returns, foregrounding, or the bounded backoff timer. */
export async function resumeSession(): Promise<void> {
  if (state.status !== 'unavailable') return
  stopRecoveryTimer()
  await restoreSession()
}

export function startSessionRecovery(): void {
  if (recoveryStarted) return
  recoveryStarted = true
  uni.onNetworkStatusChange(({ isConnected }) => {
    if (isConnected) void resumeSession()
  })
  // #ifdef H5
  if (typeof window !== 'undefined') window.addEventListener('online', () => { void resumeSession() })
  // #endif
}

// 401 时自动用 refresh token 续期，避免并发刷新。
let refreshing: { revision: number; promise: Promise<boolean> } | null = null
setRefreshHandler(() => {
  const context = captureApiContext()
  if (refreshing?.revision === context.revision) return refreshing.promise
  const refreshToken = getRefreshToken()
  if (!refreshToken) return Promise.resolve(false)
  let promise!: Promise<boolean>
  promise = (async () => {
    try {
      const tokens = await http.post<ApiAuthTokens>(
        '/api/v1/auth/refresh',
        { refreshToken },
        false,
      )
      assertApiContextCurrent(context)
      saveTokens(tokens.accessToken, tokens.refreshToken, context.origin)
      return true
    } catch (error) {
      if (assertCurrent(context)) {
        if (error instanceof ApiError && error.status === 401) {
          endSession()
          return false
        }
        suspendSession()
      }
      // Preserve the refresh failure instead of disguising an outage as the original 401.
      throw error
    } finally {
      if (refreshing?.promise === promise) refreshing = null
    }
  })()
  refreshing = { revision: context.revision, promise }
  return promise
})

function assertCurrent(context: ReturnType<typeof captureApiContext>): boolean {
  try { assertApiContextCurrent(context); return true } catch { return false }
}

/**
 * 校验邀请令牌是否有效。
 * - 请求：POST /api/v1/auth/invitations/validate（匿名）
 * @param invitationToken 邀请链接/邮件中的一次性邀请令牌
 * @returns 邀请绑定的邮箱与到期时间；无效时后端返回 4xx，由调用方捕获提示
 */
export async function validateInvitation(invitationToken: string) {
  return http.post<ApiInvitationValidation>(
    '/api/v1/auth/invitations/validate',
    { invitationToken },
    false,
  )
}

/** 注册成功后的用户摘要（注册接口不直接返回令牌，需再调 login） */
export interface RegisteredUser {
  id: string // 新用户 ID
  email: string // 登录邮箱（沿用邀请绑定邮箱）
  displayName: string // 展示昵称
  role: string // 初始角色码
}

/**
 * 凭邀请令牌完成注册。
 * - 请求：POST /api/v1/auth/register（匿名）
 * 注意：注册成功不直接下发凭证，注册页需用返回的邮箱与用户输入的密码再调用 login。
 * @param input.invitationToken 邀请令牌
 * @param input.password 登录密码（明文仅在 HTTPS body 中传输，不落日志）
 * @param input.displayName 展示昵称
 * @returns 新用户摘要
 */
export async function registerWithInvitation(input: {
  invitationToken: string
  password: string
  displayName: string
  email?: string // 仅 CDK/注册码邀请时必填（后端按 issued 状态要求自填邮箱）
}) {
  // 注册成功不直接返回凭证，注册页用返回的邮箱调用 login 完成登录。
  return http.post<RegisteredUser>('/api/v1/auth/register', input, false)
}

/**
 * 邮箱密码登录：换取双令牌、持久化并拉取当前用户资料。
 * - 请求：POST /api/v1/auth/login（匿名），随后 GET /api/v1/me（鉴权）
 * @param email 登录邮箱（提交前 trim）
 * @param password 登录密码
 * @returns 成功后状态切换为 authenticated；失败抛出 ApiError（由页面提示）
 */
export async function login(email: string, password: string) {
  endSession()
  const version = sessionVersion
  const context = captureApiContext()
  const tokens = await http.post<ApiAuthTokens>(
    '/api/v1/auth/login',
    { email: email.trim(), password },
    false,
  )
  assertApiContextCurrent(context)
  saveTokens(tokens.accessToken, tokens.refreshToken, context.origin)
  try {
    await loadCurrentUser()
    if (version !== sessionVersion) throw new ApiContextChangedError()
  } catch (error) {
    if (assertCurrent(context) && hasSession()) {
      if (error instanceof ApiError && error.status === 401) endSession()
      else suspendSession()
    }
    throw error
  }
  // 登录成功后同步自定义图片主题（远端启用则应用；失败静默）。
  void syncRemoteTheme()
}

/**
 * 拉取并缓存当前用户资料。
 * - 请求：GET /api/v1/me（鉴权，默认附带 Authorization 头）
 * @returns 当前用户 DTO；副作用是写入 state.user 并把状态置为 authenticated
 * @throws {ApiError} 令牌无效时抛出 401（上层恢复流程会先尝试刷新一次）
 */
export async function loadCurrentUser() {
  const context = captureApiContext()
  const me = await http.get<ApiMe>('/api/v1/me')
  assertApiContextCurrent(context)
  setApiUserId(me.id)
  state.user = me
  state.status = 'authenticated'
  return me
}

/**
 * 登出：通知服务端注销 refresh token，并不论接口成败都清空本地会话。
 * - 请求：POST /api/v1/auth/logout（匿名传令牌，保证过期令牌也能完成本地清理）
 * 副作用：清除本地双令牌、用户资料，状态置为 anonymous。
 */
export async function logout() {
  const refreshToken = getRefreshToken()
  endSession()
  // Revocation is best effort; neither navigation nor a later login waits for the network.
  if (refreshToken) void http.post('/api/v1/auth/logout', { refreshToken }, false).catch(() => undefined)
}

/**
 * App 启动时恢复会话：先从本地存储载入令牌，有凭证则尝试拉取用户资料。
 * 拉取过程中若 access token 过期，http 层会自动用 refresh token 刷新并重试一次；
 * 明确认证失效才清凭据；临时故障保留凭据、暂停权限并安排重试。
 * 无抛错：所有异常在函数内消化，保证应用启动不被阻断。
 */
export function restoreSession(): Promise<void> {
  if (restoring?.version === sessionVersion) return restoring.promise
  if (!tokensRestored) {
    restoreTokens(captureApiContext().origin)
    tokensRestored = true
  }
  if (!hasSession()) {
    state.status = 'anonymous'
    return Promise.resolve()
  }
  const version = sessionVersion
  let promise!: Promise<void>
  promise = (async () => {
    try {
      await loadCurrentUser()
      // loadCurrentUser intentionally advances the API identity; use the session version here.
      if (version !== sessionVersion) return
      stopRecoveryTimer()
      retryDelay = 1000
      void syncRemoteTheme()
    } catch (error) {
      if (version !== sessionVersion) return
      if (error instanceof ApiError && error.status === 401) endSession()
      else suspendSession()
    } finally {
      if (restoring?.promise === promise) restoring = null
    }
  })()
  restoring = { version, promise }
  return promise
}

// Switching server invalidates the old session synchronously, before any new request is started.
subscribeApiContextChange(({ reason }) => {
  if (reason !== 'origin') return
  endSession()
})

/**
 * 登录守卫：需要登录的操作前调用。
 * 已登录直接放行；未登录弹窗引导跳转登录页。
 * @returns true 表示已登录可继续；false 表示已拦截（弹窗由本函数负责展示）
 */
export function requireLogin(): boolean {
  if (isAuthenticated.value) return true
  if (state.status === 'unavailable') {
    void resumeSession()
    uni.showToast({ title: t('services.sessionUnavailable'), icon: 'none' })
    return false
  }
  uni.showModal({
    title: t('socialErrors.loginRequiredTitle'),
    content: t('socialErrors.loginRequiredContent'),
    confirmText: t('auth.goLogin'),
    cancelText: t('socialErrors.maybeLater'),
    success: ({ confirm }) => {
      if (confirm) uni.navigateTo({ url: '/pages/auth/login' })
    },
  })
  return false
}
