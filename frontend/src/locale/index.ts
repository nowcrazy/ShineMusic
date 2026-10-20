// locale/index.ts
// i18n 单例工厂 + 语言切换 reactive 状态。
// uni-app 3.x + Vue3 场景下，vue-i18n@9 的 legacy: false 模式可用组合式 API useI18n()。
// 支持语言、默认语言等全局配置见 locale/config.ts（唯一定义处）。
import { createI18n, type I18n, type LocaleMessageDictionary, type VueMessageType } from 'vue-i18n'
import { allMessages } from './messages'
import { loadLocale, saveLocale, isSupported } from './utils'
import { DEFAULT_LOCALE, SUPPORTED, type Locale } from './config'

export type { Locale }

let _i18n: I18n | null = null

/** 创建（或复用）i18n 实例。main.ts 里只调一次。 */
export function createAppI18n(): I18n {
  if (_i18n) return _i18n
  const initial = loadLocale()
  _i18n = createI18n({
    legacy: false, // composition API: $t still works in template, useI18n() in setup
    locale: initial,
    fallbackLocale: DEFAULT_LOCALE,
    messages: allMessages as Record<string, LocaleMessageDictionary<VueMessageType>>,
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
  if (_i18n!.global.locale.value === locale) return
  _i18n!.global.locale.value = locale
  saveLocale(locale)
}

/** 当前语言 getter。 */
export function getLocale(): Locale {
  return (_i18n?.global.locale.value as Locale) ?? loadLocale()
}

export { SUPPORTED, DEFAULT_LOCALE as FALLBACK, isSupported, LOCALE_REGISTRY } from './config'

// 快捷：用于 setNavigationBarTitle 的翻译函数（在 onShow 里调用，不在组件内）
export function t(key: string, params?: Record<string, unknown>): string {
  if (!_i18n) createAppI18n()
  const result = _i18n!.global.t(key, params ?? {})
  return typeof result === 'string' ? result : key
}
