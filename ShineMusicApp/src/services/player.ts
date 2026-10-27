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
import { checkTrackExists, getTrackDefaultLyric } from './catalog'
import { reportPlayEvent } from './feedback'
import { EQ_PRESETS, findEqPreset, type EqBand } from './eq-presets'
import {
  initMusicNotification,
  syncMusicNotificationProgress,
  updateMusicNotification,
  updateMusicNotificationState,
} from './music-notification'

/** 播放模式：order 顺序播完停止 / shuffle 随机播放 / one 单曲循环 */
export type PlayMode = 'order' | 'shuffle' | 'one'

/** 音效预设本地持久化 key */
const EQ_STORAGE_KEY = 'sm_eq_preset'
/** 播放现场本地持久化 key：冷启动后恢复歌单、曲目与进度 */
const STATE_STORAGE_KEY = 'sm-player-state'
/** 进度回写的最小间隔（毫秒），避免 onTimeUpdate 高频写存储 */
const PERSIST_INTERVAL = 2000

/** 持久化到本地的播放现场（只存可还原播放态所需的最小字段） */
interface PersistedPlayerState {
  track: Track
  queue: Track[]
  queueIndex: number
  currentTime: number
  playMode: PlayMode
}

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
  /** 播放模式：order=顺序播完停止、shuffle=随机播放、one=单曲循环 */
  playMode: PlayMode
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
  playMode: 'order',
  eqPreset: loadInitialEqPreset(),
  eqSupported: isWebAudioSupported(),
})

/**
 * 把当前播放现场写入本地存储（冷启动恢复用）。
 * @param force true=立即写入（切歌/暂停/跳转等关键节点），false=按 PERSIST_INTERVAL 节流
 */
function persistState(force = false) {
  if (!state.currentTrack) return
  const now = Date.now()
  if (!force && now - lastPersistedAt < PERSIST_INTERVAL) return
  lastPersistedAt = now
  try {
    const payload: PersistedPlayerState = {
      track: state.currentTrack,
      queue: [...state.queue],
      queueIndex: state.queueIndex,
      currentTime: state.currentTime,
      playMode: state.playMode,
    }
    uni.setStorageSync(STATE_STORAGE_KEY, JSON.stringify(payload))
  } catch {
    /* 存储不可用时忽略 */
  }
}

let lastPersistedAt = 0

/**
 * 冷启动时恢复上次的播放现场：曲目、队列、进度与播放模式。
 * 恢复后为暂停态（不自动发声），用户点播放即从上次进度继续。
 * App 端额外检查 BackgroundAudioManager 是否仍在播放（进程被回收但音频服务未死），
 * 是则直接接管播放态。
 */
function restoreState() {
  let saved: Partial<PersistedPlayerState> | null = null
  try {
    const raw = uni.getStorageSync(STATE_STORAGE_KEY)
    saved = raw ? (JSON.parse(raw) as Partial<PersistedPlayerState>) : null
  } catch {
    saved = null
  }
  if (!saved?.track?.id || !saved.track.audioUrl) return
  const track = saved.track
  const queue = Array.isArray(saved.queue) && saved.queue.length ? saved.queue : [track]
  const index = queue.findIndex((item) => item.id === track.id)
  const duration = track.duration || 0
  const position = typeof saved.currentTime === 'number' ? saved.currentTime : 0
  state.currentTrack = track
  state.queue = queue
  state.queueIndex = index >= 0 ? index : Math.max(0, Math.min(saved.queueIndex ?? 0, queue.length - 1))
  state.currentTime = duration > 0 ? Math.min(position, Math.max(0, duration - 1)) : Math.max(0, position)
  state.duration = duration
  state.playMode = saved.playMode === 'shuffle' || saved.playMode === 'one' ? saved.playMode : 'order'
  state.isPlaying = false
  // 歌词不走持久化，按需重新拉取
  void getTrackDefaultLyric(track.id).then((lyric) => {
    if (state.currentTrack?.id === track.id) state.currentLyric = lyric
  })
  // App 端：若原生音频服务仍在播放，同步它的真实状态（可能比本地持久化更新）
  // 先把持久化 queue 暴露给下面的匹配逻辑（restoreState 此时还没 state.queue）
  _restoreQueueRef = queue
  syncFromBackgroundAudio()
  _restoreQueueRef = null
  // 曲库重建/曲目被删后，持久化现场可能引用已不存在的曲目：异步校验，确认 404 则清除，
  // 避免迷你播放条复活死曲目、一点播放就陷入 404 重试循环
  void checkTrackExists(track.id).then((exists) => {
    if (!exists && state.currentTrack?.id === track.id) purgeDeadTrack(track.id, false)
  })
}

/** restoreState 内部暂存的 queue（供 syncFromBackgroundAudio 匹配曲目用，生命周期内为 null） */
let _restoreQueueRef: Track[] | null = null

/**
 * App 端专用：从 BackgroundAudioManager 同步当前播放状态到 state。
 * - Activity 重建 / 从后台切回 / BGM 被锁屏切歌 等场景都需要它。
 * - 如果 BGM.src 能在 state.queue 里匹配到 track，会覆盖 state.currentTrack；
 *   否则至少用 BGM 的进度/时长/播放态纠正 state。
 * - 纯安全函数：H5 端或 BGM 不可用时静默跳过。
 */
export function syncFromBackgroundAudio() {
  if (typeof plus === 'undefined') return
  try {
    const bgm = uni.getBackgroundAudioManager()
    if (!bgm.src) return

    // 匹配 BGM.src 对应的曲目：优先用 state.queue，restoreState 期间用 _restoreQueueRef
    const queueForMatch: readonly Track[] = state.queue.length
      ? state.queue
      : (_restoreQueueRef ?? [])
    const bgmSrc = String(bgm.src)
    // 从 BGM.src 提取路径部分：去掉 host 与 query，得到 /api/v1/tracks/{id}/stream
    // 避免 bgmSrc 是绝对 URL（含 host）或带 query 时 endsWith 匹配失败
    const urlPath = (() => {
      try {
        const u = new URL(bgmSrc)
        return u.pathname // 如 /api/v1/tracks/xxx/stream
      } catch {
        // 非标准 URL：直接取第一个 '?' 之前的部分
        const q = bgmSrc.indexOf('?')
        return q >= 0 ? bgmSrc.slice(0, q) : bgmSrc
      }
    })()
    const match = queueForMatch.find((t) => {
      const au = t.audioUrl
      // audioUrl 可能是相对路径（/api/v1/...）或完整 URL，统一取 path 部分比对
      const auPath = au.startsWith('http')
        ? (() => { try { return new URL(au).pathname } catch { return au } })()
        : au
      return urlPath === auPath || urlPath.endsWith(auPath) || auPath.endsWith(urlPath)
    })
    if (match && match.id !== state.currentTrack?.id) {
      state.currentTrack = match
      state.queueIndex = queueForMatch.findIndex((t) => t.id === match.id)
      void getTrackDefaultLyric(match.id).then((lyric) => {
        if (state.currentTrack?.id === match.id) state.currentLyric = lyric
      })
    }
    // 以 BGM 为权威源纠正播放态/进度/时长
    state.isPlaying = !bgm.paused
    state.currentTime = bgm.currentTime || state.currentTime
    state.duration = bgm.duration || state.duration
    ensureEngine()
    persistState(true)
  } catch {
    /* bgm 不可用时忽略 */
  }
}

restoreState()

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
  /** 更换媒体源并准备播放；startAt 为起始播放位置（秒），用于冷启动续播 */
  setSrc(url: string, startAt?: number): void
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
  // iOS 静音键不影响播放（音乐类应用预期行为）
  audio.obeyMuteSwitch = false
  // #ifdef APP-PLUS
  // App 端音频会话类别：playback 允许切后台/锁屏后继续播放（App 3.3.7+）
  ;(audio as unknown as { sessionCategory?: string }).sessionCategory = 'playback'
  // #endif
  audio.onTimeUpdate(() => cb.onTimeUpdate(audio.currentTime || 0, audio.duration || 0))
  audio.onEnded(() => cb.onEnded())
  audio.onError(() => cb.onError())
  return {
    setSrc: (url, startAt) => {
      // startTime 必须在设置 src 前赋值，App 端据此从指定位置起播
      audio.startTime = startAt && startAt > 0 ? startAt : 0
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
 * App 端后台音频引擎：包装 uni.getBackgroundAudioManager()。
 * 相比 InnerAudioContext，它会在系统级挂起原生音频服务，
 * 切后台/锁屏后播放不中断，进程也不易被回收；
 * 同时自动在锁屏/通知栏展示 title/singer/coverImgUrl。
 * 注意：设置 src 会自动开始播放，这是官方行为。
 */
function createBackgroundAudioEngine(cb: AudioEngineCallbacks): AudioEngine {
  const bgm = uni.getBackgroundAudioManager()
  // setSrc 后 BGM 会自动起播，此时再调 play() 会打乱原生播放器（实测导致 ~20 秒延迟/停在起始位置）
  let autoStarting = false
  let autoStartTimer: ReturnType<typeof setTimeout> | null = null
  /** 待续播位置：startTime 在 Android BGM 上会卡住播放，改为起播后 seek 一次 */
  let pendingStart = 0
  const clearAutoStarting = () => {
    autoStarting = false
    if (autoStartTimer) {
      clearTimeout(autoStartTimer)
      autoStartTimer = null
    }
  }
  // 初始化 Android 锁屏/通知栏控制（插件缺失时内部自动降级为空操作）
  initMusicNotification({
    onToggle: () => togglePlayback(),
    onPrevious: () => previousTrack(),
    onNext: () => nextTrack(),
    onClose: () => stopPlayback(),
    onSeek: (seconds) => seek(seconds),
  })
  bgm.onTimeUpdate(() => {
    const time = bgm.currentTime || 0
    const duration = bgm.duration || 0
    cb.onTimeUpdate(time, duration)
    // 同步进度到通知栏（内部按 2 秒节流）
    syncMusicNotificationProgress(time)
  })
  bgm.onEnded(() => cb.onEnded())
  bgm.onError(() => cb.onError())
  // 用户在锁屏/通知栏操作时同步状态
  bgm.onPlay(() => {
    clearAutoStarting()
    if (pendingStart > 0) {
      // 起播后再跳到续播位置：Android 上直接设 startTime 会卡在起始位置不再前进
      const target = pendingStart
      pendingStart = 0
      bgm.seek(target)
    }
    state.isPlaying = true
    updateMusicNotificationState(true)
    persistState(true)
  })
  bgm.onPause(() => {
    state.isPlaying = false
    updateMusicNotificationState(false)
    persistState(true)
  })
  bgm.onStop(() => {
    state.isPlaying = false
    updateMusicNotificationState(false)
    persistState(true)
  })
  // iOS 锁屏上一曲/下一曲（Android 不支持，注册无害）
  bgm.onPrev(() => previousTrack())
  bgm.onNext(() => nextTrack())
  return {
    setSrc: (url, startAt) => {
      // 设置 src 会自动开始播放，先写元数据再写 src
      const track = state.currentTrack
      if (track) {
        // 显式 String() 转换，避免 Vue 响应式包装或对象字符串导致原生层乱码
        bgm.title = String(track.title || '')
        bgm.singer = String(track.artist || '')
        // 专辑名：原生锁屏/通知栏 MediaSession 需要 title+singer+epname 至少两个才显示完整
        bgm.epname = String(track.album || '')
        // 封面为空时不设置，避免原生层解析空字符串导致降级显示 URL
        if (track.cover) {
          bgm.coverImgUrl = String(track.cover)
        }
        // 清空 webUrl，防止原生层把默认页面链接显示在锁屏上
        bgm.webUrl = ''
        // 推送歌曲信息给插件接管 MediaSession（真正决定锁屏显示内容的通道）
        updateMusicNotification(track, true)
      }
      const start = startAt && startAt > 0 ? startAt : 0
      // 标记自动起播窗口：这期间调用方紧随其后的 play() 必须忽略
      autoStarting = true
      if (autoStartTimer) clearTimeout(autoStartTimer)
      autoStartTimer = setTimeout(clearAutoStarting, 4000)
      // 清掉可能残留的 startTime，续播改由 onPlay 后的 seek 完成
      bgm.startTime = 0
      pendingStart = start
      bgm.src = url
    },
    play: () => {
      // setSrc 后 BGM 会自动起播；此时再调 play() 会打断原生播放器加载，
      // 实测导致起播延迟约 20 秒、续播卡在起始位置不动
      if (autoStarting) return
      bgm.play()
    },
    pause: () => {
      clearAutoStarting()
      bgm.pause()
    },
    seek: (seconds) => bgm.seek(seconds),
    stop: () => {
      clearAutoStarting()
      bgm.stop()
    },
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
    setSrc: (url, startAt) => {
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
      if (audioEl.src !== url) {
        audioEl.src = url
        // 续播：媒体元数据就绪后才能设置 currentTime
        if (startAt && startAt > 0) {
          audioEl.addEventListener(
            'loadedmetadata',
            () => {
              try {
                audioEl!.currentTime = startAt
              } catch {
                /* 元数据未就绪时忽略 */
              }
            },
            { once: true },
          )
        }
      }
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

/**
 * 播放错误自动重连（弱网容错）。
 * 公网链路抖动时底层播放器会直接抛 error 并停下；这里短暂延迟后从断点续播，
 * 避免用户感知为「突然暂停且不再恢复」。每首曲目最多两次，续播稳定 5 秒后额度重置。
 */
const MAX_AUTO_RETRIES = 2
let autoRetries = 0
let autoRetryTimer: ReturnType<typeof setTimeout> | null = null
/** 最近一次自动续播的起点（秒），用于判断是否已经稳定播放 */
let autoRetryResumeAt = 0

/** 清空自动重连状态（切歌时调用） */
function resetAutoRetry() {
  autoRetries = 0
  autoRetryResumeAt = 0
  if (autoRetryTimer) {
    clearTimeout(autoRetryTimer)
    autoRetryTimer = null
  }
}

/** 安排一次自动续播；返回 true 表示错误已被接管（不再进入错误态） */
function scheduleAutoRetry(): boolean {
  const track = state.currentTrack
  if (!track || autoRetries >= MAX_AUTO_RETRIES) return false
  autoRetries += 1
  autoRetryResumeAt = state.currentTime
  const resumeAt = autoRetryResumeAt
  const trackId = track.id
  if (autoRetryTimer) clearTimeout(autoRetryTimer)
  autoRetryTimer = setTimeout(() => {
    autoRetryTimer = null
    if (state.currentTrack?.id !== trackId) return
    // 从断点重新装载媒体源：App 端 BGM 设置 src 会自动起播，InnerAudio/H5 靠随后的 play()
    ensureEngine().setSrc(track.audioUrl, resumeAt)
    ensureEngine().play()
    state.isPlaying = true
    state.audioError = false
    persistState(true)
  }, 1500)
  return true
}

/** 正在进行存在性校验的曲目 id；防止引擎重复 onError 引发并发校验 */
let goneCheckInFlight: string | null = null

/**
 * 把已确认不存在（404）的曲目从队列与播放现场中永久移除。
 * 用于曲库重建/曲目被删后清理失效的本地持久化现场——否则死曲目每次冷启动都会复活，
 * 点播放就陷入 404 重试循环。
 * @param trackId 已确认 404 的曲目
 * @param autoplayNext 移除后是否自动播放下一首（播放中出错传 true；冷启动校验传 false）
 */
function purgeDeadTrack(trackId: string, autoplayNext: boolean) {
  const deadIndex = state.queueIndex
  const queue = state.queue.filter((item) => item.id !== trackId)
  const wasCurrent = state.currentTrack?.id === trackId
  state.queue = queue
  if (!wasCurrent) {
    // 非当前曲目：仅收缩队列并校正下标
    state.queueIndex = queue.findIndex((item) => item.id === state.currentTrack?.id)
    if (state.currentTrack) persistState(true)
    return
  }
  engine?.stop()
  resetAutoRetry()
  state.currentTrack = null
  state.currentLyric = null
  state.currentTime = 0
  state.duration = 0
  state.audioError = false
  state.isPlaying = false
  state.queueIndex = -1
  if (!queue.length) {
    // 队列已空：连同持久化现场一起清掉（persistState 需要 currentTrack，只能直接删 key）
    try {
      uni.removeStorageSync(STATE_STORAGE_KEY)
    } catch {
      /* 存储不可用时忽略 */
    }
    return
  }
  if (autoplayNext) {
    // 队列收缩后，原位置的下一首自然顶替到 deadIndex
    const nextIndex = Math.min(Math.max(deadIndex, 0), queue.length - 1)
    playTrack(queue[nextIndex], queue)
  } else {
    // 无当前曲目时持久化现场不可还原（restoreState 要求 track 存在），直接清掉
    try {
      uni.removeStorageSync(STATE_STORAGE_KEY)
    } catch {
      /* 存储不可用时忽略 */
    }
  }
}

/**
 * 引擎错误处理：先确认曲目是否仍存在，再决定重试还是移除。
 * - 曲目 404（曲库重建/被下架/被移除权限）：永久移除并按需自动切下一首；
 * - 其余（弱网等瞬断）：维持原自动续播重试，重试耗尽才进入错误态。
 */
function handleAudioError() {
  const track = state.currentTrack
  if (!track) {
    state.audioError = true
    state.isPlaying = false
    persistState(true)
    return
  }
  if (goneCheckInFlight === track.id) return
  goneCheckInFlight = track.id
  const wasPlaying = state.isPlaying
  void checkTrackExists(track.id)
    .then((exists) => {
      // 校验期间用户可能已切歌，仅当仍是同一曲目时才处置
      if (state.currentTrack?.id !== track.id) return
      if (!exists) {
        purgeDeadTrack(track.id, wasPlaying)
        return
      }
      // 播放中（用户想听）才自动续播；已暂停或到重试上限则进入错误态
      if (wasPlaying && scheduleAutoRetry()) return
      state.audioError = true
      state.isPlaying = false
      persistState(true)
    })
    .finally(() => {
      if (goneCheckInFlight === track.id) goneCheckInFlight = null
    })
}

/** 懒构建音频引擎，并把当前音效预设对应的 EQ 波段预置进去 */
function ensureEngine(): AudioEngine {
  if (engine) return engine
  // App 端优先用 BackgroundAudioManager（系统级保活）；H5 用 WebAudio；其余回退 InnerAudioContext
  const create = typeof plus !== 'undefined'
    ? createBackgroundAudioEngine
    : isWebAudioSupported()
      ? createWebAudioEngine
      : createInnerAudioEngine
  engine = create({
    onTimeUpdate: (time, duration) => {
      state.currentTime = time
      if (duration) state.duration = duration
      // 续播稳定播放 5 秒后重置重试额度：长曲目多次抖动仍可自动恢复
      if (autoRetries > 0 && time > autoRetryResumeAt + 5) autoRetries = 0
      // 播放进度按节流回写本地，App 被杀后仍能恢复到上次位置
      persistState()
    },
    onEnded: handleEnded,
    onError: handleAudioError,
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
 * 自然播完的回调：根据播放模式决定后续动作。
 * - 单曲循环（one）：seek 回 0 重播当前曲；
 * - 随机播放（shuffle）：随机切到另一首；
 * - 顺序播放（order）：到最后一首则停止，否则顺序下一首。
 */
function handleEnded() {
  if (!state.queue.length || !state.currentTrack) return
  if (state.playMode === 'one') {
    engine?.seek(0)
    engine?.play()
    state.currentTime = 0
    return
  }
  if (state.playMode === 'shuffle' && state.queue.length > 1) {
    playTrack(state.queue[randomOtherIndex(state.queueIndex, state.queue.length)], state.queue)
    return
  }
  // order：顺序播放到最后一首则停止，否则顺序下一首
  if (state.queueIndex === state.queue.length - 1) {
    state.isPlaying = false
    persistState(true)
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
  resetAutoRetry()
  eng.setSrc(track.audioUrl)
  eng.play()
  state.isPlaying = true
  persistState(true)
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
 * 冷启动恢复的现场此时引擎尚未构建：按已恢复的进度建引擎并接着播。
 */
export function togglePlayback() {
  const track = state.currentTrack
  if (!track) return
  if (state.isPlaying) {
    // 用户主动暂停：取消可能待触发的自动续播，避免暂停后自己又响起来
    resetAutoRetry()
    engine?.pause()
    state.isPlaying = false
    persistState(true)
    return
  }
  if (!engine) {
    // 冷启动恢复的现场：建引擎并从上次进度接着播
    ensureEngine().setSrc(track.audioUrl, state.currentTime)
  }
  engine?.play()
  state.isPlaying = true
  persistState(true)
}

/** 切换到队列下一首（随机播放时随机选曲；其余按顺序，末尾循环回首部）；空队列时无操作。 */
export function nextTrack() {
  if (!state.queue.length) return
  if (state.playMode === 'shuffle' && state.queue.length > 1) {
    playTrack(state.queue[randomOtherIndex(state.queueIndex, state.queue.length)], state.queue)
    return
  }
  playByOffset(1)
}

/** 切换到队列上一首（首部循环回末尾）；空队列时无操作。 */
export function previousTrack() {
  playByOffset(-1)
}

/** 从当前播放队列的指定下标开始播放（播放列表面板点击条目切歌）。越界或空队列时静默忽略。 */
export function playFromQueue(index: number) {
  const track = state.queue[index]
  if (track) playTrack(track, state.queue)
}

/** 循环切换播放模式：顺序播放 → 随机播放 → 单曲循环。 */
export function cyclePlayMode() {
  const order: PlayMode[] = ['order', 'shuffle', 'one']
  const next = (order.indexOf(state.playMode) + 1) % order.length
  state.playMode = order[next]
  persistState(true)
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
  const wasPlaying = state.isPlaying
  engine?.seek(seconds)
  state.currentTime = seconds
  persistState(true)
  // App 端 BackgroundAudioManager 在部分 Android 机型 seek 后会停在原地不再前进：
  // 播放中跳转后补一次 play() 让原生播放器继续；暂停态不补，避免自动开始播放。
  if (wasPlaying) engine?.play()
}

/** 停止播放并复位播放态（不清理当前曲目与队列，区别于切换曲目）。 */
export function stopPlayback() {
  engine?.stop()
  state.isPlaying = false
  persistState(true)
}

/** 立即把播放现场写入本地（App 切后台/退出前调用，避免丢失最后进度）。 */
export function flushPlayerState() {
  persistState(true)
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
    playFromQueue,
    cyclePlayMode,
    setEqPreset,
    seek,
    stopPlayback,
  }
}