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
import { computed, reactive } from 'vue'
import type { ApiAuthTokens, ApiInvitationValidation, ApiMe } from '@/types/music'
import { t } from '@/locale'
import { http, setRefreshHandler } from './http'
import { syncRemoteTheme } from './user-theme'
import { clearTokens, getRefreshToken, hasSession, restoreTokens, saveTokens } from './tokens'
import { clearActivity, hasIdleExpired, markActivity } from './idle-session'

interface SessionState {
  /** 当前用户资料；null 表示未登录或尚未加载 */
  user: ApiMe | null
  /** 会话状态机：unknown=启动未判定 / anonymous=匿名 / authenticated=已登录 */
  status: 'unknown' | 'anonymous' | 'authenticated'
}

const state = reactive<SessionState>({ user: null, status: 'unknown' })

/** 当前登录用户资料（响应式），未登录为 null */
export const currentUser = computed(() => state.user)
/** 是否已登录（响应式），用于路由/操作守卫 */
export const isAuthenticated = computed(() => state.status === 'authenticated')
/** 当前用户是否具备 admin 角色（响应式；仅控制管理端入口，服务端独立鉴权） */
export const isAdmin = computed(() => state.user?.roles.includes('admin') ?? false)
/** 原始会话状态（响应式）：unknown / anonymous / authenticated */
export const sessionStatus = computed(() => state.status)

// 401 时自动用 refresh token 续期，避免并发刷新。
let refreshing: Promise<boolean> | null = null
setRefreshHandler(() => {
  if (!refreshing) {
    refreshing = (async () => {
      const refreshToken = getRefreshToken()
      if (!refreshToken) return false
      try {
        const tokens = await http.post<ApiAuthTokens>(
          '/api/v1/auth/refresh',
          { refreshToken },
          false,
        )
        saveTokens(tokens.accessToken, tokens.refreshToken)
        return true
      } catch {
        clearTokens()
        state.user = null
        state.status = 'anonymous'
        return false
      } finally {
        refreshing = null
      }
    })()
  }
  return refreshing
})

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
  const tokens = await http.post<ApiAuthTokens>(
    '/api/v1/auth/login',
    { email: email.trim(), password },
    false,
  )
  saveTokens(tokens.accessToken, tokens.refreshToken)
  markActivity()
  await loadCurrentUser()
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
  const me = await http.get<ApiMe>('/api/v1/me')
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
  try {
    if (refreshToken) await http.post('/api/v1/auth/logout', { refreshToken }, false)
  } finally {
    clearTokens()
    clearActivity()
    state.user = null
    state.status = 'anonymous'
  }
}

/**
 * 空闲超时登出：仅本地清理，不再请求服务端注销（refresh token 到期后由后端自然回收）。
 * 供 Web 端空闲检查器与路由守卫调用。
 */
export function expireInactiveSession() {
  clearTokens()
  clearActivity()
  state.user = null
  state.status = 'anonymous'
}

/**
 * 空闲超时检查：已超时则清理本地会话并返回 true，调用方负责跳转登录页。
 * 非 Web 端无活动记录，hasIdleExpired 恒为 false，行为不受影响。
 */
export function enforceIdleTimeout(): boolean {
  if (!hasIdleExpired()) return false
  expireInactiveSession()
  return true
}

/**
 * App 启动时恢复会话：先从本地存储载入令牌，有凭证则尝试拉取用户资料。
 * 拉取过程中若 access token 过期，http 层会自动用 refresh token 刷新并重试一次；
 * 刷新也失败（refresh token 过期/被吊销）或无凭证时，安全降级为 anonymous。
 * 无抛错：所有异常在函数内消化，保证应用启动不被阻断。
 */
export async function restoreSession() {
  restoreTokens()
  // 上次会话若已空闲超时，直接清理本地令牌，不再尝试刷新（否则等同于永久保持登录）。
  if (hasIdleExpired()) {
    clearTokens()
    clearActivity()
    state.user = null
    state.status = 'anonymous'
    return
  }
  if (!hasSession()) {
    state.status = 'anonymous'
    return
  }
  try {
    await loadCurrentUser()
    // 会话恢复成功后同步自定义图片主题（远端启用则应用；失败静默）。
    void syncRemoteTheme()
  } catch {
    clearTokens()
    state.user = null
    state.status = 'anonymous'
  }
}

/**
 * 登录守卫：需要登录的操作前调用。
 * 已登录直接放行；未登录弹窗引导跳转登录页。
 * @returns true 表示已登录可继续；false 表示已拦截（弹窗由本函数负责展示）
 */
export function requireLogin(): boolean {
  if (isAuthenticated.value) return true
  uni.showModal({
    title: t('socialErrors.loginRequiredTitle'),
    content: t('socialErrors.loginRequiredContent'),
    confirmText: t('auth.goLogin'),
    cancelText: t('socialErrors.maybeLater'),
    success: ({ confirm }) => {
      if (confirm) uni.navigateTo({ url: '/login' })
    },
  })
  return false
}
