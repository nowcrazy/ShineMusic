import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig, type Plugin } from 'vite'
import vue from '@vitejs/plugin-vue'

const CONFIG_DIR = path.dirname(fileURLToPath(import.meta.url))
const HOSTS_FILE = path.resolve(CONFIG_DIR, '../config/hosts.json')

interface HostsConfig {
  local?: {
    lanIp?: string
    webPort?: number
    apiPort?: number
    apiHost?: 'localhost' | 'lan'
  }
  server?: {
    webOrigin?: string
    apiOrigin?: string
    appApiOrigin?: string
  }
}

function readHosts(): HostsConfig {
  try {
    return JSON.parse(fs.readFileSync(HOSTS_FILE, 'utf8')) as HostsConfig
  } catch {
    return {}
  }
}

/** uni-app 条件编译注释：按 Web 平台（H5 = true，其余 false）裁剪源码。 */
function stripConditionalCompilation(code: string): string {
  const flags: Record<string, boolean> = {
    H5: true,
    'WEB': true,
    APP: false,
    'APP-PLUS': false,
    'APP-HARMONY': false,
    'APP-ANDROID': false,
    'APP-IOS': false,
    MP: false,
    'MP-WEIXIN': false,
  }
  const lines = code.split(/\r?\n/)
  const stack: boolean[] = []
  const out: string[] = []
  for (const line of lines) {
    const directive = line.trim()
    const ifdef = /^\/\/\s*#ifdef\s+(.+)$/.exec(directive) ?? /^\/\*\s*#ifdef\s+(.+?)\s*\*\/$/.exec(directive)
    const ifndef = /^\/\/\s*#ifndef\s+(.+)$/.exec(directive) ?? /^\/\*\s*#ifndef\s+(.+?)\s*\*\/$/.exec(directive)
    const endif = /^\/\/\s*#endif$/.test(directive) || /^\/\*\s*#endif\s*\*\/$/.test(directive)
    const elseLine = /^\/\/\s*#else$/.test(directive) || /^\/\*\s*#else\s*\*\/$/.test(directive)

    if (ifdef) {
      const matched = ifdef[1].split('||').map((item) => item.trim()).some((item) => flags[item] === true)
      stack.push(matched)
      continue
    }
    if (ifndef) {
      const matched = ifndef[1].split('||').map((item) => item.trim()).every((item) => flags[item] !== true)
      stack.push(matched)
      continue
    }
    if (endif) {
      stack.pop()
      continue
    }
    if (elseLine) {
      if (stack.length > 0) stack[stack.length - 1] = !stack[stack.length - 1]
      continue
    }
    if (stack.every(Boolean)) out.push(line)
  }
  return out.join('\n')
}

/** 把 rpx 换算成基于 CSS 变量的响应式尺寸；浏览器原生不认识 rpx。 */
function convertRpx(code: string): string {
  return code.replace(/(-?\d*\.?\d+)rpx/g, (_, value: string) => `calc(${value} * var(--sm-rpx))`)
}

function uniCompatPlugin(): Plugin {
  return {
    name: 'sm-uni-compat',
    enforce: 'pre',
    transform(code, id) {
      if (!/\.(vue|ts|js|scss|css)($|\?)/.test(id)) return undefined
      if (id.includes('/node_modules/')) return undefined
      const stripped = stripConditionalCompilation(code)
      const converted = convertRpx(stripped)
      if (converted === code) return undefined
      return { code: converted, map: null }
    },
  }
}

export default defineConfig(({ command, mode }) => {
  const hosts = readHosts()
  const local = hosts.local ?? {}
  const server = hosts.server ?? {}
  const apiPort = local.apiPort ?? 5180
  const apiHost = local.apiHost === 'lan' && local.lanIp ? local.lanIp : 'localhost'

  // 开发：走 Vite 代理，天然同源；生产：独立站点直连 API（CORS 白名单由后端放行）。
  const apiBase =
    mode === 'production' ? server.apiOrigin || server.appApiOrigin || '' : ''

  return {
    base: '/',
    plugins: [
      uniCompatPlugin(),
      vue({
        template: {
          compilerOptions: {
            isCustomElement: (tag) => ['view', 'text', 'scroll-view', 'page'].includes(tag),
          },
        },
      }),
    ],
    resolve: {
      alias: {
        '@': path.resolve(CONFIG_DIR, 'src'),
        '@dcloudio/uni-app': path.resolve(CONFIG_DIR, 'src/platform/uni-app.ts'),
      },
    },
    define: {
      __SM_API_BASE__: JSON.stringify(apiBase),
      __VUE_I18N_FULL_INSTALL__: true,
      __INTLIFY_PROD_DEVTOOLS__: false,
      __VUE_I18N_LEGACY_API__: false,
    },
    server: {
      port: local.webPort ?? 5174,
      proxy: {
        '/api': { target: `http://${apiHost}:${apiPort}`, changeOrigin: true },
        '/health': { target: `http://${apiHost}:${apiPort}`, changeOrigin: true },
      },
    },
    build: {
      outDir: 'dist',
      emptyOutDir: true,
      chunkSizeWarningLimit: 1600,
    },
    css: {
      preprocessorOptions: {
        scss: {
          quietDeps: true,
        },
      },
    },
  }
})
