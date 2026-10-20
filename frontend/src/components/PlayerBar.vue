<script setup lang="ts">
/**
 * PlayerBar：全局播放器组件（底部迷你播放条 + 点击展开的全屏播放页）。
 * 消费 services/player 的全局单例状态 usePlayer()。
 *
 * 增强点：
 * - 视觉：封面黑胶唱片旋转（播放时转、暂停时停）、从封面提取主色做动态背景、律动频谱条；
 * - 歌词：沉浸模式（大字居中、逐行缩放渐隐、点击行跳转），可切换封面/歌词视图；
 * - 手势：封面区左右滑切歌、下滑关闭、双击收藏、单击切换歌词模式；
 * - 行为：收藏（library）、不感兴趣（feedback），播放/跳过/播完由 player 统一上报。
 */
import { computed, getCurrentInstance, nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AlignLeft,
  AudioLines,
  Ban,
  ChevronDown,
  Heart,
  ListOrdered,
  ListMusic,
  ListPlus,
  MessageSquare,
  Pause,
  Play,
  Repeat1,
  Shuffle,
  SlidersHorizontal,
  SkipBack,
  SkipForward,
  X,
} from 'lucide-vue-next'
import { cyclePlayMode, nextTrack, playFromQueue, previousTrack, seek, togglePlayback, usePlayer } from '@/services/player'
import { EQ_PRESETS } from '@/services/eq-presets'
import { addPlaylistTrack, favoriteIds, getPlaylists, toggleFavorite } from '@/services/library'
import { setFeedback } from '@/services/feedback'
import { isAuthenticated, requireLogin } from '@/services/session'
import { formatTime } from '@/utils/format'
import CoverArt from './CoverArt.vue'

const { state, setEqPreset } = usePlayer() // 全局播放器单例状态
const { t } = useI18n()
const expanded = ref(false) // 全屏播放器是否展开
const lyricMode = ref(false) // 是否处于沉浸歌词模式（隐藏封面、歌词铺满）
const eqPanelVisible = ref(false) // 音效预设选择弹层是否可见
const queuePanelVisible = ref(false) // 播放队列面板是否可见

/** 当前音效预设的本地化名称（音效按钮文案） */
const eqPresetName = computed(() => t(`player.eqPresets.${state.eqPreset}`))

/** 是否位于有自定义 tabbar 的页面（首页）：影响底部偏移量（132rpx 预留首页 tabbar 中间凸起按钮的高度） */
const props = withDefaults(defineProps<{ withTabbar?: boolean }>(), { withTabbar: false })
const bottomOffset = computed(() => `calc(env(safe-area-inset-bottom) + ${props.withTabbar ? 132 : 24}rpx)`)

// 播放进度百分比（0-100），无总时长时按 0 处理
const progress = computed(() => {
  const total = state.currentTrack?.duration || 0
  if (!total) return 0
  return Math.min(100, (state.currentTime / total) * 100)
})
const elapsedLabel = computed(() => formatTime(state.currentTime))
const durationLabel = computed(() => formatTime(state.currentTrack?.duration ?? 0))
const artistAlbumLabel = computed(() =>
  state.currentTrack?.album
    ? `${state.currentTrack.artist} · ${state.currentTrack.album}`
    : state.currentTrack?.artist ?? '',
)
const liked = computed(() =>
  state.currentTrack ? favoriteIds.value.has(state.currentTrack.id) : false,
)

// ===== 封面主色提取（仅 H5；失败或非 H5 时回退主题背景） =====
const palette = ref<{ c1: string; c2: string } | null>(null)

/** 把 0-255 通道转为 #RRGGBB。 */
function toHex(r: number, g: number, b: number): string {
  const hex = (value: number) => Math.max(0, Math.min(255, Math.round(value))).toString(16).padStart(2, '0')
  return `#${hex(r)}${hex(g)}${hex(b)}`
}

/** #RRGGBB + alpha -> rgba(...)。 */
function hexToRgba(hex: string, alpha: number): string {
  const value = hex.replace('#', '')
  const r = parseInt(value.slice(0, 2), 16)
  const g = parseInt(value.slice(2, 4), 16)
  const b = parseInt(value.slice(4, 6), 16)
  return `rgba(${r}, ${g}, ${b}, ${alpha})`
}

/**
 * 从图片 URL 提取两个主色（饱和度/亮度评分最高的色 + 平均色）。
 * 仅 H5 有效：使用 canvas 采样；跨域图片会因画布污染失败，此时返回 null。
 */
function extractPalette(url: string): Promise<{ c1: string; c2: string } | null> {
  return new Promise((resolve) => {
    // #ifdef H5
    try {
      const doc = (globalThis as any).document
      const ImageCtor = (globalThis as any).Image
      if (!doc || !ImageCtor) {
        resolve(null)
        return
      }
      const img = new ImageCtor()
      img.crossOrigin = 'anonymous'
      img.onload = () => {
        try {
          const size = 24
          const canvas = doc.createElement('canvas')
          canvas.width = size
          canvas.height = size
          const context = canvas.getContext('2d')
          if (!context) {
            resolve(null)
            return
          }
          context.drawImage(img, 0, 0, size, size)
          const { data } = context.getImageData(0, 0, size, size)
          let sumR = 0
          let sumG = 0
          let sumB = 0
          let count = 0
          let best = { r: 0, g: 0, b: 0, score: -1 }
          for (let i = 0; i < data.length; i += 4) {
            if (data[i + 3] < 128) continue
            const r = data[i]
            const g = data[i + 1]
            const b = data[i + 2]
            sumR += r
            sumG += g
            sumB += b
            count++
            const max = Math.max(r, g, b)
            const min = Math.min(r, g, b)
            const saturation = max === 0 ? 0 : (max - min) / max
            const luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255
            const score = saturation * 0.75 + luminance * 0.25
            if (score > best.score) best = { r, g, b, score }
          }
          if (!count) {
            resolve(null)
            return
          }
          resolve({
            c1: toHex(best.r, best.g, best.b),
            c2: toHex(sumR / count, sumG / count, sumB / count),
          })
        } catch {
          resolve(null)
        }
      }
      img.onerror = () => resolve(null)
      img.src = url
    } catch {
      resolve(null)
    }
    // #endif
    // #ifndef H5
    resolve(null)
    // #endif
  })
}

// 曲目封面变化时重新取色（封面为空则清空，回退主题背景）
watch(
  () => state.currentTrack?.cover,
  (cover) => {
    palette.value = null
    if (!cover) return
    void extractPalette(cover).then((result) => {
      if (state.currentTrack?.cover === cover) palette.value = result
    })
  },
)

/** 全屏页动态背景：叠加封面取色的径向渐变，无取色时用主题背景。 */
const overlayStyle = computed(() => {
  if (!palette.value) return {}
  return {
    backgroundColor: 'var(--sm-bg)',
    backgroundImage: `radial-gradient(120% 70% at 50% 0%, ${hexToRgba(palette.value.c1, 0.42)} 0%, transparent 62%), radial-gradient(120% 80% at 50% 100%, ${hexToRgba(palette.value.c2, 0.24)} 0%, transparent 60%)`,
  }
})

// ===== 歌词展示 =====
interface LyricLine { time: number; text: string }

/** 解析 LRC 字符串为带时间戳（秒）的行数组；非 LRC 行原样保留，time 为 -1 */
function parseLrc(content: string): LyricLine[] {
  const lines = content.split(/\r?\n/)
  const result: LyricLine[] = []
  for (const line of lines) {
    const matches = [...line.matchAll(/\[(\d{1,2}):(\d{1,2})(?:\.(\d{1,3}))?\]/g)]
    if (!matches.length) {
      const text = line.trim()
      if (text) result.push({ time: -1, text })
      continue
    }
    const lastTagEnd = matches[matches.length - 1].index! + matches[matches.length - 1][0].length
    const text = line.slice(lastTagEnd).trim()
    for (const m of matches) {
      const minutes = Number(m[1])
      const seconds = Number(m[2])
      const ms = m[3] ? Number(m[3].padEnd(3, '0')) : 0
      result.push({ time: minutes * 60 + seconds + ms / 1000, text })
    }
  }
  return result.sort((a, b) => a.time - b.time)
}

const lyricLines = computed<LyricLine[]>(() => {
  const lyric = state.currentLyric
  if (!lyric) return []
  if (lyric.format === 'lrc') return parseLrc(lyric.content)
  return lyric.content.split(/\r?\n/).map((text) => ({ time: -1, text: text.trim() })).filter((l) => l.text)
})

/** 当前高亮行（仅 LRC 有效；纯文本返回 -1） */
const activeLyricIndex = computed(() => {
  if (state.currentLyric?.format !== 'lrc') return -1
  const lines = lyricLines.value
  let idx = -1
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].time >= 0 && lines[i].time <= state.currentTime) idx = i
    else if (lines[i].time > state.currentTime) break
  }
  return idx
})

// ===== 歌词滚动：让当前行始终垂直居中 =====
// scroll-into-view 只会把目标行对齐到顶部，这里改为按“行中心 = 视口中心”计算 scroll-top。
const lyricScrollTop = ref(0) // 绑定到 scroll-view 的 scroll-top
const lyricPad = ref(0) // 上下留白高度，保证首/末行也能滚到中间
let lyricScrollPos = 0 // 记录当前实际滚动位置（由 @scroll 回写）

/** scroll-view 滚动回调：记录当前位置，供居中计算使用。 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function onLyricScroll(event: any) {
  lyricScrollPos = event?.detail?.scrollTop ?? 0
}

/**
 * 重新测量歌词区：始终刷新上下留白（= 半个视口高，保证首/末行可居中）；
 * 若已有高亮行，则把该行滚动到视口垂直中央。
 */
function centerActiveLyric() {
  const index = activeLyricIndex.value
  const query = uni.createSelectorQuery().in(instance).select('.lyric-view').boundingClientRect()
  if (index >= 0) query.select(`#lyric-line-${index}`).boundingClientRect()
  query.exec((result) => {
    const view = Array.isArray(result[0]) ? result[0][0] : result[0]
    if (!view) return
    const pad = Math.max(0, Math.ceil(view.height / 2))
    if (pad !== lyricPad.value) lyricPad.value = pad
    if (index < 0) return
    const line = Array.isArray(result[1]) ? result[1][0] : result[1]
    if (!line) return
    // 目标滚动量：让行中心落在视口中心
    const delta = line.top - view.top - (view.height - line.height) / 2
    const next = Math.max(0, Math.round(lyricScrollPos + delta))
    if (next !== lyricScrollTop.value) lyricScrollTop.value = next
  })
}

// 高亮行变化时重新居中（等 DOM 更新后再测量）
watch(activeLyricIndex, () => {
  void nextTick(() => centerActiveLyric())
})

// 切换曲目/歌词时复位滚动位置并重新居中
watch(
  () => state.currentLyric,
  () => {
    lyricScrollPos = 0
    lyricScrollTop.value = 0
    void nextTick(() => centerActiveLyric())
  },
)

// 歌词内容变化 / 展开全屏 / 切换歌词模式时，尺寸或内容改变，需重新测量
watch(
  () => lyricLines.value.length,
  () => {
    void nextTick(() => centerActiveLyric())
  },
)
watch(expanded, (open) => {
  if (open) void nextTick(() => centerActiveLyric())
})
watch(lyricMode, () => {
  void nextTick(() => centerActiveLyric())
})

// ===== 律动频谱条（视觉动效，非真实频谱；播放时律动、暂停时静置） =====
// 每根条的样式一次性预计算：与响应式状态无关，避免播放中每次重渲染都重建 26 个样式对象。
const barStyles = Array.from({ length: 26 }, (_, index) => ({
  height: `${30 + ((index * 37) % 60)}%`,
  animationDuration: `${0.55 + (index % 5) * 0.14}s`,
  animationDelay: `${(index % 7) * 0.07}s`,
}))

// ===== 交互 =====
/** 展开全屏播放器 */
function openPlayer() {
  expanded.value = true
}

/** 切换沉浸歌词模式 */
function toggleLyricMode() {
  lyricMode.value = !lyricMode.value
}

/** 选择音效预设并立即应用、关闭弹层 */
function pickEqPreset(id: string) {
  setEqPreset(id)
  eqPanelVisible.value = false
}

/** 播放队列面板点击某曲：从该下标开始播放并关闭面板 */
function playAt(index: number) {
  playFromQueue(index)
  queuePanelVisible.value = false
}

/** 收藏 / 取消收藏当前曲目 */
async function handleLike() {
  if (!state.currentTrack) return
  if (!isAuthenticated.value) {
    requireLogin()
    return
  }
  try {
    await toggleFavorite(state.currentTrack)
  } catch {
    uni.showToast({ title: '操作失败，请稍后再试', icon: 'none' })
  }
}

/** 打开当前曲目的评论页（评论区支持未登录浏览、登录后评论）。
 *  注意：不收起播放器，navigateTo 后原页面组件保留，返回时全屏播放器仍在展开状态。 */
function openComments() {
  if (!state.currentTrack) return
  uni.navigateTo({ url: `/pages/track/detail?id=${state.currentTrack.id}` })
}

/** 标记当前曲目“不感兴趣”，随后切到下一首 */
async function markNotInterested() {
  if (!state.currentTrack || !requireLogin()) return
  const trackId = state.currentTrack.id
  try {
    await setFeedback(trackId, 'not_interested')
    uni.showToast({ title: '已减少此类推荐', icon: 'none' })
    nextTrack()
  } catch {
    uni.showToast({ title: '操作失败，请稍后再试', icon: 'none' })
  }
}

/** 加入歌单：登录拦截后拉取我的歌单，通过 ActionSheet 选择目标歌单 */
async function addToPlaylist() {
  if (!state.currentTrack || !requireLogin()) return
  try {
    const playlists = (await getPlaylists()).items
    if (!playlists.length) {
      uni.showToast({ title: '先在“收藏”里创建一个歌单', icon: 'none' })
      return
    }
    uni.showActionSheet({
      itemList: playlists.map((playlist) => playlist.name),
      success: async ({ tapIndex }) => {
        const playlist = playlists[tapIndex]
        if (!playlist) return
        await addPlaylistTrack(playlist.id, state.currentTrack!.id)
        uni.showToast({ title: '已加入歌单', icon: 'none' })
      },
    })
  } catch {
    uni.showToast({ title: '操作失败，请稍后再试', icon: 'none' })
  }
}

// —— 封面区手势：左右滑切歌 / 下滑关闭 / 单击切换歌词模式 / 双击收藏 ——
let gestureStartX = 0
let gestureStartY = 0
let gestureMoved = false
let tapTimer: ReturnType<typeof setTimeout> | undefined

function onArtTouchStart(event: TouchEvent) {
  const touch = event.touches[0]
  if (!touch) return
  gestureStartX = touch.clientX
  gestureStartY = touch.clientY
  gestureMoved = false
}

function onArtTouchMove(event: TouchEvent) {
  const touch = event.touches[0]
  if (!touch) return
  if (Math.abs(touch.clientX - gestureStartX) > 12 || Math.abs(touch.clientY - gestureStartY) > 12) {
    gestureMoved = true
  }
}

function onArtTouchEnd(event: TouchEvent) {
  const touch = event.changedTouches[0]
  if (!touch) return
  const dx = touch.clientX - gestureStartX
  const dy = touch.clientY - gestureStartY
  // 横向滑动：左滑下一首、右滑上一首
  if (Math.abs(dx) > 60 && Math.abs(dx) > Math.abs(dy)) {
    if (dx < 0) nextTrack()
    else previousTrack()
    return
  }
  // 下滑：关闭全屏播放器
  if (dy > 90 && Math.abs(dy) > Math.abs(dx)) {
    expanded.value = false
    return
  }
  // 未产生位移视为点击：双击收藏，单击切换歌词模式
  if (!gestureMoved) {
    if (tapTimer) {
      clearTimeout(tapTimer)
      tapTimer = undefined
      void handleLike()
      return
    }
    tapTimer = setTimeout(() => {
      tapTimer = undefined
      toggleLyricMode()
    }, 260)
  }
}

// —— 进度条拖动 seek：缓存进度条节点位置，按触点横坐标换算播放位置 ——
const instance = getCurrentInstance()
let seekRect: { left: number; width: number } | null = null

function fractionFromEvent(event: TouchEvent): number | null {
  if (!seekRect || !event.changedTouches.length) return null
  const x = event.changedTouches[0].clientX
  return Math.min(1, Math.max(0, (x - seekRect.left) / seekRect.width))
}

function onSeekStart(event: TouchEvent) {
  uni
    .createSelectorQuery()
    .in(instance)
    .select('.seek-track')
    .boundingClientRect((rect) => {
      const nodeRect = Array.isArray(rect) ? rect[0] : rect
      if (nodeRect && typeof nodeRect.left === 'number' && typeof nodeRect.width === 'number') {
        seekRect = { left: nodeRect.left, width: nodeRect.width }
        const fraction = fractionFromEvent(event)
        if (fraction !== null && state.currentTrack) seek(state.currentTrack.duration * fraction)
      }
    })
    .exec()
}

function onSeekMove(event: TouchEvent) {
  const fraction = fractionFromEvent(event)
  if (fraction !== null && state.currentTrack) seek(state.currentTrack.duration * fraction)
}
</script>

<template>
  <view v-if="state.currentTrack">
    <!-- 底部迷你播放条：点击展开全屏页，内部按钮 @tap.stop 不触发展开 -->
    <view class="now-playing" :style="{ bottom: bottomOffset }" @tap="openPlayer">
      <view class="now-progress"><view :style="{ width: `${progress}%` }"></view></view>
      <view class="now-cover"><CoverArt :src="state.currentTrack.cover" :seed="state.currentTrack.title" :rounded="13" :icon-size="16" /></view>
      <view class="now-copy">
        <text class="now-title">{{ state.currentTrack.title }}</text>
        <text class="now-artist">{{ state.currentTrack.artist }}</text>
      </view>
      <view class="now-live">
        <view class="mini-eq" :class="{ paused: !state.isPlaying }"><view></view><view></view><view></view></view>
      </view>
      <view class="now-play" @tap.stop="togglePlayback">
        <Pause v-if="state.isPlaying" :size="21" fill="currentColor" />
        <Play v-else :size="21" fill="currentColor" />
      </view>
      <view class="now-next" @tap.stop="nextTrack"><SkipForward :size="19" /></view>
    </view>

    <!-- 全屏播放器弹层：动态背景 + 黑胶封面 / 沉浸歌词 + 频谱 + 控制区 -->
    <view v-if="expanded" class="player-overlay" :style="overlayStyle">
      <!-- 顶栏：收起 / 歌词模式切换 / 关闭 -->
      <view class="player-topbar">
        <view class="player-icon-button" @tap="expanded = false"><ChevronDown :size="23" /></view>
        <view class="player-top-label">
          <text>正在播放</text>
          <text class="player-context">{{ lyricMode ? '歌词模式' : 'Shine Music' }}</text>
        </view>
        <view class="player-icon-button" :class="{ active: lyricMode }" @tap="toggleLyricMode"><AlignLeft :size="20" /></view>
      </view>

      <!-- 舞台：封面（可手势）或沉浸歌词 -->
      <view class="player-stage" :class="{ immersive: lyricMode }">
        <!-- 黑胶封面：播放时旋转；支持左右滑切歌、下滑关闭、单击切歌词、双击收藏 -->
        <view v-if="!lyricMode" class="player-art-block">
          <view
            class="player-art-wrap"
            @touchstart="onArtTouchStart"
            @touchmove="onArtTouchMove"
            @touchend="onArtTouchEnd"
          >
            <view class="player-disc" :class="{ playing: state.isPlaying }">
              <CoverArt :src="state.currentTrack.cover" :seed="state.currentTrack.title" :rounded="0" :icon-size="44" />
              <view class="disc-hole"></view>
            </view>
            <view class="art-sticker"><AudioLines :size="16" /><text>SHINE SESSION</text></view>
          </view>
          <view class="art-hint">← 切歌 · 下滑收起 · 双击收藏 →</view>
        </view>

        <!-- 曲名/艺人 + 收藏（封面模式下显示） -->
        <view v-if="!lyricMode" class="player-info">
          <view class="player-info-copy">
            <text class="player-song-title">{{ state.currentTrack.title }}</text>
            <text class="player-song-artist">{{ artistAlbumLabel }}</text>
          </view>
          <view class="player-like" :class="{ liked }" @tap="handleLike">
            <Heart :size="24" :fill="liked ? 'currentColor' : 'none'" />
          </view>
        </view>

        <!-- 歌词区：LRC 逐行高亮，沉浸模式下铺满并放大；点击行跳转 -->
        <scroll-view
          v-if="state.currentLyric"
          class="lyric-view"
          scroll-y
          :scroll-top="lyricScrollTop"
          :scroll-with-animation="true"
          @scroll="onLyricScroll"
        >
          <!-- 顶部留白：保证第一行也能滚到中间 -->
          <view class="lyric-spacer" :style="{ height: `${lyricPad}px` }"></view>
          <view
            v-for="(line, index) in lyricLines"
            :key="index"
            :id="`lyric-line-${index}`"
            class="lyric-line"
            :class="{ active: index === activeLyricIndex, clickable: line.time >= 0 }"
            @tap="line.time >= 0 ? seek(line.time) : undefined"
          >{{ line.text }}</view>
          <!-- 底部留白：保证最后一行也能滚到中间 -->
          <view class="lyric-spacer" :style="{ height: `${lyricPad}px` }"></view>
        </scroll-view>
        <view v-else class="lyric-empty">暂无歌词</view>
      </view>

      <!-- 律动频谱条：播放时跳动，暂停时静置 -->
      <view class="visualizer" :class="{ paused: !state.isPlaying }">
        <view v-for="(style, index) in barStyles" :key="index" class="vbar" :style="style"></view>
      </view>

      <!-- 进度条：touchstart/touchmove 拖动 seek -->
      <view class="seek-area">
        <view class="seek-track" @touchstart="onSeekStart" @touchmove="onSeekMove">
          <view :style="{ width: `${progress}%` }"></view>
          <view class="seek-knob" :style="{ left: `${progress}%` }"></view>
        </view>
        <view class="seek-times"><text>{{ elapsedLabel }}</text><text>{{ durationLabel }}</text></view>
      </view>

      <!-- 播放控制：播放模式（单按钮三态）/ 加歌单 / 上一首 / 播放暂停 / 下一首 / 收藏 -->
      <view class="player-controls">
        <view class="control-secondary" :class="{ active: state.playMode !== 'order' }" @tap="cyclePlayMode">
          <ListOrdered v-if="state.playMode === 'order'" :size="22" />
          <Shuffle v-else-if="state.playMode === 'shuffle'" :size="22" />
          <Repeat1 v-else :size="22" />
        </view>
        <view class="control-secondary" @tap="queuePanelVisible = true"><ListMusic :size="22" /></view>
        <view class="control-skip" @tap="previousTrack"><SkipBack :size="24" fill="currentColor" /></view>
        <view class="control-play" @tap="togglePlayback">
          <Pause v-if="state.isPlaying" :size="27" fill="currentColor" />
          <Play v-else :size="27" fill="currentColor" />
        </view>
        <view class="control-skip" @tap="nextTrack"><SkipForward :size="24" fill="currentColor" /></view>
        <view class="control-secondary" :class="{ liked }" @tap="handleLike">
          <Heart :size="21" :fill="liked ? 'currentColor' : 'none'" />
        </view>
      </view>

      <!-- 评论 / 音效 / 不感兴趣：音效仅 H5（Web Audio）可用时展示 -->
      <view class="player-extra">
        <view class="extra-button extra-button--primary" @tap="openComments"><MessageSquare :size="17" /><text>评论</text></view>
        <view class="extra-button" @tap="addToPlaylist"><ListPlus :size="17" /><text>加歌单</text></view>
        <view v-if="state.eqSupported" class="extra-button" :class="{ active: state.eqPreset !== 'flat' }" @tap="eqPanelVisible = true">
          <SlidersHorizontal :size="17" /><text>{{ t('player.eq') }} · {{ eqPresetName }}</text>
        </view>
        <view class="extra-button" @tap="markNotInterested"><Ban :size="17" /><text>不感兴趣</text></view>
      </view>

      <view v-if="state.audioError" class="audio-notice">暂时无法播放该曲目，请稍后再试</view>
    </view>

    <!-- 播放队列面板（底部抽屉） -->
    <view v-if="queuePanelVisible" class="eq-sheet-mask" @tap="queuePanelVisible = false">
      <view class="eq-sheet queue-sheet" @tap.stop>
        <view class="eq-sheet-head">
          <text class="eq-sheet-title">播放队列（{{ state.queue.length }}）</text>
          <view class="eq-sheet-close" @tap="queuePanelVisible = false"><X :size="20" /></view>
        </view>
        <scroll-view scroll-y class="queue-scroll">
          <view
            v-for="(track, index) in state.queue"
            :key="track.id"
            class="queue-item"
            :class="{ current: index === state.queueIndex }"
            @tap="playAt(index)"
          >
            <text class="queue-index">{{ index === state.queueIndex ? '▶' : index + 1 }}</text>
            <view class="queue-copy">
              <text class="queue-title">{{ track.title }}</text>
              <text class="queue-artist">{{ track.artist }}</text>
            </view>
          </view>
        </scroll-view>
      </view>
    </view>

    <!-- 音效预设选择弹层（底部抽屉） -->
    <view v-if="eqPanelVisible" class="eq-sheet-mask" @tap="eqPanelVisible = false">
      <view class="eq-sheet" @tap.stop>
        <view class="eq-sheet-head">
          <text class="eq-sheet-title">{{ t('player.eq') }}</text>
          <view class="eq-sheet-close" @tap="eqPanelVisible = false"><X :size="20" /></view>
        </view>
        <view class="eq-grid">
          <view
            v-for="p in EQ_PRESETS"
            :key="p.id"
            class="eq-chip"
            :class="{ active: state.eqPreset === p.id }"
            @tap="pickEqPreset(p.id)"
          >{{ t('player.eqPresets.' + p.id) }}</view>
        </view>
      </view>
    </view>
  </view>
</template>

<style lang="scss" scoped>
/* 播放器：迷你条毛玻璃 + 全屏动态背景 + 黑胶旋转 + 律动频谱 + 沉浸歌词 */
.now-playing { position: fixed; z-index: 4; right: 20rpx; bottom: calc(env(safe-area-inset-bottom) + 132rpx); left: 20rpx; display: flex; align-items: center; height: 104rpx; gap: 17rpx; padding: 13rpx 18rpx; border: 1rpx solid var(--sm-border-strong); border-radius: 20rpx; background: var(--sm-surface-3); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); box-shadow: 0 12rpx 34rpx var(--sm-art-shadow); }
.now-progress { position: absolute; top: 0; right: 20rpx; left: 20rpx; height: 3rpx; overflow: hidden; border-radius: 2rpx; background: var(--sm-border); }
.now-progress view { height: 100%; background: var(--sm-accent-grad); }
.now-cover { flex: none; width: 70rpx; height: 70rpx; border-radius: 13rpx; overflow: hidden; background: var(--sm-surface-3); }
.now-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 6rpx; }
.now-title { overflow: hidden; color: var(--sm-text); font-size: 19rpx; font-weight: 700; text-overflow: ellipsis; white-space: nowrap; }
.now-artist { color: var(--sm-text-2); font-size: 16rpx; }
.now-live { display: grid; place-items: center; width: 34rpx; }
.mini-eq { display: flex; align-items: flex-end; gap: 3rpx; height: 26rpx; }
.mini-eq view { width: 4rpx; height: 100%; border-radius: 2rpx; background: var(--sm-accent); transform-origin: bottom; animation: eq .8s ease-in-out infinite; }
.mini-eq view:nth-child(2) { animation-duration: 1.05s; animation-delay: .12s; }
.mini-eq view:nth-child(3) { animation-duration: .7s; animation-delay: .24s; }
.mini-eq.paused view { animation-play-state: paused; transform: scaleY(.35); opacity: .4; }
.now-play, .now-next { display: grid; place-items: center; width: 46rpx; height: 50rpx; color: var(--sm-text); }

.player-overlay { position: fixed; z-index: 10; inset: 0; display: flex; flex-direction: column; padding: calc(env(safe-area-inset-top) + 25rpx) 44rpx calc(env(safe-area-inset-bottom) + 24rpx); background: var(--sm-page-bg); animation: rise-in .28s ease-out; transition: background .6s ease; }
.player-topbar { display: flex; flex: none; align-items: center; justify-content: space-between; }
.player-icon-button { display: grid; place-items: center; width: 52rpx; height: 52rpx; color: var(--sm-text-2); }
.player-icon-button.active { color: var(--sm-accent); }
.player-top-label { display: flex; flex-direction: column; align-items: center; gap: 5rpx; color: var(--sm-text); font-size: 18rpx; font-weight: 700; }
.player-context { color: var(--sm-text-2); font-size: 15rpx; font-weight: 400; }

.player-stage { display: flex; flex: 1 1 auto; min-height: 0; flex-direction: column; overflow: hidden; }
.player-stage.immersive { justify-content: center; }

/* 黑胶封面 */
.player-art-block { display: flex; flex: none; flex-direction: column; align-items: center; margin-top: 20rpx; }
.player-art-wrap { position: relative; width: min(56vw, 400rpx); aspect-ratio: 1; }
.player-disc { position: relative; width: 100%; height: 100%; border-radius: 50%; overflow: hidden; background: var(--sm-surface-3); box-shadow: 0 24rpx 60rpx var(--sm-art-shadow), inset 0 0 0 10rpx var(--sm-border-strong), inset 0 0 0 22rpx var(--sm-bg-soft); animation: spin 22s linear infinite; animation-play-state: paused; }
.player-disc.playing { animation-play-state: running; }
.player-disc :deep(image), .player-disc :deep(img) { width: 100%; height: 100%; }
.disc-hole { position: absolute; top: 50%; left: 50%; width: 20%; height: 20%; border: 6rpx solid var(--sm-bg-soft); border-radius: 50%; background: var(--sm-bg); transform: translate(-50%, -50%); }
.art-sticker { position: absolute; right: 8rpx; bottom: 8rpx; display: flex; align-items: center; gap: 7rpx; padding: 9rpx 13rpx; border-radius: 30rpx; background: var(--sm-overlay-ink); color: var(--sm-text); font-family: 'DM Sans', sans-serif; font-size: 12rpx; font-weight: 700; -webkit-backdrop-filter: blur(10px); backdrop-filter: blur(10px); }
.art-hint { margin-top: 16rpx; color: var(--sm-text-3); font-size: 14rpx; text-align: center; }

.player-info { display: flex; flex: none; align-items: center; justify-content: space-between; gap: 16rpx; margin-top: 30rpx; }
.player-info-copy { display: flex; min-width: 0; flex-direction: column; }
.player-song-title { display: block; overflow: hidden; color: var(--sm-text); font-size: 32rpx; font-weight: 700; text-overflow: ellipsis; white-space: nowrap; }
.player-song-artist { display: block; overflow: hidden; margin-top: 9rpx; color: var(--sm-text-2); font-size: 18rpx; text-overflow: ellipsis; white-space: nowrap; }
.player-like { display: grid; place-items: center; width: 55rpx; height: 55rpx; color: var(--sm-text-2); }
.player-like.liked, .control-secondary.liked { color: var(--sm-danger); }

/* 歌词 */
.lyric-view { flex: 1 1 auto; min-height: 0; margin-top: 20rpx; padding: 6rpx 6rpx; overflow: hidden; }
.player-stage.immersive .lyric-view { margin-top: 0; }
.lyric-spacer { width: 100%; }
.lyric-line { padding: 10rpx 0; color: var(--sm-text-3); font-size: 17rpx; line-height: 1.7; text-align: center; transition: color .3s, transform .3s, opacity .3s; }
.lyric-line.active { color: var(--sm-text); font-size: 20rpx; font-weight: 700; text-shadow: 0 0 18rpx var(--sm-accent-glow); transform: scale(1.04); }
.player-stage.immersive .lyric-line { padding: 16rpx 0; font-size: 22rpx; }
.player-stage.immersive .lyric-line.active { color: var(--sm-accent); font-size: 32rpx; text-shadow: 0 0 28rpx var(--sm-accent-glow); transform: scale(1.06); }
.lyric-line.clickable { cursor: pointer; }
.lyric-line.clickable:active { opacity: .6; }
.lyric-empty { margin-top: 26rpx; padding: 30rpx 0; color: var(--sm-text-3); font-size: 16rpx; text-align: center; }
.player-stage.immersive .lyric-empty { margin-top: 0; }

/* 律动频谱条 */
.visualizer { display: flex; flex: none; align-items: flex-end; justify-content: center; gap: 4rpx; height: 56rpx; margin-top: 22rpx; }
.vbar { width: 5rpx; border-radius: 3rpx; background: var(--sm-accent-grad); transform-origin: bottom; animation: eq 1s ease-in-out infinite; }
.visualizer.paused .vbar { animation-play-state: paused; opacity: .35; }

/* 进度条 */
.seek-area { flex: none; margin-top: 30rpx; }
.seek-track { position: relative; height: 24rpx; touch-action: none; }
.seek-track::before { content: ''; position: absolute; top: 9rpx; right: 0; left: 0; height: 5rpx; border-radius: 4rpx; background: var(--sm-border); }
.seek-track > view:first-child { position: absolute; top: 9rpx; left: 0; height: 5rpx; border-radius: 4rpx; background: var(--sm-accent-grad); }
.seek-knob { position: absolute; top: 50%; width: 15rpx; height: 15rpx; border: 3rpx solid var(--sm-bg); border-radius: 50%; background: var(--sm-accent); box-shadow: 0 0 14rpx var(--sm-accent); transform: translate(-50%, -50%); }
.seek-times { display: flex; justify-content: space-between; margin-top: 10rpx; color: var(--sm-text-2); font-family: 'DM Sans', sans-serif; font-size: 15rpx; }

/* 控制区 */
.player-controls { display: flex; flex: none; align-items: center; justify-content: space-between; margin-top: 24rpx; }
.control-secondary, .control-skip { display: grid; place-items: center; width: 52rpx; height: 60rpx; color: var(--sm-text-2); }
.control-secondary { border-radius: 18rpx; transition: color .18s ease, background .18s ease, box-shadow .18s ease; }
.control-secondary.active { color: var(--sm-accent); background: var(--sm-accent-soft); box-shadow: 0 0 0 2rpx var(--sm-accent-soft-border); }
.control-skip { color: var(--sm-text); }
.control-play { display: grid; place-items: center; width: 88rpx; height: 88rpx; border-radius: 50%; background: var(--sm-hero-ink-bg); color: var(--sm-hero-ink); box-shadow: 0 10rpx 30rpx var(--sm-accent-glow); }
.player-extra { display: flex; flex: none; flex-wrap: wrap; justify-content: center; gap: 16rpx; margin-top: 22rpx; }
.extra-button { display: flex; align-items: center; gap: 9rpx; padding: 15rpx 30rpx; border: 1rpx solid var(--sm-border-strong); border-radius: var(--sm-radius-pill); background: var(--sm-surface-2); color: var(--sm-text); font-size: 19rpx; font-weight: 700; box-shadow: 0 6rpx 16rpx var(--sm-shadow-sm); }
.extra-button.active { border-color: var(--sm-accent); color: var(--sm-accent); background: var(--sm-accent-soft); }
.extra-button--primary { border-color: var(--sm-accent-soft-border); color: var(--sm-accent); background: var(--sm-accent-soft); }
.extra-button:active { opacity: .6; }
.audio-notice { margin-top: 12rpx; color: var(--sm-danger); font-size: 16rpx; text-align: center; }

/* 音效预设选择底部抽屉 */
.eq-sheet-mask { position: fixed; z-index: 11; inset: 0; display: flex; align-items: flex-end; background: rgba(0, 0, 0, .45); -webkit-backdrop-filter: blur(4px); backdrop-filter: blur(4px); animation: fade-in .2s ease-out; }
.eq-sheet { display: flex; flex-direction: column; width: 100%; padding: 34rpx 40rpx calc(env(safe-area-inset-bottom) + 36rpx); border-radius: 34rpx 34rpx 0 0; background: var(--sm-surface-3); animation: slide-up .26s ease-out; }
.eq-sheet-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 26rpx; }
.eq-sheet-title { color: var(--sm-text); font-size: 22rpx; font-weight: 700; }
.eq-sheet-close { display: grid; place-items: center; width: 48rpx; height: 48rpx; color: var(--sm-text-2); }
.eq-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 16rpx; }
.eq-chip { display: grid; place-items: center; padding: 22rpx 0; border: 1rpx solid var(--sm-border); border-radius: 16rpx; background: var(--sm-surface); color: var(--sm-text-2); font-size: 18rpx; font-weight: 600; transition: border-color .18s ease, color .18s ease, background .18s ease; }
.eq-chip.active { border-color: var(--sm-accent); color: var(--sm-accent); background: var(--sm-accent-soft); box-shadow: 0 0 0 2rpx var(--sm-accent-soft-border); }
.eq-chip:active { opacity: .7; }

/* 播放队列面板 */
.queue-sheet { max-height: 72vh; }
.queue-scroll { max-height: 60vh; }
.queue-item { display: flex; align-items: center; gap: 20rpx; padding: 24rpx 16rpx; border-radius: 16rpx; }
.queue-item + .queue-item { border-top: 1rpx solid var(--sm-divider); }
.queue-item.current { background: var(--sm-accent-soft); }
.queue-index { flex: none; width: 44rpx; text-align: center; color: var(--sm-text-3); font-size: 19rpx; font-weight: 700; }
.queue-item.current .queue-index { color: var(--sm-accent); }
.queue-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 4rpx; }
.queue-title { overflow: hidden; color: var(--sm-text); font-size: 21rpx; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.queue-item.current .queue-title { color: var(--sm-accent); }
.queue-artist { color: var(--sm-text-2); font-size: 17rpx; }

@keyframes fade-in { from { opacity: 0; } to { opacity: 1; } }
@keyframes slide-up { from { transform: translateY(100%); } to { transform: translateY(0); } }

@keyframes spin { to { transform: rotate(360deg); } }
@keyframes eq { 0%, 100% { transform: scaleY(.25); } 50% { transform: scaleY(1); } }
@keyframes rise-in { from { opacity: 0; transform: translateY(18px); } to { opacity: 1; transform: translateY(0); } }
@media (min-width: 700px) {
  .now-playing { right: max(20rpx, calc((100vw - 680px) / 2)); left: max(20rpx, calc((100vw - 680px) / 2)); }
  .player-overlay { max-width: 720px; margin: 0 auto; }
}
</style>
