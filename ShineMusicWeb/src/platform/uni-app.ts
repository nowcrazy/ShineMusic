/**
 * 替代 @dcloudio/uni-app 的页面生命周期导出。
 *
 * 页面在 Web 端每次进入都会重新挂载，因此：
 * - onLoad  → onMounted（参数取当前路由 query）
 * - onShow  → onMounted（返回上一页时页面会重新挂载）
 * - onReachBottom → window 滚动监听
 */
import { onMounted, onUnmounted } from 'vue'
import { useRoute } from 'vue-router'

type AnyHandler = (options?: any) => void

function routeOptions(): Record<string, string | string[]> {
  const route = useRoute()
  return { ...route.query, ...route.params } as Record<string, string | string[]>
}

export function onLoad(handler: AnyHandler) {
  const options = routeOptions()
  onMounted(() => {
    void handler(options)
  })
}

export function onShow(handler: AnyHandler) {
  onMounted(() => {
    void handler()
  })
}

export function onReady(handler: AnyHandler) {
  onMounted(() => {
    void handler()
  })
}

export function onUnload(handler: AnyHandler) {
  onUnmounted(() => {
    void handler()
  })
}

export function onHide(handler: AnyHandler) {
  onUnmounted(() => {
    void handler()
  })
}

export function onLaunch(handler: AnyHandler) {
  onMounted(() => {
    void handler()
  })
}

export function onPullDownRefresh(_handler?: AnyHandler) {
  // Web 端没有下拉刷新容器，页面内的刷新按钮/自动加载已覆盖
}

export function onReachBottom(handler: AnyHandler) {
  let latched = false
  const onScroll = () => {
    const documentHeight = document.documentElement.scrollHeight
    const nearBottom = window.scrollY + window.innerHeight >= documentHeight - 160
    if (nearBottom && !latched) {
      latched = true
      void handler()
    } else if (!nearBottom && window.scrollY + window.innerHeight < documentHeight - 400) {
      latched = false
    }
  }
  onMounted(() => window.addEventListener('scroll', onScroll, { passive: true }))
  onUnmounted(() => window.removeEventListener('scroll', onScroll))
}

export function onPageScroll(handler: AnyHandler) {
  const onScroll = () => {
    void handler({ scrollTop: window.scrollY })
  }
  onMounted(() => window.addEventListener('scroll', onScroll, { passive: true }))
  onUnmounted(() => window.removeEventListener('scroll', onScroll))
}

export function onBackPress(_handler?: AnyHandler) {
  // Web 端由浏览器/路由接管返回
}

export function onTabItemTap(_handler?: AnyHandler) {
  // Web 端没有原生 tabBar
}
