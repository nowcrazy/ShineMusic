// locale/utils.ts
// 语言设置持久化：uni.storage（多端统一封装）。
// 支持语言列表 / 默认语言 / 系统语言推断规则统一维护在 locale/config.ts，本文件只负责存取。
import { DEFAULT_LOCALE, isSupported, matchSystemLocale, type Locale } from './config'

const STORAGE_KEY = 'shine-music.locale'

/** 从存储读取之前的语言设置；没有则尝试从系统语言推断。 */
export function loadLocale(): Locale {
  try {
    const stored = uni.getStorageSync(STORAGE_KEY)
    if (isSupported(stored)) return stored
  } catch {
    // H5 端 uni.getStorageSync 兜底
  }

  // H5: navigator.language；App: uni.getSystemInfoSync().language
  let sys: string | undefined
  try {
    sys = (uni.getSystemInfoSync() as { language?: string }).language
  } catch {
    sys = typeof navigator !== 'undefined' ? navigator.language : undefined
  }

  if (sys) return matchSystemLocale(sys)

  return DEFAULT_LOCALE
}

/** 持久化语言设置。 */
export function saveLocale(locale: Locale): void {
  try {
    uni.setStorageSync(STORAGE_KEY, locale)
  } catch {
    // ignore
  }
}

export { isSupported }
export { DEFAULT_LOCALE as FALLBACK, SUPPORTED } from './config'
