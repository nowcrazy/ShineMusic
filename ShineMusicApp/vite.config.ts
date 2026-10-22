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
    webPort?: number
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
 *   - #Local 段 → 开发（dev 代理）、真机 App
 *   - #Server 段 → 生产构建
 * 改 IP / 域名只改这一个文件，前端无需再动 .env。
 */
export default defineConfig(({ mode, command }) => {
  const hosts = readHosts()
  const local = hosts.local ?? {}
  const server = hosts.server ?? {}

  const lanIp = local.lanIp ?? ''
  const webPort = local.webPort ?? 5173
  const apiPort = local.apiPort ?? 5143
  const appApiPort = local.appApiPort ?? 5180
  // apiHost = 'lan' 表示后端在另一台机器/手机上，代理走局域网 IP
  const apiHost = local.apiHost === 'lan' && lanIp ? lanIp : 'localhost'

  // H5 devServer 代理目标（开发环境）
  const apiTarget = `http://${apiHost}:${apiPort}`

  // 判断当前是否为 App 平台构建（云打包 / 真机运行）。
  // 注意：云打包时 mode 是 'production' 而非 'app'，必须用 UNI_PLATFORM 判断平台。
  const isAppPlatform = process.env.UNI_PLATFORM === 'app'

  // 客户端 API 基础地址（http.ts 通过 __SM_API_BASE__ 读取）：
  //   app 平台 → APK 发布包内置地址：优先 server.appApiOrigin（公网稳定地址），未配置时回退局域网真机调试地址；
  //              用户还可在 App 设置里运行时覆盖（http.ts 的 setApiBaseOverride）
  //   h5 dev   → 空串，走相对路径 + devServer 代理
  //   h5 prod  → 同域留空（Nginx 反代）；前后端不同域时用 server.apiOrigin
  const apiBase = isAppPlatform
    ? server.appApiOrigin || `http://${lanIp}:${appApiPort}`
    : mode === 'production'
      ? server.apiOrigin && server.apiOrigin !== server.webOrigin
        ? server.apiOrigin
        : ''
      : ''

  return {
    plugins: [uni(), watchHostsPlugin()],
    // App 端（v3 service→view 渲染桥）不支持 <svg>（lucide 图标会全部隐形），
    // 用本地 shim（view + CSS mask + 内联 SVG data-URI）整体替换 lucide-vue-next；
    // H5 仍用原包。shim 由 scripts/generate-lucide-shim.mjs 生成（yarn gen:icons）。
    resolve: isAppPlatform
      ? {
          alias: {
            'lucide-vue-next': fileURLToPath(new URL('./src/utils/lucide-shim.ts', import.meta.url)),
          },
        }
      : undefined,
    // H5 构建产物用相对路径：放进 5+App 壳（file:// 本地加载）时，绝对路径 /assets 会找不到资源。
    // 仅 h5 平台的构建生效；app 产物与 dev server 不受影响。
    base: command === 'build' && process.env.UNI_PLATFORM === 'h5' ? './' : undefined,
    define: {
      __SM_API_BASE__: JSON.stringify(apiBase),
      // vue-i18n esm-bundler 特性开关：替换为字面量后不再出现
      // "You are running the esm-bundler build of vue-i18n" 提示，并启用 tree-shaking。
      // 本项目 i18n 全部为组合式 API（createI18n legacy: false），关闭 LEGACY_API 无功能影响。
      __VUE_I18N_FULL_INSTALL__: true,
      __INTLIFY_PROD_DEVTOOLS__: false,
      __VUE_I18N_LEGACY_API__: false,
    },
    server: {
      proxy: {
        '/api': { target: apiTarget, changeOrigin: true },
        '/health': { target: apiTarget, changeOrigin: true },
      },
    },
  }
})
