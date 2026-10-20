/**
 * 系统能力开关模块：拉取并缓存 GET /api/v1/system/capabilities 功能矩阵。
 *
 * 缓存策略：
 * - 模块级 reactive 单例，App.onLaunch 时匿名加载一次，全局共享、无重复请求；
 * - 加载成功以服务端配置为准并标记 online；失败（后端不可达）保留内置默认值、
 *   标记 online=false，界面按默认开关继续可用；
 * - loaded 标记区分“尚未加载”与“加载完（无论成败）”，避免入口闪烁；
 * - 开关仅控制界面入口显隐，真正的权限/配额始终由服务端强制执行，不可作为安全边界。
 */
import { reactive } from 'vue'
import type { ApiCapabilities } from '@/types/music'
import { http } from './http'

// 功能开关只用于显示/隐藏入口，真正的权限仍由服务端执行。
const state = reactive<{
  /** 是否已完成首次加载（成功或失败都算） */
  loaded: boolean
  /** 能力接口是否在线（最近一次拉取是否成功） */
  online: boolean
  /** 能力矩阵；后端不可达期间使用下列内置默认值 */
  capabilities: ApiCapabilities
}>({
  loaded: false,
  online: false,
  capabilities: {
    anonymousPlayback: true,
    userCollections: true,
    songRequests: true,
    adminLibraryImport: false,
    manualReview: true,
    editorialCollections: true,
    listeningEvents: false,
    personalizedRecommendations: false,
    serverTranscoding: true,
    userUploads: false,
    allowedFormats: [],
    maxUploadSizeBytes: null,
    minimumPasswordStrength: 2,
  },
})

/**
 * 拉取系统能力开关并写入缓存。
 * - 请求：GET /api/v1/system/capabilities（匿名）
 * 无抛错：网络/服务错误时降级为 online=false 并保留默认值；
 * 无论成败都会把 loaded 置为 true。可在 App 启动时安全调用。
 */
export async function loadCapabilities() {
  try {
    state.capabilities = await http.get<ApiCapabilities>(
      '/api/v1/system/capabilities',
      false,
    )
    state.online = true
  } catch {
    state.online = false
  } finally {
    state.loaded = true
  }
}

/**
 * 获取能力开关响应式状态（单例）。
 * @returns reactive 对象 { loaded, online, capabilities }，组件可直接在模板/计算属性中订阅
 */
export function useCapabilities() {
  return state
}
