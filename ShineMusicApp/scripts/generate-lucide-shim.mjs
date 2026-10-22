/**
 * 生成 src/utils/lucide-shim.ts —— lucide-vue-next 的 App 端替代实现。
 *
 * 背景：uni-app App 端（v3 service→view 渲染桥）用 HTML namespace 创建元素，
 * <svg>/<path> 不会被渲染，导致 lucide 图标在 App 端全部隐形（H5 正常）。
 * 本脚本从 lucide 包内提取每个图标的节点字面量，生成「view + CSS mask + 内联 SVG data-URI」
 * 的等价组件。仅在 APP 构建经 vite alias 替换 lucide-vue-next（见 vite.config.ts）。
 *
 * 运行：yarn gen:icons （新增/更换图标后需重新执行）
 */
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const SRC_DIR = path.join(ROOT, 'src')
const ICONS_DIR = path.join(ROOT, 'node_modules', 'lucide-vue-next', 'dist', 'esm', 'icons')
const OUT_FILE = path.join(SRC_DIR, 'utils', 'lucide-shim.ts')

/** 递归收集 src 下的 vue/ts 源文件 */
function collectFiles(dir, files = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name)
    if (entry.isDirectory()) collectFiles(full, files)
    else if (/\.(vue|ts)$/.test(entry.name)) files.push(full)
  }
  return files
}

/** PascalCase → kebab-case（Trash2 → trash-2、UserRound → user-round） */
function toKebab(name) {
  return name
    .replace(/([a-z0-9])([A-Z])/g, '$1-$2')
    .replace(/([A-Za-z])(\d)/g, '$1-$2')
    .toLowerCase()
}

const files = collectFiles(SRC_DIR)
const names = new Set()
const importRe = /import\s*\{([^}]+)\}\s*from\s*['"]lucide-vue-next['"]/g

for (const file of files) {
  const text = fs.readFileSync(file, 'utf8')
  for (const match of text.matchAll(importRe)) {
    for (const raw of match[1].split(',')) {
      const name = raw.trim().split(/\s+as\s+/)[0]
      if (name) names.add(name)
    }
  }
}

if (!names.size) {
  console.error('未在 src 中找到 lucide-vue-next 导入，未生成 shim。')
  process.exit(1)
}

// 解析主入口的别名导出（如 AlertCircle → circle-alert）：`export { default as X, ... } from './icons/x.js'`
const MAIN_ENTRY = path.join(ROOT, 'node_modules', 'lucide-vue-next', 'dist', 'esm', 'lucide-vue-next.js')
const aliasMap = new Map()
const exportRe = /export\s*\{([^}]*)\}\s*from\s*'\.\/icons\/([^'.]+)\.js'/g
const mainText = fs.readFileSync(MAIN_ENTRY, 'utf8')
for (const match of mainText.matchAll(exportRe)) {
  for (const raw of match[1].split(',')) {
    const name = raw.trim().replace(/^default\s+as\s+/, '')
    if (name && !name.startsWith('Lucide') && !name.endsWith('Icon')) aliasMap.set(name, match[2])
  }
}

const sorted = [...names].sort()
const entries = []
const missing = []

for (const name of sorted) {
  const kebab = toKebab(name)
  const base = fs.existsSync(path.join(ICONS_DIR, `${kebab}.js`)) ? kebab : aliasMap.get(name)
  if (!base || !fs.existsSync(path.join(ICONS_DIR, `${base}.js`))) {
    missing.push(name)
    continue
  }
  const text = fs.readFileSync(path.join(ICONS_DIR, `${base}.js`), 'utf8')
  const start = text.indexOf('[', text.indexOf('createLucideIcon('))
  const end = text.lastIndexOf(']')
  if (start < 0 || end <= start) {
    missing.push(name)
    continue
  }
  entries.push({ name, data: text.slice(start, end + 1) })
}

if (missing.length) {
  console.error(`以下图标在 lucide-vue-next 中不存在：${missing.join(', ')}`)
  process.exit(1)
}

const header = `/**
 * lucide-vue-next 的 App 端替代实现（由 scripts/generate-lucide-shim.mjs 自动生成，勿手改）。
 *
 * 背景：uni-app App 端（v3 service→view 渲染桥）用 HTML namespace 创建元素，<svg>/<path>
 * 不渲染，lucide 图标在 App 全部隐形（H5 正常）。本文件用 view + CSS mask + 内联 SVG
 * data-URI 实现等价图标；仅在 APP 构建经 vite alias 替换 lucide-vue-next（见 vite.config.ts）。
 * 新增/更换图标后运行 yarn gen:icons 重新生成。
 *
 * 支持：size（数字/字符串）、fill="currentColor"（实心）、stroke-width、class/style 透传。
 */
import { computed, defineComponent, h } from 'vue'

type LucideNode = [tag: string, attrs: Record<string, string>]

/** 与 lucide defaultAttributes 保持一致（24 视窗、stroke 2、圆头圆角） */
const DEFAULT_VIEWBOX = '0 0 24 24'
const DEFAULT_STROKE_WIDTH = 2

function classToString(value: unknown): string {
  if (!value) return ''
  if (typeof value === 'string') return value
  if (Array.isArray(value)) return value.map(classToString).filter(Boolean).join(' ')
  if (typeof value === 'object') {
    return Object.entries(value as Record<string, unknown>)
      .filter(([, active]) => active)
      .map(([key]) => key)
      .join(' ')
  }
  return ''
}

function serialize(children: LucideNode[], filled: boolean, strokeWidth: number): string {
  const inner = children
    .map(([tag, attrs]) => {
      const rest = Object.entries(attrs)
        .filter(([key]) => key !== 'key')
        .map(([key, value]) => \`\${key}="\${value}"\`)
        .join(' ')
      return \`<\${tag} \${rest}/>\`
    })
    .join('')
  const svg =
    \`<svg xmlns="http://www.w3.org/2000/svg" viewBox="\${DEFAULT_VIEWBOX}" fill="\${filled ? '#000' : 'none'}"\` +
    \` stroke="#000" stroke-width="\${strokeWidth}" stroke-linecap="round" stroke-linejoin="round">\${inner}</svg>\`
  return \`url("data:image/svg+xml,\${encodeURIComponent(svg)}")\`
}

/** 生成与 lucide 同名组件等价的 view+mask 图标组件 */
function createShimIcon(name: string, children: LucideNode[]) {
  return defineComponent({
    name,
    inheritAttrs: false,
    props: { size: { type: [Number, String], default: 24 } },
    setup(props, { attrs }) {
      const attrRecord = attrs as Record<string, unknown>
      const filled = computed(() => attrRecord.fill === 'currentColor')
      const strokeWidth = computed(() => {
        const raw = attrRecord['stroke-width']
        const num = typeof raw === 'string' ? Number(raw) : (raw as number | undefined)
        return typeof num === 'number' && Number.isFinite(num) ? num : DEFAULT_STROKE_WIDTH
      })
      const maskImage = computed(() => serialize(children, filled.value, strokeWidth.value))
      const dimension = computed(() =>
        typeof props.size === 'number' ? \`\${props.size}px\` : String(props.size),
      )
      return () =>
        h('view', {
          class: \`lucide-shim \${classToString(attrRecord.class)}\`.trim(),
          style: {
            width: dimension.value,
            height: dimension.value,
            display: 'inline-block',
            flexShrink: 0,
            verticalAlign: 'middle',
            backgroundColor: 'currentColor',
            WebkitMaskImage: maskImage.value,
            maskImage: maskImage.value,
            WebkitMaskRepeat: 'no-repeat',
            maskRepeat: 'no-repeat',
            WebkitMaskPosition: 'center',
            maskPosition: 'center',
            WebkitMaskSize: '100% 100%',
            maskSize: '100% 100%',
          },
        })
    },
  })
}
`

const exports = entries
  .map(({ name, data }) => `export const ${name} = createShimIcon('${name}', ${data} as LucideNode[])`)
  .join('\n')

fs.writeFileSync(OUT_FILE, `${header}\n${exports}\n`, 'utf8')
console.log(`lucide-shim.ts 已生成：${entries.length} 个图标 → ${path.relative(ROOT, OUT_FILE)}`)
