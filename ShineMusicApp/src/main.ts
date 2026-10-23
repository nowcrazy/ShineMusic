/**
 * 应用入口文件（uni-app + Vue3）。
 *
 * createApp 由 uni-app 框架调用，返回 app 实例供框架进行生命周期管理。
 * 此处仅挂载根组件 App.vue；全局状态（如 pinia）若需注册，可在
 * createSSRApp 之后、return 之前通过 app.use(...) 注入。
 */
import { createSSRApp } from 'vue'
import * as VueRuntime from 'vue'
import App from './App.vue'
import { patchShowModal } from '@/utils/modal-patch'
import { createAppI18n } from '@/locale'

/**
 * uni-app 定制 Vue 运行时额外导出的应用工厂。
 *
 * App 端（app-plus）必须用它创建应用：内部会在 mount('#app') 时调用
 * __registerApp 注册应用，并安装页面生命周期插件（$applyOptions）；
 * 若用 Vue 官方 createSSRApp，应用不会被注册，页面 onLoad/onShow 不触发
 * （表现为二级页面一直停在加载态）。H5 端没有该导出，回退 createSSRApp。
 */
type UniVueRuntime = typeof import('vue') & {
  createVueApp?: typeof createSSRApp
}

// 在应用创建前修补 uni.showModal（仅 H5 端生效），修复点击按钮后模态框不关闭的 bug。
patchShowModal()

/**
 * 创建并返回 uni-app 应用实例。
 *
 * @returns 包含 app 实例的对象，供 uni-app 框架接管
 */
export function createApp() {
  const runtime = VueRuntime as UniVueRuntime
  const app = (runtime.createVueApp ?? createSSRApp)(App)
  // 注册 vue-i18n（legacy: false），使组件内 useI18n()/模板 $t 可用；
  // 必须在返回 app 前完成注册，否则 PlayerBar / LanguageSwitcher 等使用 useI18n 的组件会注入失败。
  app.use(createAppI18n())
  return { app }
}
