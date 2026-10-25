/// <reference types="vite/client" />

declare module '*.vue' {
  import type { DefineComponent } from 'vue'
  const component: DefineComponent<Record<string, any>, Record<string, any>, any>
  export default component
}

/** 构建期注入的后端 API 绝对地址（开发为空串，走 Vite 代理）。 */
declare const __SM_API_BASE__: string

/** App 端原生对象：条件编译块在运行时会被裁掉，类型检查阶段保留声明即可。 */
declare const plus: any

/** 后端 uni.request 的 data 泛型别名（App 源码沿用了 DCloud 的类型名）。 */
type AnyObject = Record<string, any>

/**
 * Web 平台 uni API 兼容层（实现见 src/platform/uni.ts）。
 * 参数统一为 any，保证从 App 迁移过来的回调（success/fail 解构参数）无需逐个补类型。
 */
declare const uni: {
  request: (options: any) => any
  uploadFile: (options: any) => any
  downloadFile: (options: any) => any
  chooseFile: (options: any) => any
  chooseMessageFile: (options: any) => any
  chooseImage: (options: any) => any
  getImageInfo: (options: any) => any
  compressImage: (options: any) => any
  createCanvasContext: (canvasId: string, instance?: any) => any
  canvasGetImageData: (options: any, instance?: any) => any
  canvasToTempFilePath: (options: any, instance?: any) => any
  createInnerAudioContext: () => any
  getBackgroundAudioManager: () => any
  createSelectorQuery: () => any
  createVideoContext?: (id: string) => any
  getSystemInfoSync: () => any
  getWindowInfo?: () => any
  onWindowResize: (cb: any) => void
  offWindowResize: (cb?: any) => void
  showToast: (options: any) => any
  hideToast: () => void
  showLoading: (options?: any) => any
  hideLoading: () => void
  showModal: (options: any) => any
  showActionSheet: (options: any) => any
  navigateTo: (options: any) => any
  redirectTo: (options: any) => any
  reLaunch: (options: any) => any
  switchTab: (options: any) => any
  navigateBack: (options?: any) => any
  setNavigationBarTitle: (options: any) => any
  setNavigationBarColor: (options: any) => any
  getStorageSync: (key: string) => any
  setStorageSync: (key: string, value: any) => void
  removeStorageSync: (key: string) => void
  clearStorageSync: () => void
  getStorageInfoSync: () => any
  pageScrollTo: (options: any) => any
  stopPullDownRefresh: () => void
  startPullDownRefresh: () => void
  setClipboardData: (options: any) => any
  getClipboardData: (options: any) => any
  vibrateShort: (options?: any) => any
  openURL?: (url: string) => void
  requireNativePlugin: (name: string) => any
  $emit: (event: string, ...args: any[]) => void
  $on: (event: string, handler: (...args: any[]) => void) => void
  $off: (event: string, handler?: (...args: any[]) => void) => void
  $once: (event: string, handler: (...args: any[]) => void) => void
  [key: string]: any
}

declare function getCurrentPages(): any[]
