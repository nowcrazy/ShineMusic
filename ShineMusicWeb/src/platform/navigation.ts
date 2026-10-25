/**
 * Web 路由桥：把 uni.navigateTo / redirectTo / reLaunch / switchTab / navigateBack
 * 统一映射到 vue-router，并把页面栈维护在 sessionStorage，供 getCurrentPages() 使用。
 */
import type { Router } from 'vue-router'

const STACK_KEY = 'sm-web-page-stack'

let router: Router | null = null
let stack: string[] = []

function readStack(): string[] {
  try {
    const raw = sessionStorage.getItem(STACK_KEY)
    const parsed = raw ? (JSON.parse(raw) as unknown) : null
    return Array.isArray(parsed) ? parsed.filter((item): item is string => typeof item === 'string') : []
  } catch {
    return []
  }
}

function writeStack() {
  try {
    sessionStorage.setItem(STACK_KEY, JSON.stringify(stack))
  } catch {
    // sessionStorage 不可用时仅内存维护
  }
}

export function installRouter(target: Router) {
  router = target
  stack = readStack()
  if (stack.length === 0) stack = [target.currentRoute.value.fullPath]
  writeStack()

  target.afterEach((to, from) => {
    if (to.fullPath === from.fullPath) return
    // 浏览器后退 / router.back() 会把栈顶弹掉
    const idx = stack.lastIndexOf(from.fullPath)
    if (idx >= 0 && idx < stack.length - 1) {
      stack = stack.slice(0, idx + 1)
    }
    if (stack[stack.length - 1] !== to.fullPath) stack.push(to.fullPath)
    writeStack()
  })
}

export function pageStack(): string[] {
  return [...stack]
}

function normalizeUrl(url: string): string {
  return url.startsWith('/') ? url : `/${url}`
}

function fire(options: Record<string, unknown>, key: 'success' | 'fail' | 'complete', payload?: unknown) {
  const handler = options[key]
  if (typeof handler === 'function') (handler as (value?: unknown) => void)(payload)
}

export function navigateTo(options: Record<string, unknown>) {
  const url = normalizeUrl(String(options.url ?? '/pages/home/index'))
  if (!router) {
    window.location.hash = `#${url}`
    fire(options, 'success')
    fire(options, 'complete')
    return
  }
  router
    .push(url)
    .then(() => {
      fire(options, 'success')
      fire(options, 'complete')
    })
    .catch((error: unknown) => {
      fire(options, 'fail', error)
      fire(options, 'complete')
    })
}

export function redirectTo(options: Record<string, unknown>) {
  const url = normalizeUrl(String(options.url ?? '/pages/home/index'))
  if (!router) {
    window.location.hash = `#${url}`
    return
  }
  router.replace(url)
}

export function reLaunch(options: Record<string, unknown>) {
  const url = normalizeUrl(String(options.url ?? '/pages/home/index'))
  stack = [url]
  writeStack()
  if (router) router.replace(url)
  else window.location.hash = `#${url}`
}

export function switchTab(options: Record<string, unknown>) {
  navigateTo(options)
}

export function navigateBack(options: Record<string, unknown> = {}) {
  const delta = Number(options.delta ?? 1)
  if (!router) {
    window.history.go(-delta)
    return
  }
  const targetIndex = Math.max(0, stack.length - 1 - delta)
  const target = stack[targetIndex]
  stack = stack.slice(0, targetIndex + 1)
  writeStack()
  if (target && targetIndex >= 0) router.push(target)
  else router.back()
}

export function currentRoutePath(): string {
  return router?.currentRoute.value.fullPath ?? ''
}
