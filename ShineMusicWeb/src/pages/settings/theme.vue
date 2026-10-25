<script setup lang="ts">
/**
 * 主题外观设置页：预设主题切换 + 自定义图片主题。
 *
 * 预设主题：applyTheme() 改写 <html data-sm-theme>，全站即时换肤（原理见 services/theme）。
 * 自定义图片主题：
 * - 登录后上传壁纸（≤8MB），前端 canvas 取色生成三色色板，
 *   配合暗色遮罩滑杆即时预览；上传成功后配置同步到服务端（多端一致）；
 * - 色板支持点选换主色；滑杆拖动实时预览、松手落库；
 * - 启用/停用开关同步服务端 enabled（账号同步的事实源）；删除会清空服务端配置。
 */
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, ImagePlus, LogIn, Palette, RefreshCw, Trash2 } from 'lucide-vue-next'
import {
  applyCustomTheme,
  applyTheme,
  currentCustomConfig,
  currentTheme,
  currentWallpaperUrl,
  DEFAULT_THEME,
  THEMES,
  type CustomThemeConfig,
  type ThemeId,
} from '@/services/theme'
import { extractPalette, type ExtractedPalette } from '@/services/color-extract'
import WallpaperCropper from '@/components/WallpaperCropper.vue'
import WebSlider from '@/components/WebSlider.vue'
import {
  deleteMyTheme,
  fetchMyTheme,
  fetchWallpaperObjectUrl,
  updateThemeConfig,
  uploadWallpaper,
} from '@/services/user-theme'
import { isAuthenticated, requireLogin } from '@/services/session'

// 文案多语言
const { t } = useI18n()

/** 取色中（选图后到色板生成为止） */
const extracting = ref(false)
/** 上传/落库中 */
const saving = ref(false)
/** 本次会话选中的本地壁纸临时路径（预览用，优先级最高） */
const localWallpaperPath = ref<string | null>(null)
/** 服务端已保存壁纸的预览（远端拉取；custom 停用时也能在编辑卡里看到已存图） */
const remoteWallpaperPreview = ref<string | null>(null)

/**
 * 编辑卡缩略图展示源：刚选的本地临时图 > 当前生效壁纸 > 服务端已存壁纸。
 * 前两者都是模块级状态，离开页面再回来不会丢失。
 */
const wallpaperPreview = computed(
  () => localWallpaperPath.value || currentWallpaperUrl.value || remoteWallpaperPreview.value,
)

/** 是否已配置过自定义主题 */
const hasCustom = computed(() => currentCustomConfig.value !== null)
/** 自定义主题当前是否处于启用态（即 data-sm-theme='custom'） */
const isCustomActive = computed(() => currentTheme.value === 'custom')
/** 遮罩滑杆展示值（0-85） */
const maskPercent = computed(() =>
  Math.round((currentCustomConfig.value?.maskOpacity ?? 0.45) * 100),
)
/** 色板圆点：主色/次色/第三色（null 时以主色旋转兜底的展示色代替） */
const paletteDots = computed(() => {
  const config = currentCustomConfig.value
  if (!config) return []
  return [config.accent, config.accent2, config.accent3].filter(
    (color): color is string => Boolean(color),
  )
})

// 进入页面时拉取一次远端配置：本机首次使用时能直接看到其他设备创建的自定义主题；
// 同时拉取服务端壁纸作为缩略图兜底（停用状态下编辑卡也能看到已保存的图）。
onMounted(() => {
  if (!isAuthenticated.value) return
  fetchMyTheme()
    .then(async (remote) => {
      if (remote.wallpaperUrl && !wallpaperPreview.value) {
        remoteWallpaperPreview.value = await fetchWallpaperObjectUrl()
      }
      if (!remote.enabled) return
      const config: CustomThemeConfig = {
        accent: remote.accent ?? null,
        accent2: remote.accent2 ?? null,
        accent3: remote.accent3 ?? null,
        maskOpacity: remote.maskOpacity,
        enabled: true,
      }
      // 已处于 custom 时不动（避免打断编辑中的状态、避免重复拉取壁纸）；
      // 否则以远端壁纸一起应用（整页背景与缩略图同步就位）。
      if (!isCustomActive.value) applyCustomTheme(config, remoteWallpaperPreview.value)
    })
    .catch(() => undefined)
})

/** 选中预设主题（点击即生效并持久化；若远端自定义主题启用中则一并停用，避免下次登录被同步拉回） */
async function selectTheme(id: ThemeId) {
  if (currentTheme.value === id) return
  applyTheme(id)
  if (id !== 'custom' && hasCustom.value && currentCustomConfig.value?.enabled) {
    try {
      const remote = await updateThemeConfig({ ...currentCustomConfig.value, enabled: false })
      currentCustomConfig.value = { ...currentCustomConfig.value, enabled: remote.enabled }
    } catch {
      // 停用失败不阻断本地切换：下次登录同步时远端状态仍会生效
    }
  }
  const switchedTheme = THEMES.find((theme) => theme.id === id)
  uni.showToast({ title: t('themePage.switchedTo', { name: switchedTheme ? t(switchedTheme.nameKey) : '' }), icon: 'none' })
}

/** 裁剪编辑器打开时的原图路径（null = 关闭） */
const croppingPath = ref<string | null>(null)

/** 选择壁纸图片（未登录先引导登录；H5 端先进入裁剪编辑器） */
function chooseWallpaper() {
  if (!requireLogin()) return
  uni.chooseImage({
    count: 1,
    sizeType: ['compressed'],
    success: ({ tempFilePaths }) => {
      const path = tempFilePaths?.[0]
      // 先进入裁剪编辑器：横竖屏都按所见即所得确认显示区域，确认后才走生成管线
      if (path) croppingPath.value = path
    },
  })
}

/**
 * 裁剪完成：拿到按可见区域导出（已压缩）的壁纸源，进入取色 → 预览 → 上传管线。
 * App 端裁剪器已在小画布上取好色板（服务层无 DOM canvas），直接复用。
 */
function onCropConfirmed(url: string, palette?: ExtractedPalette) {
  croppingPath.value = null
  void onImagePicked(url, palette)
}

/** 取消裁剪：放弃本次选图 */
function onCropCancelled() {
  croppingPath.value = null
}

/**
 * 裁剪确认后：取色 → 即时预览 → 上传壁纸 → 回填色板落库。
 * @param providedPalette 裁剪器已算好的色板（App 端）；缺省时走 H5 的 DOM 取色
 */
async function onImagePicked(path: string, providedPalette?: ExtractedPalette) {
  extracting.value = true
  const palette = providedPalette ?? (await extractPalette(path))
  localWallpaperPath.value = path
  const previous = currentCustomConfig.value
  const config: CustomThemeConfig = {
    accent: palette.accent,
    accent2: palette.accent2,
    accent3: palette.accent3,
    maskOpacity: previous?.maskOpacity ?? 0.45,
    enabled: true,
  }
  // 先本地生效（秒级预览）：把本地临时路径直接作为壁纸源注入整页背景，
  // 上传失败也不影响本次会话的体验；上传成功后服务端存档，跨会话由远程同步接管。
  applyCustomTheme(config, path)
  extracting.value = false

  saving.value = true
  try {
    await uploadWallpaper(path)
    await updateThemeConfig(config)
    uni.showToast({ title: t('themePage.customThemeCreated'), icon: 'none' })
  } catch (error) {
    uni.showToast({
      title: error instanceof Error ? error.message : t('themePage.saveFailedRetry'),
      icon: 'none',
    })
  } finally {
    saving.value = false
  }
}

/** 点选色板圆点：被点的颜色成为主色，其余按原顺序后移 */
async function pickAccent(color: string) {
  const config = currentCustomConfig.value
  if (!config || config.accent === color) return
  const rest = [config.accent, config.accent2, config.accent3]
    .filter((item): item is string => Boolean(item) && item !== color)
  const next: CustomThemeConfig = {
    ...config,
    accent: color,
    accent2: rest[0] ?? null,
    accent3: rest[1] ?? null,
  }
  applyCustomTheme(next)
  try {
    await updateThemeConfig(next)
  } catch {
    // 落库失败仅影响多端同步，本地预览不受影响
  }
}

/** 滑杆拖动中：实时预览（轻量节流，约 80ms 一次） */
let lastPreviewAt = 0
function onMaskChanging(event: { detail: { value: number } }) {
  const now = Date.now()
  if (now - lastPreviewAt < 80) return
  lastPreviewAt = now
  previewMask(event.detail.value)
}

/** 滑杆松手：最终预览 + 落库 */
async function onMaskChange(event: { detail: { value: number } }) {
  previewMask(event.detail.value)
  const config = currentCustomConfig.value
  if (!config) return
  try {
    await updateThemeConfig(config)
  } catch {
    // 落库失败仅影响多端同步
  }
}

/** 以指定遮罩百分比刷新本地预览 */
function previewMask(percent: number) {
  const config = currentCustomConfig.value
  if (!config) return
  applyCustomTheme({ ...config, maskOpacity: Math.min(0.85, Math.max(0, percent / 100)) })
}

/** 启用/停用自定义主题（启用状态同步服务端，作为多端同步的开关） */
async function toggleCustom() {
  const config = currentCustomConfig.value
  if (!config) return
  if (isCustomActive.value) {
    // 停用：回到默认预设，但保留配置数据
    applyTheme(DEFAULT_THEME)
    try {
      await updateThemeConfig({ ...config, enabled: false })
    } catch {
      // 忽略落库失败
    }
    return
  }
  // 启用：需要已有壁纸（服务端强校验），否则提示先上传
  applyCustomTheme({ ...config, enabled: true })
  try {
    await updateThemeConfig({ ...config, enabled: true })
  } catch (error) {
    uni.showToast({
      title: error instanceof Error ? error.message : t('themePage.enableFailedNeedWallpaper'),
      icon: 'none',
    })
  }
}

/** 删除自定义主题（服务端壁纸 + 配置一并清除，需二次确认） */
function removeCustom() {
  uni.showModal({
    title: t('themePage.deleteCustomTitle'),
    content: t('themePage.deleteCustomConfirm'),
    confirmText: t('app.delete'),
    confirmColor: '#d05a40',
    success: async ({ confirm }) => {
      if (!confirm) return
      try {
        await deleteMyTheme()
      } catch {
        // 删除失败也先清理本地，避免界面停留在失效状态
      }
      localWallpaperPath.value = null
      remoteWallpaperPreview.value = null
      applyTheme(DEFAULT_THEME)
      uni.showToast({ title: t('themePage.customThemeDeleted'), icon: 'none' })
    },
  })
}
</script>

<template>
  <scroll-view class="theme-page" scroll-y :show-scrollbar="false">
    <!-- 页头 -->
    <view class="page-heading">
      <view class="heading-icon"><Palette :size="20" /></view>
      <view class="heading-copy">
        <text class="heading-title">{{ $t('theme.title') }}</text>
        <text class="heading-subtitle">{{ $t('themePage.intro') }}</text>
      </view>
    </view>

    <!-- ===== 自定义图片主题 ===== -->
    <view class="section-title-row">
      <text class="section-title">{{ $t('themePage.customSectionTitle') }}</text>
      <text v-if="hasCustom" class="section-badge" :class="{ on: isCustomActive }">
        {{ isCustomActive ? $t('themePage.statusActive') : $t('themePage.statusInactive') }}
      </text>
    </view>

    <!-- 未登录：引导 -->
    <view v-if="!isAuthenticated" class="custom-card login-hint">
      <view class="hint-icon"><LogIn :size="22" /></view>
      <view class="hint-copy">
        <text class="hint-title">{{ $t('themePage.loginHintTitle') }}</text>
        <text class="hint-desc">{{ $t('themePage.loginHintDesc') }}</text>
      </view>
      <view class="hint-button" @click="chooseWallpaper">{{ $t('themePage.goLogin') }}</view>
    </view>

    <!-- 未配置：上传区 -->
    <view
      v-else-if="!hasCustom"
      class="custom-card upload-area"
      :class="{ busy: extracting || saving }"
      @click="chooseWallpaper"
    >
      <view class="upload-icon"><ImagePlus :size="26" /></view>
      <text class="upload-title">{{ extracting ? $t('themePage.extracting') : saving ? $t('themePage.saving') : $t('themePage.uploadImage') }}</text>
      <text class="upload-desc">{{ $t('themePage.uploadFormatHint') }}</text>
    </view>

    <!-- 已配置：预览 + 调色 -->
    <view v-else class="custom-card editor">
      <!-- 预览条：本地临时图 > 当前生效壁纸 > 服务端已存壁纸，都没有时以色板渐变示意 -->
      <view class="custom-preview" :style="wallpaperPreview ? '' : { background: `linear-gradient(135deg, ${paletteDots[0] || '#333'}, ${paletteDots[1] || '#666'})` }">
        <img
          v-if="wallpaperPreview"
          class="preview-image"
          :src="wallpaperPreview"
          mode="aspectFill"
        />
        <view class="preview-shade" :style="{ opacity: maskPercent / 100 }"></view>
        <text class="preview-label">{{ $t('themePage.wallpaperPreview') }}</text>
      </view>

      <!-- 色板：点选换主色 -->
      <view class="palette-row">
        <text class="palette-label">{{ $t('themePage.accentColor') }}</text>
        <view class="palette-dots">
          <view
            v-for="(color, index) in paletteDots"
            :key="`${color}-${index}`"
            class="palette-dot"
            :class="{ main: index === 0 }"
            :style="{ background: color }"
            @click="pickAccent(color)"
          >
            <Check v-if="index === 0" :size="12" color="#fff" />
          </view>
        </view>
        <text class="palette-hint">{{ $t('themePage.paletteHint') }}</text>
      </view>

      <!-- 遮罩深浅滑杆 -->
      <view class="mask-row">
        <view class="mask-head">
          <text class="palette-label">{{ $t('themePage.maskOpacity') }}</text>
          <text class="mask-value">{{ maskPercent }}%</text>
        </view>
        <WebSlider
          class="mask-slider"
          :min="0"
          :max="85"
          :step="1"
          :value="maskPercent"
          activeColor="var(--sm-accent)"
          backgroundColor="var(--sm-border)"
          :block-size="20"
          @changing="onMaskChanging"
          @change="onMaskChange"
        />
        <text class="mask-hint">{{ $t('themePage.maskHint') }}</text>
      </view>

      <!-- 操作行 -->
      <view class="custom-actions">
        <view class="action-button primary" @click="toggleCustom">
          {{ isCustomActive ? $t('themePage.disableBtn') : $t('themePage.enableBtn') }}
        </view>
        <view class="action-button" :class="{ disabled: extracting || saving }" @click="chooseWallpaper">
          <RefreshCw :size="13" />
          <text>{{ $t('themePage.changeImage') }}</text>
        </view>
        <view class="action-button danger" @click="removeCustom">
          <Trash2 :size="13" />
          <text>{{ $t('app.delete') }}</text>
        </view>
      </view>
    </view>

    <!-- ===== 预设主题 ===== -->
    <view class="section-title-row">
      <text class="section-title">{{ $t('themePage.presetSectionTitle') }}</text>
    </view>

    <!-- 主题卡片列表 -->
    <view class="theme-list">
      <view
        v-for="theme in THEMES"
        :key="theme.id"
        class="theme-card"
        :class="{ active: currentTheme === theme.id }"
        @click="selectTheme(theme.id)"
      >
        <!-- 迷你预览：用该主题自身的色卡绘制，不受当前主题影响 -->
        <view class="theme-preview" :style="{ background: theme.swatch.bg }">
          <view class="preview-card" :style="{ background: theme.swatch.surface, borderColor: theme.swatch.accent }">
            <view class="preview-dot" :style="{ background: theme.swatch.accent }"></view>
            <view class="preview-lines">
              <view class="preview-line" :style="{ background: theme.swatch.text }"></view>
              <view class="preview-line short" :style="{ background: theme.swatch.text }"></view>
            </view>
          </view>
          <view class="preview-pill" :style="{ background: theme.swatch.accent }"></view>
        </view>

        <!-- 主题信息 -->
        <view class="theme-copy">
          <view class="theme-name-row">
            <text class="theme-name">{{ $t(theme.nameKey) }}</text>
            <text class="theme-tone">{{ $t(theme.toneKey) }}</text>
          </view>
          <text class="theme-desc">{{ $t(theme.descKey) }}</text>
        </view>

        <!-- 选中标记 -->
        <view class="theme-check" :class="{ checked: currentTheme === theme.id }">
          <Check v-if="currentTheme === theme.id" :size="14" />
        </view>
      </view>
    </view>

    <view class="bottom-space"></view>
  </scroll-view>

  <!-- 壁纸裁剪编辑器：固定取景框 + 拖动/缩放，所见即所得（H5 与 App 双端） -->
  <WallpaperCropper
    v-if="croppingPath"
    :src="croppingPath"
    @confirm="onCropConfirmed"
    @cancel="onCropCancelled"
  />
</template>

<style lang="scss" scoped>
/* 主题外观设置页：自身也全部使用主题令牌，保证各主题下都协调 */
.theme-page { height: 100vh; padding: 30rpx 34rpx 0; box-sizing: border-box; background: var(--sm-page-bg); }

.page-heading { display: flex; align-items: center; gap: 16rpx; padding: 10rpx 0 28rpx; }
.heading-icon { display: grid; place-items: center; width: 68rpx; height: 68rpx; border-radius: 18rpx; background: var(--sm-accent-soft); border: 1rpx solid var(--sm-border); color: var(--sm-accent); }
.heading-copy { display: flex; flex-direction: column; gap: 6rpx; }
.heading-title { color: var(--sm-text); font-size: 29rpx; font-weight: 800; }
.heading-subtitle { color: var(--sm-text-3); font-size: 17rpx; }

/* 分区标题 */
.section-title-row { display: flex; align-items: center; gap: 12rpx; padding: 8rpx 0 18rpx; }
.section-title { color: var(--sm-text-2); font-size: 20rpx; font-weight: 700; letter-spacing: 2rpx; }
.section-badge { padding: 3rpx 14rpx; border: 1rpx solid var(--sm-border); border-radius: 20rpx; color: var(--sm-text-3); font-size: 13rpx; }
.section-badge.on { border-color: transparent; background: var(--sm-accent-soft); color: var(--sm-accent); }

/* ===== 自定义主题卡片 ===== */
.custom-card { margin-bottom: 30rpx; padding: 26rpx; border: 1rpx solid var(--sm-border); border-radius: var(--sm-radius-card); background: var(--sm-surface); -webkit-backdrop-filter: blur(var(--sm-blur)); backdrop-filter: blur(var(--sm-blur)); box-shadow: var(--sm-shadow-sm); }

/* 未登录引导 */
.login-hint { display: flex; align-items: center; gap: 20rpx; }
.hint-icon { display: grid; flex: none; place-items: center; width: 84rpx; height: 84rpx; border-radius: 24rpx; background: var(--sm-accent-soft); border: 1rpx solid var(--sm-border); color: var(--sm-accent); }
.hint-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 8rpx; }
.hint-title { color: var(--sm-text); font-size: 23rpx; font-weight: 700; }
.hint-desc { color: var(--sm-text-3); font-size: 16rpx; line-height: 1.5; }
.hint-button { flex: none; padding: 14rpx 30rpx; border-radius: var(--sm-radius-pill); background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 19rpx; font-weight: 700; }

/* 上传区 */
.upload-area { display: flex; flex-direction: column; align-items: center; gap: 12rpx; padding: 60rpx 26rpx; border: 2rpx dashed var(--sm-border-strong); background: var(--sm-surface-2); }
.upload-area.busy { opacity: .7; }
.upload-icon { display: grid; place-items: center; width: 96rpx; height: 96rpx; border-radius: 50%; background: var(--sm-accent-soft); color: var(--sm-accent); }
.upload-title { color: var(--sm-text); font-size: 24rpx; font-weight: 700; }
.upload-desc { color: var(--sm-text-3); font-size: 16rpx; }

/* 编辑器 */
.editor { display: flex; flex-direction: column; gap: 24rpx; }

/* 壁纸预览 */
.custom-preview { position: relative; height: 220rpx; border-radius: 18rpx; overflow: hidden; border: 1rpx solid var(--sm-border); }
.preview-image { position: absolute; inset: 0; width: 100%; height: 100%; }
.preview-shade { position: absolute; inset: 0; background: #090b0f; transition: opacity .15s ease; }
.preview-label { position: absolute; top: 14rpx; left: 16rpx; padding: 4rpx 14rpx; border-radius: 20rpx; background: rgba(0, 0, 0, .45); color: rgba(255, 255, 255, .9); font-size: 14rpx; }

/* 色板 */
.palette-row { display: flex; align-items: center; gap: 16rpx; }
.palette-label { flex: none; color: var(--sm-text-2); font-size: 18rpx; font-weight: 600; }
.palette-dots { display: flex; align-items: center; gap: 14rpx; }
.palette-dot { display: grid; place-items: center; width: 52rpx; height: 52rpx; border-radius: 50%; border: 2rpx solid rgba(255, 255, 255, .25); transition: transform .12s ease; }
.palette-dot:active { transform: scale(.92); }
.palette-dot.main { border-color: rgba(255, 255, 255, .85); box-shadow: 0 0 0 4rpx var(--sm-accent-soft); }
.palette-hint { flex: 1; text-align: right; color: var(--sm-text-3); font-size: 14rpx; }

/* 遮罩滑杆 */
.mask-row { display: flex; flex-direction: column; gap: 10rpx; }
.mask-head { display: flex; align-items: center; justify-content: space-between; }
.mask-value { color: var(--sm-accent); font-size: 18rpx; font-weight: 800; }
.mask-slider { margin: 0; }
.mask-hint { color: var(--sm-text-3); font-size: 14rpx; }

/* 操作行 */
.custom-actions { display: flex; gap: 14rpx; }
.action-button { display: flex; flex: 1; align-items: center; justify-content: center; gap: 8rpx; padding: 18rpx 0; border: 1rpx solid var(--sm-border-strong); border-radius: var(--sm-radius-field); color: var(--sm-text-2); font-size: 18rpx; font-weight: 600; transition: opacity .12s ease; }
.action-button.disabled { opacity: .55; }
.action-button.primary { border-color: transparent; background: var(--sm-accent); color: var(--sm-accent-ink); }
.action-button.danger { border-color: var(--sm-danger-soft-border); background: var(--sm-danger-soft); color: var(--sm-danger); }

/* ===== 预设主题列表 ===== */
.theme-list { display: flex; flex-direction: column; gap: 18rpx; }

.theme-card {
  display: flex;
  align-items: center;
  gap: 20rpx;
  padding: 22rpx;
  border: 1rpx solid var(--sm-border);
  border-radius: var(--sm-radius-card);
  background: var(--sm-surface);
  -webkit-backdrop-filter: blur(var(--sm-blur));
  backdrop-filter: blur(var(--sm-blur));
  box-shadow: var(--sm-shadow-sm);
  transition: border-color .2s ease, transform .2s ease;
}

.theme-card.active {
  border-color: var(--sm-accent);
  box-shadow: 0 0 0 2rpx var(--sm-accent-soft-border), var(--sm-shadow-sm);
}

/* 迷你预览 */
.theme-preview {
  position: relative;
  display: flex;
  flex: none;
  align-items: flex-end;
  width: 130rpx;
  height: 130rpx;
  padding: 14rpx;
  border: 1rpx solid var(--sm-border);
  border-radius: 18rpx;
  overflow: hidden;
}

.preview-card {
  display: flex;
  align-items: center;
  gap: 8rpx;
  width: 100%;
  padding: 10rpx;
  border: 2rpx solid transparent;
  border-radius: 10rpx;
}

.preview-dot { flex: none; width: 16rpx; height: 16rpx; border-radius: 50%; }
.preview-lines { display: flex; flex: 1; flex-direction: column; gap: 6rpx; min-width: 0; }
.preview-line { height: 6rpx; border-radius: 3rpx; opacity: .85; }
.preview-line.short { width: 60%; opacity: .45; }
.preview-pill { position: absolute; top: 14rpx; right: 14rpx; width: 34rpx; height: 12rpx; border-radius: 6rpx; }

/* 主题信息 */
.theme-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 8rpx; }
.theme-name-row { display: flex; align-items: center; gap: 10rpx; }
.theme-name { color: var(--sm-text); font-size: 23rpx; font-weight: 800; }
.theme-tone { padding: 3rpx 12rpx; border: 1rpx solid var(--sm-border); border-radius: 20rpx; background: var(--sm-surface-2); color: var(--sm-text-3); font-size: 13rpx; }
.theme-desc { color: var(--sm-text-2); font-size: 16rpx; line-height: 1.55; }

/* 选中标记 */
.theme-check { display: grid; flex: none; place-items: center; width: 44rpx; height: 44rpx; border: 1rpx solid var(--sm-border-strong); border-radius: 50%; color: var(--sm-accent-ink); }
.theme-check.checked { border-color: transparent; background: var(--sm-accent); }

.bottom-space { height: 60rpx; }
</style>
