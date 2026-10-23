// locale/index.ts
// i18n 单例工厂 + 语言切换 reactive 状态。
// uni-app 3.x + Vue3 场景下，vue-i18n@9 的 legacy: false 模式可用组合式 API useI18n()。
// 支持语言、默认语言等全局配置见 locale/config.ts（唯一定义处）。
import { createI18n, type Composer, type DefineLocaleMessage, type I18n } from 'vue-i18n'
import { allMessages } from './messages'
import { componentsMessages } from './messages/components'
import { homeMessages } from './messages/home'
import { browseMessages } from './messages/browse'
import { socialMessages } from './messages/social'
import { adminImportMessages } from './messages/adminImport'
import { adminManageMessages } from './messages/adminManage'
import { platformMessages } from './messages/platform'
import { loadLocale, saveLocale, isSupported } from './utils'
import { DEFAULT_LOCALE, SUPPORTED, type Locale } from './config'

export type { Locale }

let _i18n: I18n | null = null

type MessageTree = Record<string, unknown>

/** 递归合并普通语言包树（值只有字符串与纯对象），source 里的值优先。 */
function mergeTrees(target: MessageTree, source: MessageTree): MessageTree {
  const out: MessageTree = { ...target }
  for (const [key, value] of Object.entries(source)) {
    const prev = out[key]
    out[key] =
      prev && value && typeof prev === 'object' && typeof value === 'object'
        ? mergeTrees(prev as MessageTree, value as MessageTree)
        : value
  }
  return out
}

/** 已编译消息函数：intlify 对函数型消息直接调用，绕过内置消息编译器。 */
type MessageFunctionCtx = {
  /** 命名参数，现代版本为函数 (key) => value，旧版/自定义处理器可能是对象 */
  named?: ((key: string) => unknown) | Record<string, unknown>
  values?: ((key: string) => unknown) | Record<string, unknown>
}
type CompiledMessage = (ctx: MessageFunctionCtx) => string

const NAMED_PLACEHOLDER = /\{\s*([a-zA-Z_$][\w$]*)\s*\}/g
const AT_LITERAL = /\{\s*'@'\s*\}/g

/** 从消息上下文取命名参数，兼容函数型 named()（intlify）与对象型 values。 */
function lookupParam(ctx: MessageFunctionCtx | undefined, key: string): unknown {
  const named = ctx?.named
  if (typeof named === 'function') return named(key)
  const values = ctx?.values
  if (typeof values === 'function') return values(key)
  if (values && typeof values === 'object') return (values as Record<string, unknown>)[key]
  if (named && typeof named === 'object') return (named as Record<string, unknown>)[key]
  return undefined
}

/**
 * 把单个消息字符串编译为插值函数（{name} 命名插值 + {'@'} 字面量）。
 * 不做动态代码求值（new Function），因此不依赖 vue-i18n 的消息编译器。
 */
function compileLeaf(message: string): CompiledMessage {
  if (!message.includes('{')) return () => message
  return (ctx) => {
    return message.replace(AT_LITERAL, '@').replace(NAMED_PLACEHOLDER, (_, key: string) => {
      const value = lookupParam(ctx, key)
      return value == null ? '' : String(value)
    })
  }
}

/** 递归把消息树中的字符串全部预编译为函数。 */
function compileTree(node: unknown): unknown {
  if (typeof node === 'string') return compileLeaf(node)
  if (node && typeof node === 'object') {
    const out: Record<string, unknown> = {}
    for (const [key, value] of Object.entries(node as Record<string, unknown>)) {
      out[key] = compileTree(value)
    }
    return out
  }
  return node
}

/**
 * 主语言包（messages.ts）+ 各域补充模块合并。
 * 域模块（locale/messages/*.ts）扩展既有命名空间时逐层合并而非整体覆盖，
 * 与 messages.ts 既有 key 冲突时以域模块（逐字对齐源码）为准。
 * 合并完成后统一预编译为函数型消息：DCloud App 产物缺 new Function，
 * 默认编译器不可用且 messageCompiler 选项在 App 运行时副本中不生效，
 * 函数型消息是唯一与版本无关的渲染路径。
 */
function buildAllMessages(): Record<string, unknown> {
  const domains: Record<string, MessageTree>[] = [
    componentsMessages,
    homeMessages,
    browseMessages,
    socialMessages,
    adminImportMessages,
    adminManageMessages,
    platformMessages,
  ]
  const out: Record<string, unknown> = {}
  for (const [locale, tree] of Object.entries(allMessages)) {
    const merged = domains.reduce(
      (acc, domain) => mergeTrees(acc, domain[locale] ?? {}),
      tree as MessageTree,
    )
    out[locale] = compileTree(merged)
  }
  return out
}

/**
 * 取 global 作用域。createI18n(legacy: false) 返回的 I18n 泛型里 global
 * 是 Composer | VueI18n 联合类型（vue-i18n@9 类型缺陷），.value / .t() 无法直接访问，
 * 这里统一收窄为 Composer（legacy: false 下运行时必为 Composer）。
 */
function globalScope(): Composer {
  return _i18n!.global as Composer
}

/** 创建（或复用）i18n 实例。main.ts 里只调一次。 */
export function createAppI18n(): I18n {
  if (_i18n) return _i18n
  const initial = loadLocale()
  const options = {
    legacy: false, // composition API: useI18n() in setup
    // 组合模式下 globalInjection 默认 false，$t/$d/$n 不会注入 globalProperties，
    // 模板 $t 会报 _ctx.$t is not a function 导致首屏白屏（App/H5 同样中招），必须显式开启
    globalInjection: true,
    locale: initial,
    fallbackLocale: DEFAULT_LOCALE,
    // vue-i18n@9.14 的 messages 选项是 schema 品牌类型，且其 d.ts 引用的 LocaleMessage
    // 在提升的 @intlify/core-base 副本中缺失（skipLibCheck 掩盖），自定义 LocaleMessages
    // 与它无类型重叠（TS2352/TS2769），经 unknown 中转到默认 schema DefineLocaleMessage。
    messages: buildAllMessages() as unknown as Record<string, DefineLocaleMessage>,
    missingWarn: import.meta.env.DEV, // 只有 dev 告警缺失 key
    fallbackWarn: false,
  }
  _i18n = createI18n(options as unknown as Parameters<typeof createI18n>[0])
  return _i18n
}

/** 运行时切换语言。持久化存储，下次启动自动恢复。 */
export function setLocale(locale: string): void {
  if (!_i18n) {
    // 尚未初始化，先创建再切
    createAppI18n()
  }
  if (!isSupported(locale)) return
  const global = globalScope()
  if (global.locale.value === locale) return
  global.locale.value = locale
  saveLocale(locale)
}

/** 当前语言 getter。 */
export function getLocale(): Locale {
  if (!_i18n) return loadLocale()
  return (globalScope().locale.value as Locale) ?? loadLocale()
}

export { SUPPORTED, DEFAULT_LOCALE as FALLBACK, isSupported, LOCALE_REGISTRY } from './config'

// 快捷：用于 setNavigationBarTitle 的翻译函数（在 onShow 里调用，不在组件内）
export function t(key: string, params?: Record<string, unknown>): string {
  if (!_i18n) createAppI18n()
  const result = globalScope().t(key, params ?? {})
  return typeof result === 'string' ? result : key
}
