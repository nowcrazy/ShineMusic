/**
 * 后台界面状态：侧边栏折叠状态、移动端抽屉开关与访问标签（面包屑）。
 * 认证会话本身由 services/session 的模块级单例承载，这里只放纯 UI 状态。
 *
 * 访问标签只存在于内存：刷新页面、退出登录、重新登录都会清空，不做任何持久化。
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

/** 历史上限：超出时淘汰最早的一条，避免顶栏无限膨胀 */
const BREADCRUMB_MAX = 10

export const useAdminStore = defineStore('admin', () => {
  const sidebarCollapsed = ref(false)
  const mobileMenuOpen = ref(false)
  const breadcrumbs = ref<BreadcrumbItem[]>([])

  function toggleSidebar() {
    sidebarCollapsed.value = !sidebarCollapsed.value
  }

  function closeMobileMenu() {
    mobileMenuOpen.value = false
  }

  /** 记录一次页面访问：同路径去重，新页面追加到末尾，超上限淘汰最早一条 */
  function pushBreadcrumb(item: BreadcrumbItem) {
    if (!item.path || !item.title) return
    if (breadcrumbs.value.some((b) => b.path === item.path)) return
    breadcrumbs.value.push(item)
    if (breadcrumbs.value.length > BREADCRUMB_MAX) breadcrumbs.value.shift()
  }

  /** 清空历史（会话结束、重新登录时调用） */
  function resetBreadcrumbs() {
    breadcrumbs.value = []
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
