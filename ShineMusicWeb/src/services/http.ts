/**
 * HTTP 基础设施工厂模块：基于 uni.request 封装，对接后端 /api/v1 REST 契约。
 *
 * 四大机制：
 * 1. baseURL 解析（resolveUrl）：
 *    - H5 开发环境默认使用相对路径，由 Vite devServer 代理转发到后端，天然避免浏览器跨域；
 *    - App / 小程序等非 H5 环境没有 devServer 代理，必须通过构建期环境变量
 *      VITE_API_BASE_URL 配置后端绝对地址进行直连，未配置时仅告警并返回相对路径；
 * 2. 鉴权头注入：默认每个请求附带 `Authorization: Bearer <accessToken>`，
 *    匿名接口（首页、目录、能力开关等）在调用时显式传入 withAuth = false；
 * 3. 401 单飞刷新与重试队列：收到 401 时调用 session 模块注册的 refreshHandler；
 *    刷新 Promise 单飞（并发 401 共享一次刷新），成功后原请求带 _retry 标记自动重放一次；
 * 4. ApiError：统一错误对象，携带 HTTP 状态码、后端错误码 code、消息与 traceId；
 *    uni.request 层网络不可达（DNS/断网/超时）时 status 为 0、code 为 'network_error'。
 */
import { t } from '@/locale'
import { getAccessToken, getRefreshToken } from './tokens'

// A revision identifies one local authentication attempt, including anonymous
// attempts. Token rotation intentionally keeps the same revision.
let sessionRevision = 0
let requestGuard: (() => boolean) | null = null
let unauthorizedHandler: (() => void) | null = null
let unavailableHandler: (() => void) | null = null
export function captureSessionRevision(): number { return sessionRevision }
export function isSessionRevisionCurrent(revision: number): boolean { return revision === sessionRevision }
export function invalidateSessionRequests(): number { return ++sessionRevision }
export function assertSessionRevisionCurrent(revision: number): void {
  if (!isSessionRevisionCurrent(revision)) {
    throw new ApiError(0, 'session_changed', '登录状态已变化，请重试')
  }
}
export function setSessionRequestGuard(guard: () => boolean, onUnauthorized: () => void, onUnavailable: () => void): void {
  requestGuard = guard
  unauthorizedHandler = onUnauthorized
  unavailableHandler = onUnavailable
}
export function assertAuthenticatedRequestAllowed(): void {
  if (requestGuard && !requestGuard()) throw new ApiError(0, 'session_unavailable', '暂时无法验证登录状态，请联网后重试')
}

/** Fetch paths (files, artwork and previews) share the JSON session boundary. */
export async function fetchWithSession(url: string, options: RequestInit = {}, revision = captureSessionRevision()): Promise<Response> {
  assertSessionRevisionCurrent(revision)
  assertAuthenticatedRequestAllowed()
  const send = async () => {
    assertSessionRevisionCurrent(revision)
    assertAuthenticatedRequestAllowed()
    const headers = new Headers(options.headers)
    const token = getAccessToken()
    if (token) headers.set('Authorization', `Bearer ${token}`)
    try { return await fetch(url, { ...options, headers }) } catch (error) {
      if (isSessionRevisionCurrent(revision)) unavailableHandler?.()
      throw error
    }
  }
  let response = await send()
  assertSessionRevisionCurrent(revision)
  if (response.status === 401 && await refreshAccessToken()) {
    assertSessionRevisionCurrent(revision)
    response = await send()
    assertSessionRevisionCurrent(revision)
  }
  if (response.status === 401) {
    unauthorizedHandler?.()
    throw new ApiError(401, 'unauthorized', '登录凭据已失效，请重新登录')
  } else if (response.status >= 500) unavailableHandler?.()
  for (const method of ['arrayBuffer', 'blob', 'formData', 'json', 'text'] as const) {
    const read = response[method].bind(response)
    Object.defineProperty(response, method, { value: async () => {
      assertSessionRevisionCurrent(revision)
      const value = await read()
      assertSessionRevisionCurrent(revision)
      return value
    } })
  }
  return response
}

/** 后端 API 统一版本前缀，所有业务路径均以此开头，与后端路由 /api/v1 对齐 */
export const API_PREFIX = '/api/v1'
/**
 * 后端基础地址（构建期内置）：由 vite.config.ts 从仓库根目录 config/hosts.json 统一注入 __SM_API_BASE__；
 * 为空串时 H5 走相对路径 + devServer 代理。
 * VITE_API_BASE_URL 仅作为兜底（特殊场景手动覆盖用），日常不要再改它。
 * 实际请求地址取 currentApiBase()：用户在设置页自定义的地址优先于构建期内置值。
 */
export const API_BASE_URL: string =
  typeof __SM_API_BASE__ !== 'undefined' ? __SM_API_BASE__ : (import.meta.env.VITE_API_BASE_URL ?? '')

/** 用户自定义服务器地址的本地存储键（设置页写入；删除该键即恢复内置地址） */
export const SERVER_BASE_STORAGE_KEY = 'sm-server-base'

/** 规范化服务器地址：去空白与尾部斜杠，未带协议时补 http:// */
function normalizeServerBase(raw: string): string {
  const trimmed = raw.trim().replace(/\/+$/, '')
  return !trimmed || /^https?:\/\//i.test(trimmed) ? trimmed : `http://${trimmed}`
}

/** 运行时覆盖地址：设置页保存后立即生效，无需重启 App */
let overrideBase = ''
let overrideLoaded = false
function loadServerOverride() {
  // platform/uni imports the request helpers before installing its global shim.
  if (overrideLoaded || typeof uni === 'undefined') return
  overrideLoaded = true
  try {
    const saved = uni.getStorageSync(SERVER_BASE_STORAGE_KEY)
    if (typeof saved === 'string' && saved.trim()) overrideBase = normalizeServerBase(saved)
  } catch {
    // 本地存储不可用时静默降级，走构建期内置地址
  }
}

/** 当前生效的服务器地址：用户自定义 > 构建期内置 */
export function currentApiBase(): string {
  loadServerOverride()
  return overrideBase || API_BASE_URL
}

/** 读取用户自定义地址（设置页回显用），未自定义时为空串 */
export function getApiBaseOverride(): string {
  loadServerOverride()
  return overrideBase
}

/**
 * 保存用户自定义服务器地址（设置页调用）：立即生效并持久化到本地存储。
 * @param url 形如 http://域名或IP:端口；传空串清除自定义、恢复内置地址
 */
export function setApiBaseOverride(url: string) {
  loadServerOverride()
  overrideBase = normalizeServerBase(url)
  overrideLoaded = true
  try {
    if (overrideBase) uni.setStorageSync(SERVER_BASE_STORAGE_KEY, overrideBase)
    else uni.removeStorageSync(SERVER_BASE_STORAGE_KEY)
  } catch {
    // 持久化失败不阻断：本次会话内已生效
  }
}
/** 单次请求超时（毫秒）：避免弱网/后端无响应时请求长时间挂起 */
const REQUEST_TIMEOUT_MS = 15000

/**
 * 统一 API 错误类型。
 * 业务代码可据此区分网络错误（status === 0）、HTTP 错误（4xx/5xx），
 * 并可用 traceId 向后端日志定位单次请求。
 */
export class ApiError extends Error {
  constructor(
    /** HTTP 状态码；0 表示请求未到达后端（网络层失败） */
    public readonly status: number,
    /** 机器可读错误码：优先取后端 error.code，缺省为 internal_error / network_error */
    public readonly code: string,
    message: string,
    /** 后端链路追踪 ID，便于排查；网络错误时不存在 */
    public readonly traceId?: string,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

type HttpMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'

interface RequestOptions {
  // 匿名接口可不附带凭证；默认附带
  withAuth?: boolean
  // 单次请求超时（毫秒）；默认 REQUEST_TIMEOUT_MS，慢接口（如音源聚合搜索）可放大
  timeoutMs?: number
  // 内部：401 后是否允许刷新重试
  _retry?: boolean
}

/**
 * 把后端相对路径解析为可请求的完整 URL。
 * - 已是 http(s) 绝对地址的原样返回（封面等可能由后端给出绝对地址）；
 * - 有生效地址（用户自定义或构建期内置）时拼接为绝对地址（自动去掉末尾斜杠避免双斜杠）；
 * - 未配置时 H5 直接返回相对路径走 Vite 代理；非 H5 环境无法解析，告警后仍返回相对路径。
 * @param path 后端返回的相对路径或任意 http(s) URL
 * @returns 解析后的请求地址
 */
export function resolveUrl(path: string): string {
  if (/^https?:\/\//i.test(path)) return path
  const base = currentApiBase()
  if (base) return `${base.replace(/\/$/, '')}${path}`
  // #ifdef H5
  return path
  // #endif
  // #ifndef H5
  console.warn('未配置 VITE_API_BASE_URL，非 H5 环境无法访问相对接口路径')
  return path
  // #endif
}

interface ErrorBody {
  error?: { code?: string; message?: string; traceId?: string }
}

/**
 * 执行单次 uni.request（Promise 化），负责内容类型与鉴权头注入。
 * @param method HTTP 方法
 * @param url 已解析的完整地址
 * @param data 请求体（GET 不发送 body）
 * @param withAuth 是否注入 Authorization 头
 * @returns resolve 为 { status, data }（HTTP 错误状态也在此返回，由上层统一抛错）；
 *          reject 仅发生在 uni.request 网络层失败，包装为 ApiError(0, 'network_error')
 */
function sendRequest(method: HttpMethod, url: string, data: unknown, withAuth: boolean, timeoutMs?: number) {
  return new Promise<{ status: number; data: unknown }>((resolve, reject) => {
    const header: Record<string, string> = {}
    if (data !== undefined && method !== 'GET') header['Content-Type'] = 'application/json'
    // 显式声明客户端类型，后端审计日志据此区分 PC 后台 / App（设备描述由 UA 解析）。
    header['X-Client-Type'] = 'web-admin'
    const token = getAccessToken()
    if (withAuth && token) header.Authorization = `Bearer ${token}`
    uni.request({
      url,
      method: method as any,
      data: data as AnyObject | undefined,
      header,
      timeout: timeoutMs ?? REQUEST_TIMEOUT_MS,
      success: ({ statusCode, data: body }) => resolve({ status: statusCode, data: body }),
      fail: (error) => reject(new ApiError(0, 'network_error', error.errMsg || t('services.networkError'))),
    })
  })
}

// 由 session 模块注册：401 时用 refresh token 换新 access token。
let refreshHandler: (() => Promise<boolean>) | null = null
/**
 * 注册令牌刷新处理器（由 session 模块在初始化时调用）。
 * 处理器需返回布尔结果：true 表示刷新成功（可重试原请求），false 表示刷新失败（应放弃）。
 * @param handler 刷新回调，内部负责单飞去重与令牌落盘
 */
export function setRefreshHandler(handler: () => Promise<boolean>) {
  refreshHandler = handler
}

/**
 * 主动触发一次令牌刷新（单飞去重由 session 端的 handler 保证）。
 * 供绕过 uni.request 的请求路径（如文件上传、blob 拉取）在收到 401 时复用刷新机制。
 * @returns 是否刷新成功（未注册处理器或无 refresh token 时为 false）
 */
export async function refreshAccessToken(): Promise<boolean> {
  if (!refreshHandler || !getRefreshToken()) return false
  return refreshHandler()
}

/**
 * 核心请求函数：发送 HTTP 请求并处理 401 自动刷新重试与错误归一化。
 *
 * 处理流程：
 * 1. 发送请求；
 * 2. 若响应为 401、此前未重试过、请求带鉴权、本地存在 refresh token 且已注册刷新器，
 *    则调用刷新器；刷新成功后以 _retry: true 递归重放同一请求（只重试一次）；
 * 3. 状态码不在 2xx 区间时，解析后端 { error: { code, message, traceId } } 错误体并抛出 ApiError；
 * 4. 成功时把响应体断言为泛型 T 返回（运行时不做 schema 校验，信任后端契约）。
 * @param method HTTP 方法
 * @param path 以 /api/v1 开头的接口路径（或经 resolveUrl 兼容的地址）
 * @param data 请求体数据，GET/DELETE 不传
 * @param options.withAuth 是否附带鉴权头（默认 true）；options._retry 为内部重试标记
 * @returns 响应体（类型由调用方按后端 DTO 指定）
 * @throws {ApiError} 非 2xx 响应或网络失败时抛出
 */
export async function request<T>(
  method: HttpMethod,
  path: string,
  data?: unknown,
  options: RequestOptions = {},
): Promise<T> {
  const revision = captureSessionRevision()
  const { withAuth = true, timeoutMs, _retry = false } = options
  // /me is the verification request used to leave the unavailable state.
  if (withAuth && path !== `${API_PREFIX}/me`) assertAuthenticatedRequestAllowed()
  let result: { status: number; data: unknown }
  try {
    result = await sendRequest(method, resolveUrl(path), data, withAuth, timeoutMs)
  } catch (error) {
    if (withAuth && isSessionRevisionCurrent(revision)) unavailableHandler?.()
    throw error
  }
  const { status, data: body } = result
  assertSessionRevisionCurrent(revision)

  if (status === 401 && !_retry && withAuth && getRefreshToken() && refreshHandler) {
    const refreshed = await refreshHandler()
    assertSessionRevisionCurrent(revision)
    if (refreshed) {
      return request<T>(method, path, data, { withAuth, timeoutMs, _retry: true })
    }
  }

  if (status < 200 || status >= 300) {
    if (status === 401 && withAuth) unauthorizedHandler?.()
    else if (status >= 500 && withAuth) unavailableHandler?.()
    const errorBody = (body ?? {}) as ErrorBody
    const detail = errorBody.error ?? {}
    throw new ApiError(
      status,
      detail.code || (status === 0 ? 'network_error' : 'internal_error'),
      detail.message || t('services.requestFailed', { status }),
      detail.traceId,
    )
  }

  return body as T
}

/**
 * HTTP 动词快捷方法集合。
 * - get/delete 的第二参为 withAuth（默认 true）；
 * - post/put/patch 的第二参为请求体（缺省发送空对象以满足后端 JSON 解析），第三参为 withAuth。
 * 路径均为以 /api/v1 开头的后端相对路径。
 */
export const http = {
  get: <T>(path: string, withAuth = true) => request<T>('GET', path, undefined, { withAuth }),
  post: <T>(path: string, body?: unknown, withAuth = true) =>
    request<T>('POST', path, body ?? {}, { withAuth }),
  put: <T>(path: string, body?: unknown, withAuth = true) =>
    request<T>('PUT', path, body ?? {}, { withAuth }),
  patch: <T>(path: string, body?: unknown, withAuth = true) =>
    request<T>('PATCH', path, body ?? {}, { withAuth }),
  delete: <T>(path: string, withAuth = true) => request<T>('DELETE', path, undefined, { withAuth }),
}
