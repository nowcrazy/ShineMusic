<script setup lang="ts">
/**
 * 壁纸裁剪编辑器（仅 H5，覆盖层组件）。
 *
 * 交互模型：固定取景框（取景框比例 = 屏幕可见区域比例，输出即壁纸所见），
 * 图片在框内拖动与缩放，所见即所得：
 * - 单指拖动、双指捏合缩放、滚轮缩放（Pointer Events 统一处理）；
 * - 图片必须始终完全覆盖取景框（缩放/位移实时收敛），不会露出空隙；
 * - 确认后按可见区域导出位图：最长边 2560px、WebP（不支持时回退 JPEG），
 *   以 objectURL 交给上层，同时承担压缩职责（原图不再直接参与铺底与上传）。
 */
import { computed, getCurrentInstance, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { paletteFromPixels, type ExtractedPalette } from '@/services/color-extract'

const props = defineProps<{ src: string }>()
const emit = defineEmits<{
  (e: 'cancel'): void
  (e: 'confirm', url: string, palette?: ExtractedPalette): void
}>()

const { t } = useI18n()

/** 导出位图最长边；导出同时完成压缩，控制整页背景的解码/绘制开销 */
const MAX_EXPORT_EDGE = 2560
/** 导出编码质量 */
const EXPORT_QUALITY = 0.85
/** 缩放范围：1 = 恰好铺满取景框（不允许缩出空隙），上限防止过度放大失真 */
const MIN_ZOOM = 1
const MAX_ZOOM = 6

const viewport = ref<HTMLDivElement | null>(null)
/** 已解码的原图（init 时加载，导出时复用避免二次解码） */
let decoded: HTMLImageElement | null = null
/** 原图自然尺寸 */
const natural = ref({ w: 0, h: 0 })
/** 取景框尺寸（像素） */
const frame = ref({ w: 0, h: 0 })
/** 相对铺满态的缩放倍率 */
const zoom = ref(1)
/** 图片中心相对取景框中心的位移（像素） */
const offset = ref({ x: 0, y: 0 })
const loaded = ref(false)
const exporting = ref(false)

/** 铺满取景框所需的最小缩放（显示像素 / 原图像素） */
let baseScale = 1

const dispW = computed(() => Math.round(natural.value.w * baseScale * zoom.value))
const dispH = computed(() => Math.round(natural.value.h * baseScale * zoom.value))

/** 图片定位：中心锚点 + 位移 */
const imgStyle = computed(() => ({
  width: `${dispW.value}px`,
  height: `${dispH.value}px`,
  transform: `translate(calc(-50% + ${offset.value.x}px), calc(-50% + ${offset.value.y}px))`,
}))

function clamp(value: number, min: number, max: number) {
  return Math.max(min, Math.min(max, value))
}

/** 位移收敛：图片边缘不允许进入取景框内（保持完全覆盖） */
function clampOffset() {
  const maxX = Math.max(0, (dispW.value - frame.value.w) / 2)
  const maxY = Math.max(0, (dispH.value - frame.value.h) / 2)
  offset.value = {
    x: clamp(offset.value.x, -maxX, maxX),
    y: clamp(offset.value.y, -maxY, maxY),
  }
}

function setZoom(next: number) {
  zoom.value = clamp(next, MIN_ZOOM, MAX_ZOOM)
  clampOffset()
}

/** 测量取景框并重置视图（铺满居中） */
function measureAndReset() {
  const el = viewport.value
  if (!el || !natural.value.w) return
  frame.value = { w: el.clientWidth, h: el.clientHeight }
  baseScale = Math.max(frame.value.w / natural.value.w, frame.value.h / natural.value.h)
  zoom.value = 1
  offset.value = { x: 0, y: 0 }
}

function handleResize() {
  if (!loaded.value) return
  measureAndReset()
}

/** App 端屏幕旋转：重新量取景框并重置视图 */
function handleAppResize() {
  if (loaded.value) measureAppFrame()
}

onMounted(() => {
  // #ifdef H5
  const img = new Image()
  img.onload = () => {
    decoded = img
    natural.value = { w: img.naturalWidth, h: img.naturalHeight }
    measureAndReset()
    loaded.value = true
  }
  img.onerror = () => emit('cancel')
  img.src = props.src
  window.addEventListener('resize', handleResize)
  // #endif
  // #ifdef APP-PLUS
  // App 端无 DOM：用 uni.getImageInfo 取原图尺寸，用节点查询量取景框尺寸
  uni.getImageInfo({
    src: props.src,
    success: (info) => {
      natural.value = { w: info.width, h: info.height }
      measureAppFrame(() => {
        loaded.value = true
      })
    },
    fail: () => emit('cancel'),
  })
  uni.onWindowResize(handleAppResize)
  // #endif
})

onBeforeUnmount(() => {
  // #ifdef H5
  window.removeEventListener('resize', handleResize)
  // #endif
  // #ifdef APP-PLUS
  uni.offWindowResize(handleAppResize)
  // #endif
})

/* ---------- 手势：单指拖动 / 双指捏合 / 滚轮缩放 ---------- */

const pointers = new Map<number, { x: number; y: number }>()
let panStart: { x: number; y: number; ox: number; oy: number } | null = null
let pinchStart: { dist: number; zoom: number; midX: number; midY: number; ox: number; oy: number } | null = null

function onPointerDown(e: PointerEvent) {
  ;(e.currentTarget as Element).setPointerCapture?.(e.pointerId)
  pointers.set(e.pointerId, { x: e.clientX, y: e.clientY })
  if (pointers.size === 1) {
    panStart = { x: e.clientX, y: e.clientY, ox: offset.value.x, oy: offset.value.y }
  } else if (pointers.size === 2) {
    const [a, b] = [...pointers.values()]
    pinchStart = {
      dist: Math.max(1, Math.hypot(a.x - b.x, a.y - b.y)),
      zoom: zoom.value,
      midX: (a.x + b.x) / 2,
      midY: (a.y + b.y) / 2,
      ox: offset.value.x,
      oy: offset.value.y,
    }
    panStart = null
  }
}

function onPointerMove(e: PointerEvent) {
  if (!pointers.has(e.pointerId)) return
  pointers.set(e.pointerId, { x: e.clientX, y: e.clientY })
  if (pointers.size >= 2 && pinchStart) {
    const [a, b] = [...pointers.values()]
    const dist = Math.max(1, Math.hypot(a.x - b.x, a.y - b.y))
    setZoom(pinchStart.zoom * clamp(dist / pinchStart.dist, 0.2, 5))
    const midX = (a.x + b.x) / 2
    const midY = (a.y + b.y) / 2
    offset.value = { x: pinchStart.ox + (midX - pinchStart.midX), y: pinchStart.oy + (midY - pinchStart.midY) }
    clampOffset()
  } else if (panStart) {
    offset.value = { x: panStart.ox + (e.clientX - panStart.x), y: panStart.oy + (e.clientY - panStart.y) }
    clampOffset()
  }
}

function onPointerUp(e: PointerEvent) {
  pointers.delete(e.pointerId)
  if (pointers.size === 1) {
    const [p] = [...pointers.values()]
    panStart = { x: p.x, y: p.y, ox: offset.value.x, oy: offset.value.y }
    pinchStart = null
  } else if (pointers.size === 0) {
    panStart = null
    pinchStart = null
  }
}

function onWheel(e: WheelEvent) {
  e.preventDefault()
  setZoom(zoom.value * (e.deltaY < 0 ? 1.12 : 1 / 1.12))
}

function resetView() {
  zoom.value = 1
  offset.value = { x: 0, y: 0 }
}

/* ---------- App 端：touch 手势 + uni canvas 导出 ---------- */

/** 当前组件实例：App 端 canvas API 需要传入组件实例 */
const instance = getCurrentInstance()?.proxy
/** 导出画布尺寸（按可见区域计算，确认时写入） */
const exportW = ref(1)
const exportH = ref(1)
/** 取色小画布尺寸（最长边 96px，与 H5 取色采样规格一致） */
const paletteW = ref(1)
const paletteH = ref(1)

/**
 * App 端取色：把原图缩绘到 96px 小画布，读像素后复用 H5 同一套色板算法
 * （App 端无 document canvas，只能走 uni canvas）。失败返回 undefined 由上层兜底。
 */
async function extractPaletteApp(): Promise<ExtractedPalette | undefined> {
  if (!instance || !natural.value.w) return undefined
  try {
    const scale = Math.min(1, 96 / Math.max(natural.value.w, natural.value.h))
    const width = Math.max(8, Math.round(natural.value.w * scale))
    const height = Math.max(8, Math.round(natural.value.h * scale))
    paletteW.value = width
    paletteH.value = height
    await nextTick()
    const ctx = uni.createCanvasContext('sm-palette-canvas', instance)
    ctx.drawImage(props.src, 0, 0, width, height)
    await new Promise<void>((resolve) => {
      ctx.draw(false, () => resolve())
    })
    // 各端返回形态不一（Uint8ClampedArray / ArrayBuffer / number[]），统一成可索引的数组
    const data = await new Promise<ArrayLike<number>>((resolve, reject) => {
      uni.canvasGetImageData(
        {
          canvasId: 'sm-palette-canvas',
          x: 0,
          y: 0,
          width,
          height,
          success: (res) => {
            const raw = (res as unknown as { data: ArrayBuffer | ArrayLike<number> }).data
            resolve(raw instanceof ArrayBuffer ? new Uint8ClampedArray(raw) : raw)
          },
          fail: reject,
        },
        instance,
      )
    })
    return paletteFromPixels(data, width, height)
  } catch {
    return undefined
  }
}

/** App 端 <img> 定位样式（与 H5 的 imgStyle 等价） */
const appImgStyle = computed(() => ({
  width: `${dispW.value}px`,
  height: `${dispH.value}px`,
  transform: `translate(calc(-50% + ${offset.value.x}px), calc(-50% + ${offset.value.y}px))`,
}))

/** 量取景框尺寸并重置视图（App 端无 clientWidth，用节点查询） */
function measureAppFrame(done?: () => void) {
  if (!instance || !natural.value.w) return
  uni
    .createSelectorQuery()
    .in(instance)
    .select('#crop-stage')
    .boundingClientRect((rect) => {
      const box = rect as { width?: number; height?: number } | null
      if (!box?.width || !box.height) return
      frame.value = { w: box.width, h: box.height }
      baseScale = Math.max(frame.value.w / natural.value.w, frame.value.h / natural.value.h)
      zoom.value = 1
      offset.value = { x: 0, y: 0 }
      done?.()
    })
    .exec()
}

interface TouchPoint {
  clientX?: number
  pageX?: number
  clientY?: number
  pageY?: number
}

function touchPoints(e: any): Array<{ x: number; y: number }> {
  return (e?.touches ?? []).map((t: TouchPoint) => ({
    x: Number(t.clientX ?? t.pageX ?? 0),
    y: Number(t.clientY ?? t.pageY ?? 0),
  }))
}

let appPanStart: { x: number; y: number; ox: number; oy: number } | null = null
let appPinchStart: { dist: number; zoom: number } | null = null

function onAppTouchStart(e: any) {
  const pts = touchPoints(e)
  if (pts.length >= 2) {
    appPinchStart = {
      dist: Math.max(1, Math.hypot(pts[0].x - pts[1].x, pts[0].y - pts[1].y)),
      zoom: zoom.value,
    }
    appPanStart = null
  } else if (pts.length === 1) {
    appPanStart = { x: pts[0].x, y: pts[0].y, ox: offset.value.x, oy: offset.value.y }
    appPinchStart = null
  }
}

function onAppTouchMove(e: any) {
  const pts = touchPoints(e)
  if (pts.length >= 2 && appPinchStart) {
    const dist = Math.max(1, Math.hypot(pts[0].x - pts[1].x, pts[0].y - pts[1].y))
    setZoom(appPinchStart.zoom * clamp(dist / appPinchStart.dist, 0.2, 5))
    return
  }
  if (pts.length === 1 && appPanStart) {
    offset.value = { x: appPanStart.ox + (pts[0].x - appPanStart.x), y: appPanStart.oy + (pts[0].y - appPanStart.y) }
    clampOffset()
  }
}

function onAppTouchEnd(e: any) {
  const pts = touchPoints(e)
  if (pts.length >= 2) {
    appPinchStart = {
      dist: Math.max(1, Math.hypot(pts[0].x - pts[1].x, pts[0].y - pts[1].y)),
      zoom: zoom.value,
    }
    appPanStart = null
  } else if (pts.length === 1) {
    appPanStart = { x: pts[0].x, y: pts[0].y, ox: offset.value.x, oy: offset.value.y }
    appPinchStart = null
  } else {
    appPanStart = null
    appPinchStart = null
  }
}

/** App 端确认裁剪：按可见区域在 uni canvas 上绘制，导出临时文件交给上层 */
async function confirmCropApp() {
  if (!loaded.value || exporting.value || !instance) return
  exporting.value = true
  try {
    const k = natural.value.w / dispW.value
    const sw = Math.min(natural.value.w, (frame.value.w / dispW.value) * natural.value.w)
    const sh = Math.min(natural.value.h, (frame.value.h / dispH.value) * natural.value.h)
    const sx = clamp(((frame.value.w - dispW.value) / 2 + offset.value.x) * k, 0, natural.value.w - sw)
    const sy = clamp(((frame.value.h - dispH.value) / 2 + offset.value.y) * k, 0, natural.value.h - sh)
    const scale = Math.min(1, MAX_EXPORT_EDGE / Math.max(sw, sh))
    const outW = Math.max(1, Math.round(sw * scale))
    const outH = Math.max(1, Math.round(sh * scale))
    exportW.value = outW
    exportH.value = outH
    await nextTick()

    const ctx = uni.createCanvasContext('sm-crop-canvas', instance)
    ctx.drawImage(props.src, sx, sy, sw, sh, 0, 0, outW, outH)
    await new Promise<void>((resolve) => {
      ctx.draw(false, () => resolve())
    })
    const result = await new Promise<{ tempFilePath: string }>((resolve, reject) => {
      uni.canvasToTempFilePath(
        {
          canvasId: 'sm-crop-canvas',
          x: 0,
          y: 0,
          width: outW,
          height: outH,
          destWidth: outW,
          destHeight: outH,
          fileType: 'jpg',
          quality: 0.85,
          success: (res) => resolve(res),
          fail: reject,
        },
        instance,
      )
    })
    // 取色失败不阻断流程：返回 undefined 时上层走默认色板兜底
    const palette = await extractPaletteApp()
    emit('confirm', result.tempFilePath, palette)
  } catch {
    uni.showToast({ title: t('wallpaper.exportFailed'), icon: 'none' })
  } finally {
    exporting.value = false
  }
}

/* ---------- 导出：按可见区域裁剪并压缩 ---------- */

async function confirmCrop() {
  if (!loaded.value || exporting.value || !decoded) return
  exporting.value = true
  try {
    const k = natural.value.w / dispW.value
    // 取景窗口对应的原图区域（钳制到合法范围，抵消 dispW/dispH 取整的亚像素误差）
    const sw = Math.min(natural.value.w, (frame.value.w / dispW.value) * natural.value.w)
    const sh = Math.min(natural.value.h, (frame.value.h / dispH.value) * natural.value.h)
    const sx = clamp(((frame.value.w - dispW.value) / 2 + offset.value.x) * k, 0, natural.value.w - sw)
    const sy = clamp(((frame.value.h - dispH.value) / 2 + offset.value.y) * k, 0, natural.value.h - sh)
    const scale = Math.min(1, MAX_EXPORT_EDGE / Math.max(sw, sh))
    const outW = Math.max(1, Math.round(sw * scale))
    const outH = Math.max(1, Math.round(sh * scale))

    const canvas = document.createElement('canvas')
    canvas.width = outW
    canvas.height = outH
    const ctx = canvas.getContext('2d')
    if (!ctx) return
    ctx.imageSmoothingQuality = 'high'
    ctx.drawImage(decoded, sx, sy, sw, sh, 0, 0, outW, outH)

    const encoded = await new Promise<Blob | null>((resolve) =>
      canvas.toBlob(resolve, 'image/webp', EXPORT_QUALITY),
    )
    const blob =
      encoded && encoded.type === 'image/webp'
        ? encoded
        : await new Promise<Blob | null>((resolve) =>
            canvas.toBlob(resolve, 'image/jpeg', EXPORT_QUALITY),
          )
    if (!blob) return
    emit('confirm', URL.createObjectURL(blob))
  } finally {
    exporting.value = false
  }
}
</script>

<template>
  <view class="cropper-mask">
    <view class="cropper-top">
      <text class="cp-btn" @click="emit('cancel')">{{ $t('wallpaper.cancel') }}</text>
      <text class="cp-title">{{ $t('wallpaper.title') }}</text>
      <!-- #ifdef H5 -->
      <text class="cp-btn primary" @click="confirmCrop">{{ exporting ? $t('wallpaper.processing') : $t('wallpaper.done') }}</text>
      <!-- #endif -->
      <!-- #ifdef APP-PLUS -->
      <text class="cp-btn primary" @click="confirmCropApp">{{ exporting ? $t('wallpaper.processing') : $t('wallpaper.done') }}</text>
      <!-- #endif -->
    </view>
    <!-- #ifdef H5 -->
    <div
      ref="viewport"
      class="cropper-stage"
      @pointerdown="onPointerDown"
      @pointermove="onPointerMove"
      @pointerup="onPointerUp"
      @pointercancel="onPointerUp"
      @wheel.prevent="onWheel"
    >
      <img v-if="loaded" class="crop-img" :src="props.src" :style="imgStyle" draggable="false" alt="" />
      <view v-else class="crop-loading">{{ $t('wallpaper.loading') }}</view>
    </div>
    <!-- #endif -->
    <!-- #ifdef APP-PLUS -->
    <view
      id="crop-stage"
      class="cropper-stage"
      @touchstart.stop.prevent="onAppTouchStart"
      @touchmove.stop.prevent="onAppTouchMove"
      @touchend.stop.prevent="onAppTouchEnd"
      @touchcancel.stop="onAppTouchEnd"
    >
      <img v-if="loaded" class="crop-img" :src="props.src" mode="scaleToFill" :style="appImgStyle" />
      <view v-else class="crop-loading">{{ $t('wallpaper.loading') }}</view>
    </view>
    <!-- 导出画布：移出可视区域，仅用于 canvasToTempFilePath -->
    <canvas
      id="sm-crop-canvas"
      canvas-id="sm-crop-canvas"
      class="crop-canvas"
      :style="{ width: exportW + 'px', height: exportH + 'px' }"
    ></canvas>
    <!-- 取色画布：缩绘到 96px 后读像素生成色板 -->
    <canvas
      id="sm-palette-canvas"
      canvas-id="sm-palette-canvas"
      class="crop-canvas"
      :style="{ width: paletteW + 'px', height: paletteH + 'px' }"
    ></canvas>
    <!-- #endif -->
    <view class="cropper-bottom">
      <text class="hint">{{ $t('wallpaper.gestureHint') }}</text>
      <text class="reset" @click="resetView">{{ $t('wallpaper.reset') }}</text>
    </view>
  </view>
</template>

<style lang="scss" scoped>
/* 裁剪编辑器：深色沉浸蒙层，自身使用主题令牌之外的固定深色（不随主题变化） */
.cropper-mask {
  position: fixed;
  inset: 0;
  z-index: 999;
  display: flex;
  flex-direction: column;
  background: #0c0e0c;
}

.cropper-top {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 24rpx 30rpx;
  flex: none;
}

.cp-btn {
  min-width: 110rpx;
  color: rgba(255, 255, 255, 0.75);
  font-size: 26rpx;
  text-align: center;
}

.cp-btn.primary {
  min-width: 110rpx;
  padding: 8rpx 22rpx;
  border-radius: 999rpx;
  background: var(--sm-accent, #5be07a);
  color: #10131a;
  font-size: 25rpx;
  font-weight: 700;
}

.cp-title {
  color: rgba(255, 255, 255, 0.92);
  font-size: 28rpx;
  font-weight: 700;
}

/* 取景框：上下栏之间的全部区域，比例即输出壁纸比例 */
.cropper-stage {
  position: relative;
  flex: 1;
  overflow: hidden;
  touch-action: none;
  cursor: grab;
}

.cropper-stage:active {
  cursor: grabbing;
}

.crop-img {
  position: absolute;
  left: 50%;
  top: 50%;
  transform-origin: center;
  user-select: none;
  -webkit-user-drag: none;
}

.crop-loading {
  position: absolute;
  inset: 0;
  display: grid;
  place-items: center;
  color: rgba(255, 255, 255, 0.55);
  font-size: 24rpx;
}

/* 导出画布：必须在 DOM 中才可被 canvasToTempFilePath 读取，移出可视区域即可 */
.crop-canvas {
  position: fixed;
  left: -9999px;
  top: 0;
}

.cropper-bottom {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 22rpx 30rpx 34rpx;
  flex: none;
}

.hint {
  color: rgba(255, 255, 255, 0.5);
  font-size: 22rpx;
}

.reset {
  padding: 8rpx 24rpx;
  border: 1rpx solid rgba(255, 255, 255, 0.3);
  border-radius: 999rpx;
  color: rgba(255, 255, 255, 0.85);
  font-size: 23rpx;
}
</style>
