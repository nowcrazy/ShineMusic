/**
 * 全局播放器模块：基于 uni-app InnerAudioContext 的单例音频播放服务。
 *
 * 设计要点：
 * - 全局单例：InnerAudioContext 与播放状态都是模块级变量，页面/组件切换（甚至 tab 切换）
 *   时播放不中断、状态不丢失；全应用共用一个播放器实例（见 ensureContext 懒创建）；
 * - 发布订阅：状态保存在 Vue reactive 对象中，通过 usePlayer() 暴露其 readonly 视图，
 *   任意组件引用 state 即完成订阅，进度/播放态变化自动驱动视图更新；
 * - 队列播放：state.queue + queueIndex 构成循环队列，自然播放结束或手动切歌时
 *   按偏移量取模计算下一首（列表首尾循环）；
 * - 本模块不直接调用 /api/v1 接口，播放地址由 catalog.mapTrack 预先解析到 Track.audioUrl。
 */
import { reactive, readonly } from 'vue'
import type { ApiLyric, Track } from '@/types/music'
import { getTrackDefaultLyric } from './catalog'
import { reportPlayEvent } from './feedback'

// 全局播放器单例：页面切换时保留播放状态与 InnerAudioContext。
interface PlayerState {
  /** 当前播放曲目；null 表示启动后从未播放 */
  currentTrack: Track | null
  /** 当前曲目的默认歌词；null 表示无歌词或尚未加载 */
  currentLyric: ApiLyric | null
  /** 播放队列（顺序即播放顺序） */
  queue: Track[]
  /** 当前曲目在队列中的下标，-1 表示空队列 */
  queueIndex: number
  /** 是否正在播放（暂停/出错/停止时为 false） */
  isPlaying: boolean
  /** 当前播放位置，单位秒（由 onTimeUpdate 高频回写） */
  currentTime: number
  /** 曲目总时长，单位秒，未知时为 0 */
  duration: number
  /** 音频是否发生错误（地址失效/解码失败等），供 UI 展示错误态 */
  audioError: boolean
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
})

// 平台音频上下文单例；延迟到首次播放时创建，避免无谓占用系统音频资源
let ctx: ReturnType<typeof uni.createInnerAudioContext> | null = null

/**
 * 懒创建并绑定 InnerAudioContext 的全部事件监听。
 * - onTimeUpdate：播放进度回调，同步 currentTime/duration 到响应式状态；
 * - onEnded：自然播完，自动播放队列下一首（偏移 +1，循环）；
 * - onError：置错误标记并同步播放态，UI 可据此提示。
 * @returns 全局唯一的 InnerAudioContext 实例
 */
function ensureContext() {
  if (ctx) return ctx
  ctx = uni.createInnerAudioContext()
  ctx.autoplay = false
  ctx.onTimeUpdate(() => {
    if (!ctx) return
    state.currentTime = ctx.currentTime || 0
    if (ctx.duration) state.duration = ctx.duration
  })
  ctx.onEnded(() => playByOffset(1))
  ctx.onError(() => {
    state.audioError = true
    state.isPlaying = false
  })
  return ctx
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
 * @param previous 上一首曲目（可能为 null）
 * @param positionSec 切走时的播放位置（秒）
 * @param nextTrackId 即将播放的曲目 ID
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
 * 副作用：重置进度与错误标记、写入 InnerAudioContext.src 并调用 play()。
 * @param track 要播放的曲目（audioUrl 需已可直接访问）
 * @param queue 可选的播放队列
 */
export function playTrack(track: Track, queue?: Track[]) {
  const audio = ensureContext()
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
  audio.src = track.audioUrl
  audio.play()
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
 * 无上下文或无当前曲目时为空操作；副作用为调用 ctx.pause()/play() 并翻转 isPlaying。
 */
export function togglePlayback() {
  if (!ctx || !state.currentTrack) return
  if (state.isPlaying) {
    ctx.pause()
    state.isPlaying = false
  } else {
    ctx.play()
    state.isPlaying = true
  }
}

/** 切换到队列下一首（末尾循环回首部）；空队列时无操作。 */
export function nextTrack() {
  playByOffset(1)
}

/** 切换到队列上一首（首部循环回末尾）；空队列时无操作。 */
export function previousTrack() {
  playByOffset(-1)
}

/**
 * 拖动进度条跳转播放位置。
 * @param seconds 目标位置，单位秒；乐观更新 currentTime，随后以 onTimeUpdate 回调为准
 */
export function seek(seconds: number) {
  ctx?.seek(seconds)
  state.currentTime = seconds
}

/** 停止播放并复位播放态（不清理当前曲目与队列，区别于切换曲目）。 */
export function stopPlayback() {
  ctx?.stop()
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
    seek,
    stopPlayback,
  }
}
