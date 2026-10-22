/**
 * App 端（APP-PLUS）基座启动竞态兜底补丁。
 *
 * 背景：HBuilder 基座 / App 运行时下，service 层（app-service.js 求值）与视图层
 * （launch webview 加载 __uniappview.html）存在启动竞态：视图层先就绪时发出的
 * onWebviewReady 可能在 service 侧处理链路就绪前到达而被丢弃，导致运行时不导航、
 * 页面栈恒为空，表现为启动后仅有 globalStyle 渐变背景（白屏）。
 * 该现象为非确定性（同一包多次启动结果不同），bundle 越大越易触发；
 * 最小工程（bundle 极小）从未复现。
 *
 * 策略（全部带守卫；链路正常时零副作用，仅输出一行诊断日志）：
 * 1. createApp 后约 2.5s：若 getApp().$vm 尚未注册且 __registerApp 可用 → 手动注册；
 * 2. 再 400ms：若页面栈仍为空 → 向 launch webview（id 动态获取）补发 onWebviewReady；
 * 3. 再 400ms：页面栈仍为空 → reLaunch 到首页（entryPagePath）。
 * 救援序列（注册 + onWebviewReady + reLaunch）已在与基座相同的运行时环境中实测可正常打开页面。
 */
import type { App } from 'vue'

// __registerApp / UniServiceJSBridge 由 uni-app 编译产物（app-service.js）的
// 运行时外壳注入，与业务代码同属一个求值作用域，可直接按标识符引用；
// H5 等端不存在，必须经 typeof 守卫访问，绝不裸引用（本模块仅在 APP-PLUS 被引入）。
declare const __registerApp: (vm: unknown) => void
declare const UniServiceJSBridge: {
  subscribeHandler: (name: string, data: unknown, webviewId?: string) => void
}

const REGISTER_CHECK_DELAY = 2500
const READY_SIGNAL_DELAY = 400
const NAV_CHECK_DELAY = 400
const RESCUE_VERIFY_DELAY = 1500

/** 当前页面栈长度（getCurrentPages 在 service 层始终可用，仍做守卫）。 */
function pagesCount(): number {
  try {
    return typeof getCurrentPages === 'function' ? getCurrentPages().length : 0
  } catch {
    return 0
  }
}

/** launch webview id：基座中为 appid（如 "HBuilder"），正式运行时为 "1"，动态获取不写死。 */
function launchWebviewId(): string {
  try {
    const plusApi = (globalThis as {
      plus?: { webview: { getLaunchWebview(): { id?: string } | null } }
    }).plus
    return plusApi?.webview.getLaunchWebview()?.id || '1'
  } catch {
    return '1'
  }
}

/**
 * 安装启动兜底（仅 APP-PLUS 调用，见 main.ts 条件编译）。
 * 链路正常时所有检查短路返回，不产生任何副作用。
 */
export function installAppBootFix(app: App): void {
  setTimeout(() => {
    try {
      let registered = false
      try {
        registered = typeof getApp === 'function' && !!(getApp() as unknown as { $vm?: unknown } | undefined)?.$vm
      } catch {
        registered = false
      }
      console.log('[boot-fix] registered =', registered, ', pages =', pagesCount(), ', launchWebview =', launchWebviewId())
      if (!registered && typeof __registerApp === 'function') {
        const internal = (app as unknown as { _instance?: { proxy?: unknown } })._instance
        __registerApp(internal?.proxy ?? internal ?? app)
        console.log('[boot-fix] __registerApp applied')
      }
    } catch (error) {
      console.log('[boot-fix] register check failed:', error)
    }

    setTimeout(() => {
      try {
        if (pagesCount() > 0) {
          console.log('[boot-fix] pages ready, no fallback needed')
          return
        }
        const webviewId = launchWebviewId()
        console.log('[boot-fix] chain stalled, fire onWebviewReady, webview =', webviewId)
        const bridge = typeof UniServiceJSBridge !== 'undefined' ? UniServiceJSBridge : undefined
        if (bridge && typeof bridge.subscribeHandler === 'function') {
          bridge.subscribeHandler('onWebviewReady', {}, webviewId)
        } else {
          console.log('[boot-fix] UniServiceJSBridge unavailable')
        }
      } catch (error) {
        console.log('[boot-fix] ready signal failed:', error)
      }

      setTimeout(() => {
        try {
          if (pagesCount() === 0) {
            console.log('[boot-fix] still no pages, reLaunch to home')
            uni.reLaunch({ url: '/pages/home/index' })
          } else {
            console.log('[boot-fix] pages ready after signal')
          }
        } catch (error) {
          console.log('[boot-fix] nav failed:', error)
        }

        setTimeout(() => {
          console.log('[boot-fix] final pages =', pagesCount())
        }, RESCUE_VERIFY_DELAY)
      }, NAV_CHECK_DELAY)
    }, READY_SIGNAL_DELAY)
  }, REGISTER_CHECK_DELAY)
}
