// locale/index.ts
// i18n 单例工厂 + 语言切换 reactive 状态。
// uni-app 3.x + Vue3 场景下，vue-i18n@9 的 legacy: false 模式可用组合式 API useI18n()。
// 支持语言、默认语言等全局配置见 locale/config.ts（唯一定义处）。
import { createI18n, type Composer, type DefineLocaleMessage, type I18n } from 'vue-i18n'
import { allMessages } from './messages'
import { loadLocale, saveLocale, isSupported } from './utils'
import { DEFAULT_LOCALE, SUPPORTED, type Locale } from './config'

export type { Locale }

let _i18n: I18n | null = null

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
  _i18n = createI18n({
    legacy: false, // composition API: $t still works in template, useI18n() in setup
    locale: initial,
    fallbackLocale: DEFAULT_LOCALE,
    // vue-i18n@9.14 的 messages 选项是 schema 品牌类型，且其 d.ts 引用的 LocaleMessage
    // 在提升的 @intlify/core-base 副本中缺失（skipLibCheck 掩盖），自定义 LocaleMessages
    // 与它无类型重叠（TS2352/TS2769），经 unknown 中转到默认 schema DefineLocaleMessage。
    messages: allMessages as unknown as Record<string, DefineLocaleMessage>,
    missingWarn: import.meta.env.DEV, // 只有 dev 告警缺失 key
    fallbackWarn: false,
  })
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
