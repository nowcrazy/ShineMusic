/**
 * 主题服务：管理应用外观主题（九套预设 + 用户自定义图片主题）。
 *
 * 工作原理：
 * - 所有页面样式只引用 CSS 令牌变量（--sm-*），令牌值定义在 App.vue 的全局样式中，
 *   按 <html data-sm-theme="<id>"> 选择器分主题声明。
 * - 因此切换主题只需改写 document.documentElement 上的 data-sm-theme 属性，
 *   无需重新渲染任何页面即可整体换肤。
 * - 预设主题持久化在本地存储，App.vue onLaunch 时调用 initTheme() 恢复。
 *
 * 自定义图片主题（custom）：
 * - 用户上传壁纸后由前端取色（services/color-extract）得到三色色板，
 *   配合可调的暗色遮罩，运行时动态生成一套「壁纸 + 玻璃拟态」令牌，
 *   以 <style id="sm-custom-theme"> 注入 <head>，随 data-sm-theme='custom' 生效；
 * - 壁纸二进制经带鉴权的请求取回后转为 objectURL 注入（CSS 背景无法携带请求头），
 *   objectURL 仅本次会话有效，跨会话由 services/user-theme 的 syncRemoteTheme 重新拉取；
 * - 色板/遮罩/启用状态持久化在本地存储（sm-theme-custom），多端同步以后端为准。
 *
 * 新增预设主题步骤：
 * 1. 在 App.vue 中追加一个 `:root[data-sm-theme='<id>'] { ... }` 令牌块；
 * 2. 在下方 THEMES 数组中追加一条主题元数据（含色卡预览色）。
 */
import { ref } from 'vue'
import { accentInk, hslToHex, hexToHsl, hueRotate, withAlpha } from './color-extract'

/** 主题 id 联合类型（custom 为运行时生成的自定义图片主题，不在 THEMES 列表中） */
export type ThemeId = 'lime' | 'glass' | 'cream' | 'night' | 'sunset' | 'violet' | 'ocean' | 'sakura' | 'oled' | 'custom'

/** 主题元数据（用于主题选择页展示） */
export interface ThemeMeta {
  /** 主题标识，对应 <html data-sm-theme="..."> 的取值 */
  id: ThemeId
  /** 主题名称（中文字面量，作为数据回退） */
  name: string
  /** 主题名称的多语言 key（展示处经 t()/\$t() 取当前语言文案） */
  nameKey: string
  /** 一句话描述（中文字面量，作为数据回退） */
  description: string
  /** 一句话描述的多语言 key */
  descKey: string
  /** 明暗属性（中文字面量，作为数据回退），供选择页标注 */
  tone: '浅色' | '深色'
  /** 明暗属性的多语言 key（themePage.toneLight / themePage.toneDark） */
  toneKey: string
  /** 色卡预览：底色 / 卡片面 / 强调色 / 文字色 */
  swatch: { bg: string; surface: string; accent: string; text: string }
}

/** 内置主题列表（顺序即选择页展示顺序，glass 为默认） */
export const THEMES: ThemeMeta[] = [
  {
    id: 'lime',
    name: '绿意精致',
    nameKey: 'theme.lime',
    description: '米白底 + 青柠绿，清爽通透，适合白天使用',
    descKey: 'theme.limeDesc',
    tone: '浅色',
    toneKey: 'themePage.toneLight',
    swatch: { bg: '#f7f8f4', surface: '#ffffff', accent: '#c8f36a', text: '#23261f' },
  },
  {
    id: 'glass',
    name: '玻璃质感',
    nameKey: 'theme.glass',
    description: '深色底 + 彩色光晕 + 毛玻璃，沉浸有质感',
    descKey: 'theme.glassDesc',
    tone: '深色',
    toneKey: 'themePage.toneDark',
    swatch: { bg: '#10131a', surface: 'rgba(255,255,255,.16)', accent: '#5be07a', text: '#f2f5f0' },
  },
  {
    id: 'cream',
    name: '暖调奶油',
    nameKey: 'theme.cream',
    description: '米杏底 + 陶土橙，温暖柔和，大圆角留白',
    descKey: 'theme.creamDesc',
    tone: '浅色',
    toneKey: 'themePage.toneLight',
    swatch: { bg: '#faf4ea', surface: '#ffffff', accent: '#e8a87c', text: '#3a2f24' },
  },
  {
    id: 'night',
    name: '暗夜电台',
    nameKey: 'theme.night',
    description: '深墨绿底 + 荧光青柠，夜间护眼，微光氛围',
    descKey: 'theme.nightDesc',
    tone: '深色',
    toneKey: 'themePage.toneDark',
    swatch: { bg: '#14170f', surface: 'rgba(255,255,255,.14)', accent: '#c8f36a', text: '#eef3e2' },
  },
  {
    id: 'sunset',
    name: '落日橙',
    nameKey: 'theme.sunset',
    description: '暖橘底 + 珊瑚橙，黄昏氛围，温暖而有活力',
    descKey: 'theme.sunsetDesc',
    tone: '浅色',
    toneKey: 'themePage.toneLight',
    swatch: { bg: '#fdf3ec', surface: '#ffffff', accent: '#ff8a5c', text: '#3a2a22' },
  },
  {
    id: 'violet',
    name: '紫罗兰',
    nameKey: 'theme.violet',
    description: '深紫底 + 霓虹紫，梦幻神秘，暗夜里的霓虹感',
    descKey: 'theme.violetDesc',
    tone: '深色',
    toneKey: 'themePage.toneDark',
    swatch: { bg: '#15121f', surface: 'rgba(255,255,255,.12)', accent: '#9d6bff', text: '#f2effa' },
  },
  {
    id: 'ocean',
    name: '海洋蓝',
    nameKey: 'theme.ocean',
    description: '雾蓝底 + 澄澈海蓝，冷静清爽，通透明快',
    descKey: 'theme.oceanDesc',
    tone: '浅色',
    toneKey: 'themePage.toneLight',
    swatch: { bg: '#eef4f8', surface: '#ffffff', accent: '#4aa3df', text: '#1f2a33' },
  },
  {
    id: 'sakura',
    name: '樱花粉',
    nameKey: 'theme.sakura',
    description: '樱粉底 + 落樱粉，柔和不刺眼，少女氛围',
    descKey: 'theme.sakuraDesc',
    tone: '浅色',
    toneKey: 'themePage.toneLight',
    swatch: { bg: '#fdf2f5', surface: '#ffffff', accent: '#f18ab0', text: '#3a2530' },
  },
  {
    id: 'oled',
    name: 'OLED 纯黑',
    nameKey: 'theme.oled',
    description: '纯黑底 + 霓虹青，极致省电，OLED 屏观感最佳',
    descKey: 'theme.oledDesc',
    tone: '深色',
    toneKey: 'themePage.toneDark',
    swatch: { bg: '#000000', surface: 'rgba(255,255,255,.06)', accent: '#45d0ff', text: '#e8e8ea' },
  },
]

/** 默认主题 */
export const DEFAULT_THEME: ThemeId = 'glass'

/** 本地存储键 */
const STORAGE_KEY = 'sm-theme'

/** 当前主题（响应式，供界面展示选中态） */
export const currentTheme = ref<ThemeId>(DEFAULT_THEME)

/** 校验任意字符串是否为受支持的主题 id（含 custom） */
function isThemeId(value: unknown): value is ThemeId {
  return value === 'custom' || THEMES.some((theme) => theme.id === value)
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
 * 自定义主题恢复到「色板 + 遮罩」的无壁纸形态（objectURL 不跨会话），
 * 壁纸图片由 services/user-theme 的 syncRemoteTheme 在登录恢复后异步补齐。
 * 应在 App.vue onLaunch 中尽早调用，避免首屏出现默认主题闪烁。
 */
export function initTheme() {
  let stored: unknown = ''
  try {
    stored = uni.getStorageSync(STORAGE_KEY)
  } catch {
    stored = ''
  }
  restoreCustomConfig()
  const id = isThemeId(stored) ? stored : DEFAULT_THEME
  currentTheme.value = id
  if (id === 'custom' && currentCustomConfig.value) {
    // 无壁纸先注入色板令牌，壁纸到位后 syncRemoteTheme 会重新调用 applyCustomTheme。
    injectCustomStyle(currentCustomConfig.value, null)
  }
  writeToDocument(id)
}

// ============================================================================
// 自定义图片主题（custom）
// ========================================================================== */

/** 本地存储键：自定义主题配置（色板/遮罩/启用，不含壁纸二进制） */
const CUSTOM_STORAGE_KEY = 'sm-theme-custom'
/** 注入的自定义主题样式元素 id */
const CUSTOM_STYLE_ID = 'sm-custom-theme'
/** 遮罩不透明度上限，与后端校验保持一致 */
export const MAX_MASK_OPACITY = 0.85

/** 自定义图片主题配置（本地与远端共用的形态） */
export interface CustomThemeConfig {
  /** 主强调色 #RRGGBB；null 时以默认强调色兜底 */
  accent: string | null
  /** 次强调色 #RRGGBB */
  accent2: string | null
  /** 第三强调色 #RRGGBB */
  accent3: string | null
  /** 壁纸暗色遮罩不透明度 0-0.85 */
  maskOpacity: number
  /** 是否启用（多端同步的开关，本地仅作展示缓存） */
  enabled: boolean
}

/** 当前自定义主题配置（响应式，供主题页展示与编辑）；null 表示从未配置 */
export const currentCustomConfig = ref<CustomThemeConfig | null>(null)

/**
 * 当前壁纸预览地址（响应式，供主题页缩略图展示）。
 * 与 CSS 背景共用同一个 objectURL：本会话刚选图时是本地 blob 路径，
 * 其余时候是服务端壁纸拉取后的 blob；离开页面不会丢失（模块级状态）。
 */
export const currentWallpaperUrl = ref<string | null>(null)

/** 壁纸 objectURL（仅本次会话有效；CSS 背景无法携带鉴权头，因此经 blob 注入） */
let wallpaperObjectUrl: string | null = null

/** 从本地存储恢复自定义主题配置（initTheme 专用） */
function restoreCustomConfig() {
  try {
    const raw = uni.getStorageSync(CUSTOM_STORAGE_KEY)
    if (!raw) return
    const parsed = typeof raw === 'string' ? JSON.parse(raw) : raw
    if (parsed && typeof parsed === 'object' && 'maskOpacity' in parsed) {
      currentCustomConfig.value = {
        accent: parsed.accent ?? null,
        accent2: parsed.accent2 ?? null,
        accent3: parsed.accent3 ?? null,
        maskOpacity: clampMask(parsed.maskOpacity),
        enabled: Boolean(parsed.enabled),
      }
    }
  } catch {
    // 配置损坏时忽略：按未配置处理
    currentCustomConfig.value = null
  }
}

/** 遮罩值收敛到 0-0.85 */
function clampMask(value: unknown): number {
  const num = typeof value === 'number' && Number.isFinite(value) ? value : 0.45
  return Math.max(0, Math.min(MAX_MASK_OPACITY, num))
}

/**
 * 应用自定义图片主题：注入令牌样式、切换 <html data-sm-theme='custom'> 并持久化。
 * @param config 自定义主题配置（色板 + 遮罩 + 启用）
 * @param newWallpaperUrl 壁纸 objectURL；不传则沿用当前会话已加载的壁纸（若有）
 */
export function applyCustomTheme(config: CustomThemeConfig, newWallpaperUrl?: string | null) {
  if (newWallpaperUrl !== undefined && newWallpaperUrl !== wallpaperObjectUrl) {
    revokeWallpaper()
    wallpaperObjectUrl = newWallpaperUrl || null
  }
  currentCustomConfig.value = {
    ...config,
    maskOpacity: clampMask(config.maskOpacity),
  }
  injectCustomStyle(currentCustomConfig.value, wallpaperObjectUrl)
  currentWallpaperUrl.value = wallpaperObjectUrl
  currentTheme.value = 'custom'
  writeToDocument('custom')
  try {
    uni.setStorageSync(STORAGE_KEY, 'custom')
    // objectURL 不入存储：只持久化色板/遮罩/启用。
    uni.setStorageSync(CUSTOM_STORAGE_KEY, JSON.stringify(currentCustomConfig.value))
  } catch {
    // 存储不可用时忽略：本次会话内主题仍然生效
  }
}

/**
 * 彻底清除自定义主题（删除服务端配置后调用）：移除注入样式、objectURL 与本地配置。
 */
export function resetCustomTheme() {
  revokeWallpaper()
  currentCustomConfig.value = null
  // #ifdef H5
  document.getElementById(CUSTOM_STYLE_ID)?.remove()
  // #endif
  try {
    uni.removeStorageSync(CUSTOM_STORAGE_KEY)
  } catch {
    // 忽略存储异常
  }
}

/** 释放当前壁纸 objectURL（若存在） */
function revokeWallpaper() {
  if (wallpaperObjectUrl && wallpaperObjectUrl.startsWith('blob:')) {
    try {
      URL.revokeObjectURL(wallpaperObjectUrl)
    } catch {
      // 忽略释放失败
    }
  }
  wallpaperObjectUrl = null
  currentWallpaperUrl.value = null
}

/** 注入/替换自定义主题令牌样式块 */
function injectCustomStyle(config: CustomThemeConfig, wallpaperUrl: string | null) {
  // #ifdef H5
  if (typeof document === 'undefined') return
  let style = document.getElementById(CUSTOM_STYLE_ID) as HTMLStyleElement | null
  if (!style) {
    style = document.createElement('style')
    style.id = CUSTOM_STYLE_ID
    document.head.appendChild(style)
  }
  style.textContent = buildCustomTokenCss(config, wallpaperUrl)
  // #endif
}

/**
 * 由色板 + 遮罩 + 壁纸地址生成 `:root[data-sm-theme='custom']` 的完整令牌块。
 * 结构对齐 App.vue 的 glass 主题（暗底玻璃拟态，保证壁纸上内容可读），
 * 强调色系替换为取色色板，页面背景替换为「壁纸 + 可调暗色遮罩」。
 */
function buildCustomTokenCss(config: CustomThemeConfig, wallpaperUrl: string | null): string {
  const accent = config.accent ?? '#5be07a'
  const accent2 = config.accent2 ?? hueRotate(accent, 35)
  const accent3 = config.accent3 ?? hueRotate(accent, -35)
  const mask = clampMask(config.maskOpacity)
  // 底色：由主色派生的极暗色调（低饱和、低亮度），让整站底色与壁纸同源。
  const accentHsl = hexToHsl(accent)
  const base = accentHsl ? hslToHex(accentHsl.h, Math.min(accentHsl.s, 0.3), 0.07) : '#10131a'

  // 页面背景：有壁纸 → 暗色遮罩叠壁纸；无壁纸 → 主色系光晕渐变兜底。
  const maskLayer = `linear-gradient(rgba(9, 11, 15, ${mask.toFixed(3)}), rgba(9, 11, 15, ${mask.toFixed(3)}))`
  const pageBg = wallpaperUrl
    ? `${maskLayer}, url("${wallpaperUrl}") center / cover no-repeat`
    : `radial-gradient(circle at 8% 4%, ${withAlpha(accent, 0.26)}, transparent 38%),
      radial-gradient(circle at 96% 20%, ${withAlpha(accent2, 0.26)}, transparent 42%),
      radial-gradient(circle at 4% 94%, ${withAlpha(accent3, 0.22)}, transparent 40%), ${base}`

  // rpx 换算：SFC 里的 rpx 由 uni-app 编译期转换，而本函数是运行时注入的原始 CSS，
  // 浏览器不认识 rpx 单位（会导致圆角/阴影整条失效塌成 0）。
  // 这里转成响应式 calc：1rpx = 100vw/750，且与 uni-app H5 一致在 960px 宽度封顶。
  const rpx = (n: number) => `calc(${n} * min(100vw, 960px) / 750)`

  return `
:root[data-sm-theme='custom'] {
  --sm-bg: ${base};
  --sm-bg-soft: ${hslToHex(accentHsl?.h ?? 220, accentHsl ? Math.min(accentHsl.s, 0.3) : 0.2, 0.1)};
  --sm-page-bg: ${pageBg};
  --sm-surface: rgba(255, 255, 255, .09);
  --sm-surface-2: rgba(255, 255, 255, .06);
  --sm-surface-3: rgba(255, 255, 255, .12);
  --sm-border: rgba(255, 255, 255, .16);
  --sm-border-strong: rgba(255, 255, 255, .22);
  --sm-divider: rgba(255, 255, 255, .08);

  --sm-text: #f2f5f0;
  --sm-text-2: rgba(255, 255, 255, .62);
  --sm-text-3: rgba(255, 255, 255, .45);

  --sm-accent: ${accent};
  --sm-accent-ink: ${accentInk(accent)};
  --sm-accent-2: ${accent2};
  --sm-accent-3: ${accent3};
  --sm-accent-soft: ${withAlpha(accent, 0.16)};
  --sm-accent-soft-border: ${withAlpha(accent, 0.4)};
  --sm-accent-grad: linear-gradient(90deg, ${accent}, ${accent2});
  --sm-accent-glow: ${withAlpha(accent, 0.3)};

  --sm-danger: #ff8f6b;
  --sm-danger-soft: rgba(255, 143, 107, .16);
  --sm-danger-soft-border: rgba(255, 143, 107, .3);
  --sm-warn: #ffd166;
  --sm-warn-soft: rgba(255, 209, 102, .18);
  --sm-info-soft: ${withAlpha(accent2, 0.2)};
  --sm-muted-soft: rgba(255, 255, 255, .12);
  --sm-accent-2-ink: ${accentInk(accent2)};
  --sm-accent-3-soft: ${withAlpha(accent3, 0.18)};

  --sm-radius-card: ${rpx(24)};
  --sm-radius-field: ${rpx(16)};
  --sm-radius-pill: ${rpx(40)};
  --sm-shadow: 0 ${rpx(16)} ${rpx(44)} rgba(0, 0, 0, .4);
  --sm-shadow-sm: 0 ${rpx(8)} ${rpx(20)} rgba(0, 0, 0, .3);
  --sm-blur: 20px;

  --sm-glow-opacity: .5;
  --sm-glow-1: ${accent};
  --sm-glow-2: ${accent2};
  --sm-glow-3: ${accent3};

  --sm-hero-bg: linear-gradient(135deg, ${withAlpha(accent, 0.4)}, ${withAlpha(accent2, 0.34)});
  --sm-hero-shade: linear-gradient(90deg, rgba(0, 0, 0, .82) 0%, rgba(0, 0, 0, .42) 52%, rgba(0, 0, 0, .04) 100%);
  --sm-hero-ink-bg: rgba(255, 255, 255, .94);
  --sm-hero-ink: #10131a;
  --sm-avatar-bg: linear-gradient(135deg, ${withAlpha(accent, 0.6)}, ${withAlpha(accent2, 0.55)});
  --sm-cover-bg: linear-gradient(135deg, ${withAlpha(accent, 0.6)}, ${withAlpha(accent2, 0.55)});
  --sm-cover-ink: rgba(255, 255, 255, .9);
  --sm-overlay-ink: rgba(9, 11, 15, .5);
  --sm-mask: rgba(9, 11, 15, .62);

  --sm-nav-bg: ${base};
  --sm-nav-text: #ffffff;
  --sm-tabbar-bg: ${withAlpha(base, 0.72)};
  --sm-art-shadow: rgba(0, 0, 0, .42);
}
`
}
