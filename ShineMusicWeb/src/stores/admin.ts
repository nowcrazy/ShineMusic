/**
 * 后台界面状态：侧边栏折叠状态、移动端抽屉开关与面包屑访问历史。
 * 认证会话本身由 services/session 的模块级单例承载，这里只放纯 UI 状态。
 */
import { defineStore } from 'pinia'
import { ref } from 'vue'

/** 面包屑历史单条记录 */
export interface BreadcrumbItem {
  /** 路由路径（去重键，点击时用于跳转） */
  path: string
  /** 页面标题（来自 route.meta.title） */
  title: string
}

const BREADCRUMB_STORAGE_KEY = 'sm-admin-breadcrumbs'
/** 历史上限：超出时淘汰最早的一条，避免顶栏无限膨胀 */
const BREADCRUMB_MAX = 10

/** 从 sessionStorage 恢复历史（刷新页面后保留本次会话的访问轨迹） */
function loadBreadcrumbs(): BreadcrumbItem[] {
  try {
    const raw = sessionStorage.getItem(BREADCRUMB_STORAGE_KEY)
    if (!raw) return []
    const parsed = JSON.parse(raw)
    if (!Array.isArray(parsed)) return []
    return parsed.filter(
      (item): item is BreadcrumbItem =>
        !!item && typeof item.path === 'string' && typeof item.title === 'string',
    )
  } catch {
    return []
  }
}

export const useAdminStore = defineStore('admin', () => {
  const sidebarCollapsed = ref(false)
  const mobileMenuOpen = ref(false)
  const breadcrumbs = ref<BreadcrumbItem[]>(loadBreadcrumbs())

  function toggleSidebar() {
    sidebarCollapsed.value = !sidebarCollapsed.value
  }

  function closeMobileMenu() {
    mobileMenuOpen.value = false
  }

  function persistBreadcrumbs() {
    try {
      sessionStorage.setItem(BREADCRUMB_STORAGE_KEY, JSON.stringify(breadcrumbs.value))
    } catch {
      // 存储不可用时仅保留内存态，不影响导航
    }
  }

  /** 记录一次页面访问：同路径去重，新页面追加到末尾，超上限淘汰最早一条 */
  function pushBreadcrumb(item: BreadcrumbItem) {
    if (!item.path || !item.title) return
    if (breadcrumbs.value.some((b) => b.path === item.path)) return
    breadcrumbs.value.push(item)
    if (breadcrumbs.value.length > BREADCRUMB_MAX) breadcrumbs.value.shift()
    persistBreadcrumbs()
  }

  /** 清空历史（退出登录时调用） */
  function resetBreadcrumbs() {
    breadcrumbs.value = []
    try {
      sessionStorage.removeItem(BREADCRUMB_STORAGE_KEY)
    } catch {
      // 忽略
    }
  }

  /**
   * 关闭一条面包屑：从历史里移除指定 path。
   * @returns 应该跳转的下一条路径 —— 如果关闭的是当前页，返回被移除条目前紧邻的面包屑 path（供 View 跳回）；
   *          如果关闭的是历史页，返回 null（只删不跳，View 无需额外动作）；
   *          如果被关位置前面没有其他面包屑了，返回 null。
   */
  function closeBreadcrumb(path: string, currentPath: string): string | null {
    const idx = breadcrumbs.value.findIndex((b) => b.path === path)
    if (idx === -1) return null
    const closingCurrent = path === currentPath
    // 关当前页 → 跳到被关位置的前一条
    // 关历史页 → 只删，返回 null
    const jumpTarget = closingCurrent ? breadcrumbs.value[idx - 1]?.path ?? null : null
    breadcrumbs.value.splice(idx, 1)
    persistBreadcrumbs()
    return jumpTarget
  }

  return {
    sidebarCollapsed,
    mobileMenuOpen,
    breadcrumbs,
    toggleSidebar,
    closeMobileMenu,
    pushBreadcrumb,
    resetBreadcrumbs,
    closeBreadcrumb,
  }
})
