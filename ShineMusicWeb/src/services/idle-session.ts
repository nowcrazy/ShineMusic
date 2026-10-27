/**
 * Web 端空闲会话超时（仅 Web，不影响 App 端会话策略）。
 *
 * 场景：后台长时间不操作后仍保持登录不合理——超过 IDLE_TIMEOUT_MS 无任何交互，
 * 自动清除本地令牌并跳转登录页，避免公用电脑上长期持有可用会话。
 *
 * 实现要点：
 * - 记录上次交互时间到本地存储，键 shine.auth.lastActivityAt；
 * - 用户活动（指针/键盘/滚轮/触摸）按 TOUCH_THROTTLE_MS 节流写回，避免频繁写存储；
 * - 已超时后不再续期：用户回到页面的第一个动作不会把过期会话“救活”，交给检查器登出；
 * - 30s 轮询 + 标签页重新可见时检查，覆盖后台标签页定时器被浏览器节流的场景。
 */
const ACTIVITY_KEY = 'shine.auth.lastActivityAt'

/** 空闲超时阈值：30 分钟无操作即自动登出。 */
export const IDLE_TIMEOUT_MS = 30 * 60 * 1000

/** 活动时间写回节流：15s 内多次交互只写一次存储。 */
const TOUCH_THROTTLE_MS = 15 * 1000

/** 轮询检查间隔。 */
const CHECK_INTERVAL_MS = 30 * 1000

const ACTIVITY_EVENTS = ['pointerdown', 'keydown', 'wheel', 'mousemove', 'touchstart'] as const

let lastTouchAt = 0
let stopWatcher: (() => void) | null = null

/** 读取上次交互时间；无记录（旧版本升级/首次登录）返回 null。 */
function readActivity(): number | null {
  const raw = uni.getStorageSync(ACTIVITY_KEY)
  const value = Number(raw)
  return Number.isFinite(value) && value > 0 ? value : null
}

/** 是否已超过空闲阈值（无活动记录视为未超时）。 */
export function hasIdleExpired(): boolean {
  const last = readActivity()
  return last !== null && Date.now() - last > IDLE_TIMEOUT_MS
}

/**
 * 记录一次用户活动（节流写回）。
 * 已超时则直接忽略：避免用户回到页面时的第一个事件把过期会话续上。
 */
export function markActivity() {
  const now = Date.now()
  const last = readActivity()
  if (last !== null && now - last > IDLE_TIMEOUT_MS) return
  if (now - lastTouchAt < TOUCH_THROTTLE_MS) return
  lastTouchAt = now
  uni.setStorageSync(ACTIVITY_KEY, now)
}

/** 清除活动记录（登出/会话失效时调用）。 */
export function clearActivity() {
  lastTouchAt = 0
  uni.removeStorageSync(ACTIVITY_KEY)
}

/**
 * 启动空闲监听：监听用户交互，超时后回调一次并继续监听（登出后重新登录仍受保护）。
 * @param onExpire 判定超时后的处理（调用方负责清理会话与跳转登录页）
 * @returns 停止监听的函数（热更新/卸载时可调用）
 */
export function startIdleWatcher(onExpire: () => void): () => void {
  if (stopWatcher || typeof window === 'undefined') return () => {}
  markActivity()
  const onActivity = () => markActivity()
  for (const name of ACTIVITY_EVENTS) window.addEventListener(name, onActivity, { passive: true })

  const check = () => {
    if (!hasIdleExpired()) return
    // 处理方会清除活动记录，下一轮不再触发；监听保持运行以覆盖重新登录的场景。
    onExpire()
  }
  const onVisibilityChange = () => {
    if (document.visibilityState === 'visible') check()
  }
  document.addEventListener('visibilitychange', onVisibilityChange)
  const timer = window.setInterval(check, CHECK_INTERVAL_MS)

  function stop() {
    window.clearInterval(timer)
    for (const name of ACTIVITY_EVENTS) window.removeEventListener(name, onActivity)
    document.removeEventListener('visibilitychange', onVisibilityChange)
    stopWatcher = null
  }

  stopWatcher = stop
  return stop
}
