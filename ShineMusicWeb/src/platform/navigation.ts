/**
 * 后台导航兼容层：把 uni 风格的页面跳转映射到浏览器地址栏。
 *
 * 管理后台只有一条桌面端路由栈，不需要模拟页面堆栈；这里保留同名导出，
 * 以便 platform/uni.ts 继续向业务层提供 uni 调用约定。
 */

function normalize(url: string) {
  return url.startsWith('/') ? url : `/${url}`
}

export function navigateTo(options: { url: string }) {
  window.location.assign(normalize(options.url))
}

export function redirectTo(options: { url: string }) {
  window.location.replace(normalize(options.url))
}

export function reLaunch(options: { url: string }) {
  window.location.replace(normalize(options.url))
}

export function switchTab(options: { url: string }) {
  window.location.assign(normalize(options.url))
}

export function navigateBack(options?: { delta?: number }) {
  if (window.history.length > (options?.delta ?? 1)) {
    window.history.back()
  } else {
    window.location.assign('/dashboard')
  }
}

/** 后台没有多页栈，返回空数组以满足 getCurrentPages 调用约定。 */
export function pageStack() {
  return []
}
