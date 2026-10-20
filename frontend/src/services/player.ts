/**
 * 全局播放器模块：单例音频播放服务。
 *
 * 设计要点：
 * - 全局单例：音频引擎与播放状态都是模块级变量，页面/组件切换（甚至 tab 切换）
 *   时播放不中断、状态不丢失；全应用共用一个播放器实例（见 ensureEngine 懒构建）；
 * - 发布订阅：状态保存在 Vue reactive 对象中，通过 usePlayer() 暴露其 readonly 视图，
 *   任意组件引用 state 即完成订阅，进度/播放态变化自动驱动视图更新；
 * - 队列播放：state.queue + queueIndex 构成队列，自然播完/手动切歌时按偏移取模计算下一首；
 * - 音频引擎抽象：H5 端支持 Web Audio（可挂接 EQ 音效预设），非 H5 端降级为
 *   uni InnerAudioContext（无 EQ，UI 应据 state.eqSupported 隐藏或禁用音效入口）；
 * - 本模块不直接调用 /api/v1 接口，播放地址由 catalog.mapTrack 预先解析到 Track.audioUrl。
 */
import { reactive, readonly } from 'vue'
import type { ApiLyric, Track } from '@/types/music'
import { getTrackDefaultLyric } from './catalog'
import { reportPlayEvent } from './feedback'
import { EQ_PRESETS, findEqPreset, type EqBand } from './eq-presets'

/** 循环模式：order 顺序播完停止 / all 列表循环 / one 单曲循环 */
export type RepeatMode = 'order' | 'all' | 'one'

/** 音效预设本地持久化 key */
const EQ_STORAGE_KEY = 'sm_eq_preset'

// 全局播放器单例：页面切换时保留播放状态与音频引擎。
interface PlayerState {
  /** 当前播放曲目；null 表示启动后从未播放 */
  currentTrack: Track | null
  /** 当前曲目的默认歌词；null 表示无歌词或尚未加载 */
  currentLyric: ApiLyric | null
  /** 播放队列（顺序即播放顺序） */
  queue: readonly Track[]
  /** 当前曲目在队列中的下标，-1 表示空队列 */
  queueIndex: number
  /** 是否正在播放（暂停/出错/停止时为 false） */
  isPlaying: boolean
  /** 当前播放位置，单位秒（由引擎 onTimeUpdate 高频回写） */
  currentTime: number
  /** 曲目总时长，单位秒，未知时为 0 */
  duration: number
  /** 音频是否发生错误（地址失效/解码失败等），供 UI 展示错误态 */
  audioError: boolean
  /** 随机播放开关（开启后自然播完/下一首时随机选曲） */
  shuffle: boolean
  /** 循环模式：all=列表循环（历史默认行为）、one=单曲循环、order=顺序播完停止 */
  repeatMode: RepeatMode
  /** 当前音效预设 id（默认 flat=原声） */
  eqPreset: string
  /** 当前运行环境是否支持 EQ（H5 且有 Web Audio）；false 时 UI 应隐藏音效入口 */
  eqSupported: boolean
}

/** 读取本地持久化的音效预设 id；非法或不可用时回退原声 flat */
function loadInitialEqPreset(): string {
  try {
    const saved = localStorage.getItem(EQ_STORAGE_KEY)
    if (saved && EQ_PRESETS.some((preset) => preset.id === saved)) return saved
  } catch {
    /* localStorage 不可用（如部分 app 环境）时忽略 */
  }
  return 'flat'
}

const state = reactive<PlayerState>({
  currentTrack: null,
  currentLyric: null,
  queue: [],
  queueIndex: -1,
  isPlaying: false,
  currentTime: 0,
  duration: 0,
  audioError: false,
  shuffle: false,
  repeatMode: 'all',
  eqPreset: loadInitialEqPreset(),
  eqSupported: isWebAudioSupported(),
})

// ---------------------------------------------------------------------------
// 音频引擎抽象：屏蔽 Web Audio（H5 + EQ）与 InnerAudioContext（其余端）之间的差异。
// ---------------------------------------------------------------------------

/** 引擎回调：把底层事件转发为播放器状态更新 */
interface AudioEngineCallbacks {
  onTimeUpdate(time: number, duration: number): void
  onEnded(): void
  onError(): void
}

/** 统一的音频引擎接口 */
interface AudioEngine {
  /** 更换媒体源并准备播放 */
  setSrc(url: string): void
  play(): void
  pause(): void
  seek(seconds: number): void
  stop(): void
  /** 应用音效 EQ 滤波器；无 EQ 能力时为空操作 */
  setEqBands(bands: EqBand[]): void
}

/** 当前环境是否支持 Web Audio（H5 且有 AudioContext） */
function isWebAudioSupported(): boolean {
  if (typeof window === 'undefined') return false
  const w = window as unknown as { AudioContext?: unknown; webkitAudioContext?: unknown }
  return Boolean(w.AudioContext || w.webkitAudioContext)
}

/** 非 H5 / 无 Web Audio：包装 uni InnerAudioContext（原播放路径，无 EQ） */
function createInnerAudioEngine(cb: AudioEngineCallbacks): AudioEngine {
  const audio = uni.createInnerAudioContext()
  audio.autoplay = false
  audio.onTimeUpdate(() => cb.onTimeUpdate(audio.currentTime || 0, audio.duration || 0))
  audio.onEnded(() => cb.onEnded())
  audio.onError(() => cb.onError())
  return {
    setSrc: (url) => {
      audio.src = url
    },
    play: () => audio.play(),
    pause: () => audio.pause(),
    seek: (seconds) => audio.seek(seconds),
    stop: () => audio.stop(),
    setEqBands: () => {
      /* 该引擎无 EQ 能力 */
    },
  }
}

/**
 * H5 + Web Audio：用原生 <audio> 作为媒体源，经 MediaElementSourceNode 接入
 * BiquadFilter 链（EQ 音效）后输出到 destination。
 * 注意：一个 media 元素只能 createMediaElementSource 一次，故 source 在元素首次创建时建立；
 * 换曲只改 audioEl.src，切预设只重建 filters 段的连接。
 */
function createWebAudioEngine(cb: AudioEngineCallbacks): AudioEngine {
  let audioCtx: AudioContext | null = null
  let audioEl: HTMLAudioElement | null = null
  let source: MediaElementAudioSourceNode | null = null
  let filters: BiquadFilterNode[] = []
  let bands: EqBand[] = []

  const getCtx = (): AudioContext => {
    if (!audioCtx) {
      const w = window as unknown as { AudioContext?: new () => AudioContext; webkitAudioContext?: new () => AudioContext }
      const Ctor = w.AudioContext ?? w.webkitAudioContext
      audioCtx = new Ctor!()
    }
    return audioCtx
  }

  /** 依据当前 bands 重建 source 之后的滤波器连接（无 bands 时直连 destination） */
  const rebuild = () => {
    if (!audioCtx || !source) return
    try {
      source.disconnect()
    } catch {
      /* ignore */
    }
    filters.forEach((f) => {
      try {
        f.disconnect()
      } catch {
        /* ignore */
      }
    })
    filters = []
    let node: AudioNode = source
    for (const band of bands) {
      const f = audioCtx.createBiquadFilter()
      f.type = band.type
      f.frequency.value = band.frequency
      f.gain.value = band.gain
      f.Q.value = band.q
      node.connect(f)
      filters.push(f)
      node = f
    }
    node.connect(audioCtx.destination)
  }

  return {
    setSrc: (url) => {
      const ac = getCtx()
      if (!audioEl) {
        audioEl = new Audio()
        audioEl.crossOrigin = 'anonymous'
        audioEl.preload = 'auto'
        audioEl.addEventListener('timeupdate', () => {
          cb.onTimeUpdate(audioEl!.currentTime || 0, audioEl!.duration || 0)
        })
        audioEl.addEventListener('ended', () => cb.onEnded())
        audioEl.addEventListener('error', () => cb.onError())
        source = ac.createMediaElementSource(audioEl)
      }
      if (audioEl.src !== url) audioEl.src = url
      rebuild()
    },
    play: () => {
      if (!audioEl) return
      if (audioCtx && audioCtx.state === 'suspended') void audioCtx.resume()
      void audioEl.play().catch(() => {
        /* 忽略自动播放策略拦截 */
      })
    },
    pause: () => audioEl?.pause(),
    seek: (seconds) => {
      if (audioEl) audioEl.currentTime = seconds
    },
    stop: () => {
      if (audioEl) {
        audioEl.pause()
        try {
          audioEl.currentTime = 0
        } catch {
          /* ignore */
        }
      }
    },
    setEqBands: (next) => {
      bands = next
      rebuild()
    },
  }
}

/** 音频引擎单例；延迟到首次播放时构建，避免无谓占用系统音频资源 */
let engine: AudioEngine | null = null

/** 懒构建音频引擎，并把当前音效预设对应的 EQ 波段预置进去 */
function ensureEngine(): AudioEngine {
  if (engine) return engine
  engine = (isWebAudioSupported() ? createWebAudioEngine : createInnerAudioEngine)({
    onTimeUpdate: (time, duration) => {
      state.currentTime = time
      if (duration) state.duration = duration
    },
    onEnded: handleEnded,
    onError: () => {
      state.audioError = true
      state.isPlaying = false
    },
  })
  engine.setEqBands(findEqPreset(state.eqPreset).bands)
  return engine
}

/**
 * 随机返回一个「不等于当前下标」的合法队列下标（队列长度 >1 时）。
 * 用于随机播放的自然切歌与手动下一首。
 */
function randomOtherIndex(current: number, length: number): number {
  if (length <= 1) return 0
  let next = Math.floor(Math.random() * length)
  if (next === current) next = (next + 1) % length
  return next
}

/**
 * 自然播完的回调：根据循环模式 + 随机开关决定后续动作。
 * - 单曲循环：seek 回 0 重播当前曲，不动队列与歌词；
 * - 随机播放：随机切到另一首；
 * - 顺序播放到最后一首：停止（isPlaying=false，指针停在末尾）；
 * - 其余（列表循环 / 顺序未到末尾）：顺序下一首。
 */
function handleEnded() {
  if (!state.queue.length || !state.currentTrack) return
  if (state.repeatMode === 'one') {
    engine?.seek(0)
    engine?.play()
    state.currentTime = 0
    return
  }
  if (state.shuffle && state.queue.length > 1) {
    playTrack(state.queue[randomOtherIndex(state.queueIndex, state.queue.length)], state.queue)
    return
  }
  const isLast = state.queueIndex === state.queue.length - 1
  if (state.repeatMode === 'order' && isLast) {
    state.isPlaying = false
    return
  }
  playByOffset(1)
}

/**
 * 按相对偏移量切换队列中的曲目（对队列长度取模，实现首尾循环）。
 * @param offset 1 = 下一首，-1 = 上一首
 */
function playByOffset(offset: number) {
  if (!state.queue.length) return
  const nextIndex =
    (state.queueIndex + offset + state.queue.length) % state.queue.length
  playTrack(state.queue[nextIndex], state.queue)
}

/**
 * 切歌时上报上一首的收尾信号（行为数据）。
 * 判定：播放进度达到时长的 90% 以上视为“完整收听(complete)”，否则视为“跳过(skip)”。
 * 进度为 0（尚未真正播放）时不上报，避免误判。
 */
function reportTransition(previous: Track | null, positionSec: number, nextTrackId: string) {
  if (!previous || previous.id === nextTrackId) return
  const duration = previous.duration || 0
  const position = positionSec || 0
  if (position <= 0) return
  if (duration > 0 && position >= duration * 0.9) {
    reportPlayEvent(previous.id, 'complete', position * 1000)
  } else {
    reportPlayEvent(previous.id, 'skip', position * 1000)
  }
}

/**
 * 播放指定曲目，并可选地（重）设播放队列。
 *
 * 队列处理规则：
 * - 传入非空 queue：替换当前队列，并定位到该曲目；
 * - 未传 queue 且曲目已在当前队列中：仅移动下标（保持队列）；
 * - 未传 queue 且队列空：以单曲初始化队列。
 *
 * 副作用：重置进度与错误标记、写入媒体源并调用 play()。
 * @param track 要播放的曲目（audioUrl 需已可直接访问）
 * @param queue 可选的播放队列
 */
export function playTrack(track: Track, queue?: readonly Track[]) {
  const eng = ensureEngine()
  // 上报上一首的结束/跳过信号（此时 currentTime 仍为上一首的进度）
  reportTransition(state.currentTrack, state.currentTime, track.id)
  if (queue && queue.length) {
    state.queue = queue
    state.queueIndex = queue.findIndex((item) => item.id === track.id)
  } else if (state.queue.length) {
    const index = state.queue.findIndex((item) => item.id === track.id)
    if (index >= 0) state.queueIndex = index
  } else {
    state.queue = [track]
    state.queueIndex = 0
  }
  state.currentTrack = track
  state.currentLyric = null
  state.currentTime = 0
  state.duration = track.duration || 0
  state.audioError = false
  eng.setSrc(track.audioUrl)
  eng.play()
  state.isPlaying = true
  // 上报一次“开始播放”（用于统计播放次数与歌手偏好）
  reportPlayEvent(track.id, 'play', 0)
  // 异步拉取该曲目的默认歌词（不阻塞播放）
  void getTrackDefaultLyric(track.id).then((lyric) => {
    // 仅当当前曲目仍是这首时才写入，避免竞态覆盖
    if (state.currentTrack?.id === track.id) state.currentLyric = lyric
  })
}

/**
 * 从指定下标开始播放整个队列（歌单/专辑列表的“播放全部”入口）。
 * @param tracks 曲目队列
 * @param startIndex 起始下标，默认 0；越界（空队列）时静默忽略
 */
export function playQueue(tracks: Track[], startIndex = 0) {
  const track = tracks[startIndex]
  if (track) playTrack(track, tracks)
}

/**
 * 切换播放/暂停（播放器条的播放按钮）。
 * 无当前曲目时为空操作；副作用为调用引擎 pause()/play() 并翻转 isPlaying。
 */
export function togglePlayback() {
  if (!engine || !state.currentTrack) return
  if (state.isPlaying) {
    engine.pause()
    state.isPlaying = false
  } else {
    engine.play()
    state.isPlaying = true
  }
}

/** 切换到队列下一首（随机播放时随机选曲；其余按顺序，末尾循环回首部）；空队列时无操作。 */
export function nextTrack() {
  if (!state.queue.length) return
  if (state.shuffle && state.queue.length > 1) {
    playTrack(state.queue[randomOtherIndex(state.queueIndex, state.queue.length)], state.queue)
    return
  }
  playByOffset(1)
}

/** 切换到队列上一首（首部循环回末尾）；空队列时无操作。 */
export function previousTrack() {
  playByOffset(-1)
}

/** 切换随机播放开关（开/关）。 */
export function toggleShuffle() {
  state.shuffle = !state.shuffle
}

/** 循环切换播放模式：单曲循环 → 列表循环 → 顺序播放。 */
export function cycleRepeatMode() {
  const order: RepeatMode[] = ['one', 'all', 'order']
  const next = (order.indexOf(state.repeatMode) + 1) % order.length
  state.repeatMode = order[next]
}

/**
 * 切换音效预设：立即应用到音频引擎的 EQ 滤波器，并持久化到本地。
 * 非 H5 / 无 Web Audio 环境（state.eqSupported 为 false）下为空操作。
 * @param id 预设 id（见 eq-presets 的 EQ_PRESETS）
 */
export function setEqPreset(id: string) {
  if (!state.eqSupported) return
  const preset = findEqPreset(id)
  state.eqPreset = preset.id
  engine?.setEqBands(preset.bands)
  try {
    localStorage.setItem(EQ_STORAGE_KEY, preset.id)
  } catch {
    /* localStorage 不可用时忽略 */
  }
}

/**
 * 拖动进度条跳转播放位置。
 * @param seconds 目标位置，单位秒；乐观更新 currentTime，随后以 onTimeUpdate 回调为准
 */
export function seek(seconds: number) {
  engine?.seek(seconds)
  state.currentTime = seconds
}

/** 停止播放并复位播放态（不清理当前曲目与队列，区别于切换曲目）。 */
export function stopPlayback() {
  engine?.stop()
  state.isPlaying = false
}

/**
 * 播放器组合式入口：供任意组件获取只读播放状态与控制方法。
 * @returns state 为 readonly 代理（组件无法直接篡改状态，只能通过方法变更），
 *          其余为播放控制函数
 */
export function usePlayer() {
  return {
    state: readonly(state),
    playTrack,
    playQueue,
    togglePlayback,
    nextTrack,
    previousTrack,
    toggleShuffle,
    cycleRepeatMode,
    setEqPreset,
    seek,
    stopPlayback,
  }
}