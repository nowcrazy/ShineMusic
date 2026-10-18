/**
 * 主题服务：管理应用外观主题（四套预设，可扩展）。
 *
 * 工作原理：
 * - 所有页面样式只引用 CSS 令牌变量（--sm-*），令牌值定义在 App.vue 的全局样式中，
 *   按 <html data-sm-theme="<id>"> 选择器分主题声明。
 * - 因此切换主题只需改写 document.documentElement 上的 data-sm-theme 属性，
 *   无需重新渲染任何页面即可整体换肤。
 * - 当前主题持久化在本地存储，App.vue onLaunch 时调用 initTheme() 恢复。
 *
 * 新增主题步骤：
 * 1. 在 App.vue 中追加一个 `:root[data-sm-theme='<id>'] { ... }` 令牌块；
 * 2. 在下方 THEMES 数组中追加一条主题元数据（含色卡预览色）。
 */
import { ref } from 'vue'

/** 主题 id 联合类型 */
export type ThemeId = 'lime' | 'glass' | 'cream' | 'night'

/** 主题元数据（用于主题选择页展示） */
export interface ThemeMeta {
  /** 主题标识，对应 <html data-sm-theme="..."> 的取值 */
  id: ThemeId
  /** 主题名称 */
  name: string
  /** 一句话描述 */
  description: string
  /** 明暗属性，供选择页标注 */
  tone: '浅色' | '深色'
  /** 色卡预览：底色 / 卡片面 / 强调色 / 文字色 */
  swatch: { bg: string; surface: string; accent: string; text: string }
}

/** 内置主题列表（顺序即选择页展示顺序，glass 为默认） */
export const THEMES: ThemeMeta[] = [
  {
    id: 'lime',
    name: '绿意精致',
    description: '米白底 + 青柠绿，清爽通透，适合白天使用',
    tone: '浅色',
    swatch: { bg: '#f7f8f4', surface: '#ffffff', accent: '#c8f36a', text: '#23261f' },
  },
  {
    id: 'glass',
    name: '玻璃质感',
    description: '深色底 + 彩色光晕 + 毛玻璃，沉浸有质感',
    tone: '深色',
    swatch: { bg: '#10131a', surface: 'rgba(255,255,255,.16)', accent: '#5be07a', text: '#f2f5f0' },
  },
  {
    id: 'cream',
    name: '暖调奶油',
    description: '米杏底 + 陶土橙，温暖柔和，大圆角留白',
    tone: '浅色',
    swatch: { bg: '#faf4ea', surface: '#ffffff', accent: '#e8a87c', text: '#3a2f24' },
  },
  {
    id: 'night',
    name: '暗夜电台',
    description: '深墨绿底 + 荧光青柠，夜间护眼，微光氛围',
    tone: '深色',
    swatch: { bg: '#14170f', surface: 'rgba(255,255,255,.14)', accent: '#c8f36a', text: '#eef3e2' },
  },
]

/** 默认主题 */
export const DEFAULT_THEME: ThemeId = 'glass'

/** 本地存储键 */
const STORAGE_KEY = 'sm-theme'

/** 当前主题（响应式，供界面展示选中态） */
export const currentTheme = ref<ThemeId>(DEFAULT_THEME)

/** 校验任意字符串是否为受支持的主题 id */
function isThemeId(value: unknown): value is ThemeId {
  return THEMES.some((theme) => theme.id === value)
}

/**
 * 把主题写入 <html data-sm-theme>。
 * 仅 H5 端可操作 document；其它端由各端自定义导航栏读取 currentTheme 自行适配。
 */
function writeToDocument(id: ThemeId) {
  // #ifdef H5
  if (typeof document !== 'undefined') {
    document.documentElement.setAttribute('data-sm-theme', id)
  }
  // #endif
}

/**
 * 切换主题并持久化。
 * @param id 目标主题 id；非法值会被忽略
 */
export function applyTheme(id: ThemeId) {
  if (!isThemeId(id)) return
  currentTheme.value = id
  writeToDocument(id)
  try {
    uni.setStorageSync(STORAGE_KEY, id)
  } catch {
    // 存储不可用时忽略：本次会话内主题仍然生效
  }
}

/**
 * 启动时恢复主题：读取本地存储，非法或缺失时回退到默认主题。
 * 应在 App.vue onLaunch 中尽早调用，避免首屏出现默认主题闪烁。
 */
export function initTheme() {
  let stored: unknown = ''
  try {
    stored = uni.getStorageSync(STORAGE_KEY)
  } catch {
    stored = ''
  }
  const id = isThemeId(stored) ? stored : DEFAULT_THEME
  currentTheme.value = id
  writeToDocument(id)
}
