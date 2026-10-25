/**
 * 从 App 工程（ShineMusicApp）导入页面/组件/服务/语言包到 Web 工程。
 *
 * 这是一次性迁移工具：导入后的代码由 ShineMusicWeb 独立维护，
 * 后续两端按各自需求演进，不再自动同步。
 */
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const ROOT = path.resolve(HERE, '../..')
const APP_SRC = path.join(ROOT, 'ShineMusicApp', 'src')
const WEB_SRC = path.resolve(HERE, '..', 'src')

const DIRS = ['components', 'locale', 'pages', 'services', 'types', 'utils']
const SKIP_FILES = new Set(['types/env.d.ts', 'utils/lucide-shim.ts', 'utils/web-api-shim.ts'])

function transform(code, relativePath) {
  let out = code
  // uni 的点击事件 → 浏览器 click
  out = out.replace(/@tap/g, '@click')
  // uni 的 image 组件 → 原生 img
  out = out.replace(/<image(\s|>)/g, '<img$1').replace(/<\/image>/g, '</img>')
  return out
}

function ensureDir(dir) {
  fs.mkdirSync(dir, { recursive: true })
}

function walk(dir, base) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const absolute = path.join(dir, entry.name)
    const relative = path.relative(base, absolute).split(path.sep).join('/')
    if (entry.isDirectory()) {
      walk(absolute, base)
      continue
    }
    if (SKIP_FILES.has(relative)) continue
    if (!/\.(vue|ts|scss|json)$/.test(entry.name)) continue
    const content = fs.readFileSync(absolute, 'utf8')
    const next = transform(content, relative)
    const target = path.join(WEB_SRC, relative)
    ensureDir(path.dirname(target))
    fs.writeFileSync(target, next)
    copied += 1
  }
}

let copied = 0
for (const dir of DIRS) {
  const source = path.join(APP_SRC, dir)
  if (!fs.existsSync(source)) continue
  walk(source, APP_SRC)
}

// ---- 全局样式：从 App.vue 的 <style> 块抽取主题令牌与公共样式 ----
const appVue = fs.readFileSync(path.join(APP_SRC, 'App.vue'), 'utf8')
const styleMatch = /<style lang="scss">([\s\S]*?)<\/style>/.exec(appVue)
if (!styleMatch) throw new Error('App.vue 中未找到全局样式块')

let globalStyle = styleMatch[1]
// 条件编译注释在 SCSS 中保留内容即可（H5 分支本就是我们要的）
globalStyle = globalStyle.replace(/\/\*\s*#ifdef H5\s*\*\//g, '').replace(/\/\*\s*#endif\s*\*\//g, '')
// uni 的 page 根节点 → Web 的 #app
globalStyle = globalStyle.replace(/(^|[\s,}])page(?=[\s,{])/g, '$1#app')
// 桌面端放宽限宽（App 端 720px 的折叠屏约束不适用于 Web）
globalStyle = globalStyle.replace(/max-width:\s*720px/g, 'max-width: 1180px')
// H5 原生导航栏选择器在 Web 端不存在，直接移除
globalStyle = globalStyle.replace(/\.uni-page-head[^{]*\{[^}]*\}/g, '')

const webBase = `/* ============================================================================
 * Web 端基础重置与尺寸基准
 * ----------------------------------------------------------------------------
 * 1rpx 在 Web 端统一换算为 CSS 变量 --sm-rpx：
 *   - 桌面（≥900px）：固定 0.5px，保持与手机端一致的 14~16px 正文观感；
 *   - 窄屏（<900px）：按视口宽度等比缩放，等价 uni-app H5 的 rpx 行为。
 * 构建期由 vite 插件把源码中的 rpx 转成 calc(N * var(--sm-rpx))。
 * ========================================================================== */
:root {
  --sm-rpx: 0.5px;
}

@media (max-width: 899px) {
  :root {
    --sm-rpx: calc(100vw / 750);
  }
}

html,
body {
  margin: 0;
  padding: 0;
  min-height: 100%;
  -webkit-font-smoothing: antialiased;
  text-rendering: optimizeLegibility;
}

#app {
  max-width: 1180px;
  margin: 0 auto;
  min-height: 100vh;
  position: relative;
}

view,
text,
scroll-view,
page {
  box-sizing: border-box;
}

view {
  display: block;
}

text {
  display: inline;
}

scroll-view {
  display: block;
  overflow: hidden;
}

scroll-view[scroll-y] {
  overflow-y: auto;
  -webkit-overflow-scrolling: touch;
}

scroll-view[scroll-x] {
  overflow-x: auto;
  overflow-y: hidden;
  white-space: nowrap;
  -webkit-overflow-scrolling: touch;
}

img {
  display: block;
  max-width: 100%;
}

img[mode='aspectFill'] {
  object-fit: cover;
}

img[mode='aspectFit'] {
  object-fit: contain;
}

input,
textarea {
  font-family: inherit;
}

::selection {
  background: var(--sm-accent-soft);
}

`

ensureDir(path.join(WEB_SRC, 'styles'))
fs.writeFileSync(path.join(WEB_SRC, 'styles', 'global.scss'), `${webBase}${globalStyle}`)

console.log(`[import-from-app] 已迁移 ${copied} 个文件，并生成 src/styles/global.scss`)
