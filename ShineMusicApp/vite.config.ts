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
    webPort?: number
    apiPort?: number
    appApiPort?: number
  }
  server?: {
    /** 唯一服务器地址：换成自己的服务器时只改这一处，App / H5 / Web / 后端全部跟随 */
    origin?: string
    /** 以下为旧版字段，仅作兼容回退，新配置不需要填写 */
    webOrigin?: string
    apiOrigin?: string
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
 * 让新的端口 / 固定入口配置立即生效（否则需手动重启）。
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
 *   - #Local 段 → 仅本地开发端口
 *   - #Server 段 → origin：唯一服务器地址（域名，或 http://服务器IP:端口）
 * 换服务器只改 origin 一处，App / H5 / Web / 后端全部跟随；配置里不含任何会变的本机局域网 IP。
 *
 * 本工程产 App（APK）；H5（uni -p h5）仅用于开发期 UI/业务流预览、不发版，正式 Web 前端见同级 ShineMusicWeb 工程。
 */
export default defineConfig(() => {
  const hosts = readHosts()
  const local = hosts.local ?? {}
  const server = hosts.server ?? {}

  const appApiPort = local.appApiPort ?? 5180

  // 判断当前是否为 App 平台构建（云打包 / 真机运行）。
  // 注意：云打包时 mode 是 'production' 而非 'app'，必须用 UNI_PLATFORM 判断平台。
  const isAppPlatform = process.env.UNI_PLATFORM === 'app'
  const isH5 = process.env.UNI_PLATFORM === 'h5'

  // server.origin 是唯一真源：App 的 apiBase 与 H5 devServer 的代理目标都从它派生，
  // 因此换服务器时不需要分别改 APK 内置地址、代理目标、CORS 白名单。
  // 兼容旧配置：origin 缺失时依次回退旧字段，最后才是本机回环（无服务器、纯本地开发时）。
  const serverOrigin =
    server.origin || server.appApiOrigin || server.apiOrigin || server.webOrigin || ''
  const apiTarget = serverOrigin || `http://127.0.0.1:${appApiPort}`

  // 客户端 API 基础地址（http.ts 通过 __SM_API_BASE__ 读取）：
  //   H5（yarn dev:h5）→ 空串：resolveUrl 走相对路径，由下方 server.proxy 转发到 apiTarget，
  //        浏览器侧同源无跨域，WebAudio（audio crossOrigin='anonymous'）也不会被跨域污染。
  //   App（dev 与 release 一致）→ 绝对地址 apiTarget：代码跑在手机 WebView 里，
  //        localhost 指向手机本身，必须给一个可达的对外地址。
  //   运行时覆盖：App 设置页填写的 sm-server-base 始终优先于构建期内置值（见 http.ts）。
  // dev 与 release 合并后不再按 mode 区分，消除了「调试与打包地址不一致」的漂移。
  const apiBase = isH5 ? '' : apiTarget

  return {
    plugins: [uni(), watchHostsPlugin()],
    // H5 dev server：App 端走 watch 构建不使用 dev server，此配置只在 uni -p h5 时生效。
    // host: true 绑定 0.0.0.0，允许手机浏览器访问（移动端浏览器验证 WebAudio/自动播放策略）。
    // 代理目标 = server.origin（与 App 同一个地址，换服务器自动跟随）；
    // origin 未配置时才回退本机回环 127.0.0.1（纯本地、无服务器场景）。
    // 不再使用 local.lanIp：局域网 IP 随 DHCP / 网卡变化，曾导致 ECONNREFUSED 500。
    server: isH5
      ? {
          host: true,
          proxy: {
            '/api': { target: apiTarget, changeOrigin: true },
            // 健康探针同样要代理：否则 H5 的 SPA fallback 会用 index.html 返回 200，
            // 让 App 设置页「测试连通」在后端根本不可达时误报成功。
            '/health': { target: apiTarget, changeOrigin: true },
          },
        }
      : undefined,
    // 平台差异别名：
    // - App 端（v3 service→view 渲染桥）不支持 <svg>（lucide 图标会全部隐形），
    //   用本地 shim（view + CSS mask + 内联 SVG data-URI）整体替换 lucide-vue-next；
    //   shim 由 scripts/generate-lucide-shim.mjs 生成（yarn gen:icons）。
    // - H5 端（uni -p h5 浏览器预览，本工程仅用于 UI 预览、不发版）：新编译器下
    //   uni-h5-vite 的 resolveId 未把裸 'vue' 重定向到定制运行时，导致
    //   uni-app.es.js 取不到 injectHook 导出而白屏；此处显式别名兜底。
    //   App 构建不受影响（UNI_PLATFORM 恒非 h5）。
    resolve: {
      alias: {
        ...(isH5
          ? { vue: fileURLToPath(new URL('./node_modules/@dcloudio/uni-h5-vue/dist/vue.runtime.esm.js', import.meta.url)) }
          : {}),
        ...(isAppPlatform
          ? { 'lucide-vue-next': fileURLToPath(new URL('./src/utils/lucide-shim.ts', import.meta.url)) }
          : {}),
      },
    },
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
