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
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'

const props = defineProps<{ src: string }>()
const emit = defineEmits<{ (e: 'cancel'): void; (e: 'confirm', url: string): void }>()

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

onMounted(() => {
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
})

onBeforeUnmount(() => window.removeEventListener('resize', handleResize))

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
      <text class="cp-btn" @tap="emit('cancel')">取消</text>
      <text class="cp-title">调整壁纸区域</text>
      <text class="cp-btn primary" @tap="confirmCrop">{{ exporting ? '处理中…' : '完成' }}</text>
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
      <view v-else class="crop-loading">图片加载中…</view>
    </div>
    <!-- #endif -->
    <view class="cropper-bottom">
      <text class="hint">拖动调整位置 · 双指或滚轮缩放</text>
      <text class="reset" @tap="resetView">重置</text>
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
