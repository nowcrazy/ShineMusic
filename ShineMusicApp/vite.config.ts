import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createRequire } from 'node:module'
import { defineConfig, type Plugin } from 'vite'

// @dcloudio/vite-plugin-uni 是 CJS 包且导出结构特殊（default 上才是插件函数），
// ESM import interop 拿不到函数，必须用 createRequire 取 .default。
const require = createRequire(import.meta.url)
const uni = require('@dcloudio/vite-plugin-uni').default

// 编译器 @dcloudio 全家桶精确锁定 3.0.0-alpha-5020520260821001，
// 与 HBuilderX 5.26 内置编译器及云打包基座完全同版本（根治白屏错位）。

// 以 vite.config.ts 自身位置为基准定位仓库根目录，不依赖 process.cwd()。
// HBuilderX / VS 等 IDE 启动时工作目录可能与命令行不同，用 cwd 会读错路径。
const CONFIG_DIR = path.dirname(fileURLToPath(import.meta.url))
const HOSTS_FILE = path.resolve(CONFIG_DIR, '../config/hosts.json')

/** config/hosts.json 的结构（与后端 Program.cs 共用同一份文件） */
interface HostsConfig {
  local?: {
    lanIp?: string
    apiPort?: number
    appApiPort?: number
    apiHost?: 'localhost' | 'lan'
  }
  server?: {
    webOrigin?: string
    apiOrigin?: string
    /** APK 发布包内置的公网稳定地址（DDNS 域名/服务器 IP），未配置时回退局域网真机调试地址 */
    appApiOrigin?: string
  }
}

/**
 * 读取仓库根目录的全局地址配置。
 * 读不到时返回空对象，下面的默认值兜底，保证构建不会因缺文件失败。
 */
function readHosts(): HostsConfig {
  try {
    return JSON.parse(fs.readFileSync(HOSTS_FILE, 'utf8')) as HostsConfig
  } catch {
    return {}
  }
}

/**
 * 监听仓库根目录的 config/hosts.json：文件一改就自动重启 dev server，
 * 让新的 lanIp / 端口立即生效（否则需手动重启）。
 * 仅在 dev（serve）阶段生效，不影响构建产物。
 */
function watchHostsPlugin(): Plugin {
  return {
    name: 'sm-watch-hosts',
    apply: 'serve',
    configureServer(server) {
      const hostsFile = HOSTS_FILE.toLowerCase()
      // hosts.json 在项目根目录之外，需显式加入监听
      server.watcher.add(HOSTS_FILE)
      const onChange = (changed: string) => {
        if (changed.toLowerCase() !== hostsFile) return
        server.config.logger.info('[hosts] config/hosts.json 已变更，正在重启 dev server…')
        server.restart()
      }
      server.watcher.on('change', onChange)
      server.watcher.on('add', onChange)
    },
  }
}

/**
 * 单一地址源：config/hosts.json
 *   - #Local 段 → 真机 App 调试地址
 *   - #Server 段 → 生产 APK 内置地址
 * 改 IP / 域名只改这一个文件，前端无需再动 .env。
 *
 * 本工程只产 App（APK），Web 前端见同级 ShineMusicWeb 工程。
 */
export default defineConfig(({ mode }) => {
  const hosts = readHosts()
  const local = hosts.local ?? {}
  const server = hosts.server ?? {}

  const appApiPort = local.appApiPort ?? 5180

  // 判断当前是否为 App 平台构建（云打包 / 真机运行）。
  // 注意：云打包时 mode 是 'production' 而非 'app'，必须用 UNI_PLATFORM 判断平台。
  const isAppPlatform = process.env.UNI_PLATFORM === 'app'

  // 客户端 API 基础地址（http.ts 通过 __SM_API_BASE__ 读取）：
  //   dev（yarn dev:app / HBuilderX 运行）→ 本地调试地址：优先 local.lanIp（真机同 Wi-Fi 直连，
  //        后端以 0.0.0.0 监听即可）；未配置 lanIp 时回退 127.0.0.1（模拟器 + adb reverse）；
  //   发行包（yarn build:app / 云打包）→ 优先 server.appApiOrigin（公网稳定地址）；
  //        未配置时回退 http://127.0.0.1:${appApiPort}。用户仍可在 App 设置里运行时覆盖。
  // 发行模式：uni build -p app --mode app（本地打包）与云打包（mode=production）；
  // 其余（uni -p app / HBuilderX 真机调试，mode=development）按本地调试处理。
  const isReleaseBuild = mode === 'app' || mode === 'production'
  const devApiHost = local.lanIp || '127.0.0.1'
  const apiBase = isReleaseBuild
    ? server.appApiOrigin || `http://127.0.0.1:${appApiPort}`
    : `http://${devApiHost}:${appApiPort}`

  return {
    plugins: [uni(), watchHostsPlugin()],
    // App 端（v3 service→view 渲染桥）不支持 <svg>（lucide 图标会全部隐形），
    // 用本地 shim（view + CSS mask + 内联 SVG data-URI）整体替换 lucide-vue-next；
    // shim 由 scripts/generate-lucide-shim.mjs 生成（yarn gen:icons）。
    resolve: isAppPlatform
      ? {
          alias: {
            'lucide-vue-next': fileURLToPath(new URL('./src/utils/lucide-shim.ts', import.meta.url)),
          },
        }
      : undefined,
    define: {
      __SM_API_BASE__: JSON.stringify(apiBase),
      // vue-i18n esm-bundler 特性开关：替换为字面量后不再出现
      // "You are running the esm-bundler build of vue-i18n" 提示，并启用 tree-shaking。
      // 本项目 i18n 全部为组合式 API（createI18n legacy: false），关闭 LEGACY_API 无功能影响。
      __VUE_I18N_FULL_INSTALL__: true,
      __INTLIFY_PROD_DEVTOOLS__: false,
      __VUE_I18N_LEGACY_API__: false,
    },
  }
})
