/**
 * 应用入口文件（uni-app + Vue3 SSR）。
 *
 * createApp 由 uni-app 框架调用，返回 app 实例供框架进行生命周期管理。
 * 此处仅挂载根组件 App.vue；全局状态（如 pinia）若需注册，可在
 * createSSRApp 之后、return 之前通过 app.use(...) 注入。
 */
import { createSSRApp } from 'vue'
import App from './App.vue'
import { patchShowModal } from '@/utils/modal-patch'
import { createAppI18n } from '@/locale'

// 在应用创建前修补 uni.showModal（仅 H5 端生效），修复点击按钮后模态框不关闭的 bug。
patchShowModal()

/**
 * 创建并返回 uni-app 应用实例。
 *
 * @returns 包含 app 实例的对象，供 uni-app 框架接管
 */
export function createApp() {
  const app = createSSRApp(App)
  // 注册 vue-i18n（legacy: false），使组件内 useI18n()/模板 $t 可用；
  // 必须在返回 app 前完成注册，否则 PlayerBar / LanguageSwitcher 等使用 useI18n 的组件会注入失败。
  app.use(createAppI18n())
  return { app }
}
