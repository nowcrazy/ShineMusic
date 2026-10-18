import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig, type Plugin } from 'vite'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)
const uni = require('@dcloudio/vite-plugin-uni').default

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
export default defineConfig(({ mode }) => {
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

  // 客户端 API 基础地址（http.ts 通过 __SM_API_BASE__ 读取）：
  //   dev        → 空串，走相对路径 + devServer 代理
  //   app        → 真机可访问的完整地址
  //   production → 同域留空（Nginx 反代）；前后端不同域时用 server.apiOrigin
  const apiBase =
    mode === 'app'
      ? `http://${lanIp}:${appApiPort}`
      : mode === 'production'
        ? server.apiOrigin && server.apiOrigin !== server.webOrigin
          ? server.apiOrigin
          : ''
        : ''

  return {
    plugins: [uni(), watchHostsPlugin()],
    define: {
      __SM_API_BASE__: JSON.stringify(apiBase),
    },
    server: {
      proxy: {
        '/api': { target: apiTarget, changeOrigin: true },
        '/health': { target: apiTarget, changeOrigin: true },
      },
    },
  }
})
