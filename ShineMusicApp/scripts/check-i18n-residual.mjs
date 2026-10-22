// scripts/check-i18n-residual.mjs
// i18n 防回退闸门：扫描 src 中的 UI 中文串（去除注释后），与基线比对。
// - 基线（i18n-residual-baseline.json）= 有意保留的「逻辑匹配值」白名单
//   （tab 比较值、风格/mood 匹配数组、主题名回退数据等，展示层走 key 映射）。
// - 出现基线之外的新增 UI 中文串 → 退出码 1，阻止提交/CI。
// - 用法：
//     node scripts/check-i18n-residual.mjs           # 检查（CI / 提交前）
//     node scripts/check-i18n-residual.mjs --update  # 重新生成基线（确认新增均为有意保留后使用）
import { readFileSync, writeFileSync, existsSync, readdirSync, statSync } from 'node:fs'
import { join, relative, extname, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = dirname(fileURLToPath(import.meta.url))
const SRC = join(__dirname, '..', 'src')
const BASELINE = join(__dirname, 'i18n-residual-baseline.json')
const UPDATE = process.argv.includes('--update')

const CJK = /[\u4e00-\u9fff]/
const SKIP_DIRS = new Set(['locale', 'node_modules', 'dist', 'unpackage'])
const SKIP_FILES = (f) => f.endsWith('.generated.ts') || f === 'lucide-shim.ts'

function walk(dir, acc = []) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name)
    if (statSync(p).isDirectory()) {
      if (!SKIP_DIRS.has(name)) walk(p, acc)
    } else if (['.vue', '.ts'].includes(extname(name)) && !SKIP_FILES(name)) acc.push(p)
  }
  return acc
}

// 去注释：块注释整段抹平（保留换行数）、行注释粗略移除、vue 模板注释移除。
// 不处理「字符串内伪注释」——扫描用途可接受，新增误报时人工进基线。
function stripComments(code, isVue) {
  let out = code.replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, ' '))
  out = out.replace(/(^|[^:"'`\\])\/\/[^\n]*/g, '$1')
  if (isVue) out = out.replace(/<!--[\s\S]*?-->/g, (m) => m.replace(/[^\n]/g, ' '))
  return out
}

function scan() {
  const results = {}
  for (const file of walk(SRC)) {
    const rel = relative(SRC, file).replace(/\\/g, '/')
    const src = stripComments(readFileSync(file, 'utf8'), extname(file) === '.vue')
    const hits = new Set()
    const strRe = /'([^'\n]*)'|"([^"\n]*)"|`([^`]*)`/g
    let m
    while ((m = strRe.exec(src))) {
      const text = (m[1] ?? m[2] ?? m[3] ?? '').trim()
      if (text && CJK.test(text)) hits.add(text)
    }
    if (extname(file) === '.vue') {
      const textRe = />([^<>{}]*[\u4e00-\u9fff][^<>{}]*)</g
      while ((m = textRe.exec(src))) {
        const text = m[1].trim()
        if (text) hits.add(text)
      }
    }
    if (hits.size) results[rel] = [...hits].sort()
  }
  return results
}

const current = scan()

if (UPDATE) {
  writeFileSync(BASELINE, JSON.stringify(current, null, 2) + '\n')
  const total = Object.values(current).reduce((n, list) => n + list.length, 0)
  console.log(`基线已更新：${total} 条（${Object.keys(current).length} 个文件）。请人工确认新增条目均为有意保留的逻辑值。`)
  process.exit(0)
}

if (!existsSync(BASELINE)) {
  console.error('缺少基线文件 scripts/i18n-residual-baseline.json，先运行 --update 生成。')
  process.exit(1)
}

const baseline = JSON.parse(readFileSync(BASELINE, 'utf8'))
const violations = []
for (const [file, texts] of Object.entries(current)) {
  const allowed = new Set(baseline[file] ?? [])
  for (const text of texts) {
    if (!allowed.has(text)) violations.push({ file, text })
  }
}

if (violations.length) {
  console.error(`发现 ${violations.length} 条未走语言包的 UI 中文串（新增 UI 文案必须进 locale 语言包并用 $t()/t() 接线）：`)
  for (const { file, text } of violations) console.error(`  ${file}: ${text.slice(0, 80)}`)
  console.error('若确属有意保留的逻辑匹配值，请更新基线：yarn check:i18n --update')
  process.exit(1)
}

const stale = []
for (const [file, texts] of Object.entries(baseline)) {
  const cur = new Set(current[file] ?? [])
  for (const text of texts) if (!cur.has(text)) stale.push({ file, text })
}
if (stale.length) {
  console.log(`提示：基线中 ${stale.length} 条已不在源码中（可运行 --update 清理基线）。`)
}
console.log('i18n 残留检查通过：无新增未接线 UI 文案。')
