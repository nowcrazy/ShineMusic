// locale/config.ts
// 语言全局配置：默认语言、支持语言注册表、系统语言匹配规则集中在一处。
// 新增语言只需两步：
//   ① 在 LOCALE_REGISTRY 增加条目（可同时配置系统语言推断前缀）
//   ② 在 messages.ts 增加对应语言包并注册到 allMessages
// 其余代码（utils / index / LanguageSwitcher）全部从本配置派生，无需改动。

/** 默认（回退）语言：无存储偏好且无法从系统语言推断时使用。 */
export const DEFAULT_LOCALE = 'zh-CN'

/** 语言元数据。code 同时是 messages.ts 语言包的 key。 */
export interface LocaleMeta {
  /** BCP 47 语言代码 */
  code: string
  /** 母语显示名：固定不翻译，保证用户在任何语言下都能识别并找回自己的语言 */
  nativeName: string
  /** 系统语言推断：navigator.language / uni.getSystemInfoSync().language 的前缀匹配列表 */
  matchPrefix: string[]
}

/**
 * 支持的语言注册表：切换器选项、存储校验、系统语言推断全部由此派生。
 * 顺序即切换器展示顺序。
 */
export const LOCALE_REGISTRY: LocaleMeta[] = [
  { code: 'zh-CN', nativeName: '简体中文', matchPrefix: ['zh'] },
  { code: 'en', nativeName: 'English', matchPrefix: ['en'] },
  { code: 'ja', nativeName: '日本語', matchPrefix: ['ja'] },
]

/** 支持的语言代码列表（派生自注册表，勿手改）。 */
export const SUPPORTED = LOCALE_REGISTRY.map((locale) => locale.code)

/** 语言代码类型（派生自注册表，勿手改）。 */
export type Locale = (typeof LOCALE_REGISTRY)[number]['code']

/** 按系统语言字符串推断界面语言：遍历注册表前缀匹配，未命中回退默认语言。 */
export function matchSystemLocale(systemLanguage: string): Locale {
  const normalized = systemLanguage.toLowerCase()
  const hit = LOCALE_REGISTRY.find((locale) =>
    locale.matchPrefix.some((prefix) => normalized.startsWith(prefix.toLowerCase())),
  )
  return (hit?.code as Locale) ?? DEFAULT_LOCALE
}

export function isSupported(locale: string): locale is Locale {
  return SUPPORTED.includes(locale)
}
