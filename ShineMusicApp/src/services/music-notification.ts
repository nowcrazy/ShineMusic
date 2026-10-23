/**
 * Android 锁屏/通知栏音乐控制适配层（XZH-musicNotification 原生插件）。
 *
 * 背景：uni.getBackgroundAudioManager() 只负责后台播放保活，不向 Android
 * MediaSession 推送歌曲元数据，锁屏会降级显示音频流 URL（含 track UUID）。
 * 该插件接管 MediaSession，负责锁屏/通知栏的歌名、歌手、封面与播放控制。
 *
 * 设计要点：
 * - 插件缺失时全量降级为空操作：未打包含插件的 APK（普通基座/H5）不报错、不影响播放；
 * - 插件需先 createNotification 成功后才能 update，因此用 ready + pending 缓存首发元数据；
 * - 锁屏按钮事件通过 plus.globalEvent 回调到 player，由调用方注入，避免与 player 循环依赖。
 */
import type { Track } from '@/types/music'

/** 锁屏/通知栏操作回调，由 player 模块注入 */
export interface MusicNotificationHandlers {
  /** 播放/暂停按钮 */
  onToggle(): void
  /** 上一首 */
  onPrevious(): void
  /** 下一首 */
  onNext(): void
  /** 关闭通知栏 */
  onClose(): void
  /** 通知栏进度条拖动，单位秒 */
  onSeek(seconds: number): void
}

/** 原生插件实例；null 表示当前环境不可用 */
let plugin: any = null
/** createNotification 是否已回调成功 */
let ready = false
/** globalEvent 是否已绑定，避免重复注册 */
let eventsBound = false
/** 业务回调 */
let handlers: MusicNotificationHandlers | null = null
/** 通知栏就绪前到达的首发元数据 */
let pendingTrack: Track | null = null
let pendingPlaying = false
/** setPosition 的节流时间戳（毫秒） */
let lastPositionAt = 0

/** 懒加载并初始化插件；不可用时返回 null（非 Android / 未集成插件） */
function ensurePlugin(): any {
  // #ifdef APP-PLUS
  if (plugin) return plugin
  try {
    const mod = uni.requireNativePlugin('XZH-musicNotification') as any
    if (!mod || typeof mod.init !== 'function') return null
    plugin = mod
    mod.init({ path: '/pages/home/index', icon: '' })
    if (typeof mod.createNotification === 'function') {
      mod.createNotification(() => {
        ready = true
        if (pendingTrack) pushUpdate(pendingTrack, pendingPlaying)
      })
    } else {
      ready = true
    }
  } catch {
    plugin = null
  }
  // #endif
  return plugin
}

/** 绑定锁屏/通知栏按钮事件（只绑定一次） */
function bindEvents(mod: any) {
  // #ifdef APP-PLUS
  if (eventsBound) return
  eventsBound = true
  try {
    const events = (plus as unknown as { globalEvent: { addEventListener(name: string, callback: (event: any) => void): void } }).globalEvent
    events.addEventListener('musicNotificationPause', () => handlers?.onToggle())
    events.addEventListener('musicNotificationPrevious', () => handlers?.onPrevious())
    events.addEventListener('musicNotificationNext', () => handlers?.onNext())
    events.addEventListener('musicNotificationClose', () => handlers?.onClose())
    events.addEventListener('musicSeekTo', (event: { position?: number }) => {
      const position = Number(event?.position ?? 0)
      if (position > 0) handlers?.onSeek(position)
    })
  } catch {
    /* 事件注册失败时不影响播放 */
  }
  // #endif
}

/** 推送歌曲元数据 + 播放态到通知栏 */
function pushUpdate(track: Track, isPlaying: boolean) {
  const mod = ensurePlugin()
  if (!mod) return
  if (!ready) {
    pendingTrack = track
    pendingPlaying = isPlaying
    return
  }
  try {
    mod.update({
      songName: String(track.title || ''),
      artistsName: String(track.artist || ''),
      picUrl: track.cover || '',
      // 插件要求毫秒
      duration: Math.round((track.duration || 0) * 1000),
      favour: false,
    })
    mod.playOrPause(!!isPlaying)
  } catch {
    /* 插件调用异常时静默降级 */
  }
}

/** 初始化锁屏控制，并注入播放器操作回调 */
export function initMusicNotification(next: MusicNotificationHandlers) {
  handlers = next
  const mod = ensurePlugin()
  if (mod) bindEvents(mod)
}

/** 切歌时更新通知栏歌曲信息与播放态 */
export function updateMusicNotification(track: Track | null, isPlaying: boolean) {
  if (!track) return
  pushUpdate(track, isPlaying)
}

/** 仅同步播放/暂停态（不重推歌曲信息） */
export function updateMusicNotificationState(isPlaying: boolean) {
  const mod = ensurePlugin()
  if (!mod || !ready) return
  try {
    mod.playOrPause(!!isPlaying)
  } catch {
    /* ignore */
  }
}

/** 同步通知栏进度，按 2 秒节流；positionSec 单位秒 */
export function syncMusicNotificationProgress(positionSec: number) {
  const mod = ensurePlugin()
  if (!mod || !ready || typeof mod.setPosition !== 'function') return
  const now = Date.now()
  if (now - lastPositionAt < 2000) return
  lastPositionAt = now
  try {
    mod.setPosition(Math.max(0, Math.round(positionSec * 1000)))
  } catch {
    /* ignore */
  }
}

/** 移除通知栏（停止播放时调用） */
export function cancelMusicNotification() {
  const mod = ensurePlugin()
  if (!mod || typeof mod.cancel !== 'function') return
  try {
    mod.cancel()
  } catch {
    /* ignore */
  }
}
