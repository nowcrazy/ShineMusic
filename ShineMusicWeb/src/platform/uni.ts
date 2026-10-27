/**
 * Web 平台 uni API 兼容层（仅实现本项目用到的能力）。
 *
 * 设计原则：
 * - 纯浏览器实现，不依赖任何 uni-app 运行时；
 * - 请求、存储、上传、音频、画布等能力全部对齐 uni 的调用约定（含回调形态）；
 * - 主题与 UI 反馈复用页面同一套 CSS 令牌，避免 Web 端出现割裂样式。
 */
import {
  navigateBack,
  navigateTo,
  pageStack,
  redirectTo,
  reLaunch,
  switchTab,
} from './navigation'
import {
  hideLoading,
  hideToast,
  showActionSheet,
  showLoading,
  showModal,
  showToast,
} from './ui'
import { refreshAccessToken } from '@/services/http'
import { getAccessToken } from '@/services/tokens'

const FILE_REGISTRY = new Map<string, File>()

function invoke(options: Record<string, any>, key: 'success' | 'fail' | 'complete', payload?: any) {
  const handler = options[key]
  if (typeof handler === 'function') handler(payload)
}

function storageSet(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify({ v: value }))
  } catch {
    // 隐私模式/超配额时静默降级
  }
}

function storageGet(key: string): any {
  try {
    const raw = localStorage.getItem(key)
    if (raw === null) return ''
    const parsed = JSON.parse(raw) as { v?: unknown }
    return parsed && typeof parsed === 'object' && 'v' in parsed ? parsed.v : raw
  } catch {
    return ''
  }
}

function storageRemove(key: string) {
  try {
    localStorage.removeItem(key)
  } catch {
    // ignore
  }
}

function toQuery(data: Record<string, unknown>): string {
  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(data)) {
    if (value === undefined || value === null) continue
    if (Array.isArray(value)) value.forEach((item) => params.append(key, String(item)))
    else params.append(key, String(value))
  }
  return params.toString()
}

function parseBody(text: string, contentType: string): unknown {
  if (!text) return null
  if (contentType.includes('application/json')) {
    try {
      return JSON.parse(text)
    } catch {
      return text
    }
  }
  return text
}

/** 把 uni.uploadFile 的 filePath（blob URL / File 对象 / 本地路径）解析为浏览器 File。 */
async function resolveFile(filePath: string, explicit?: unknown): Promise<File> {
  if (explicit instanceof File) return explicit
  const registered = FILE_REGISTRY.get(filePath)
  if (registered) return registered
  if (filePath.startsWith('blob:') || filePath.startsWith('data:')) {
    const response = await fetch(filePath)
    const blob = await response.blob()
    return new File([blob], 'upload.bin', { type: blob.type })
  }
  throw new Error(`无法读取本地文件：${filePath}`)
}

/** 401 时用 refresh token 续期并重放一次（上传/下载绕过 http 层时同样有效）。 */
async function withAuthRetry<T>(run: (headers: Record<string, string>) => Promise<T>, headers: Record<string, string>, status: number): Promise<T> {
  if (status !== 401) throw new Error('no-retry')
  const refreshed = await refreshAccessToken()
  if (!refreshed) throw new Error('refresh-failed')
  return run({ ...headers, Authorization: `Bearer ${getAccessToken()}` })
}

function request(options: Record<string, any>) {
  const controller = new AbortController()
  const method = String(options.method ?? 'GET').toUpperCase()
  const headers: Record<string, string> = { ...(options.header ?? {}) }
  let url = String(options.url ?? '')
  let body: BodyInit | undefined

  if (method === 'GET' || method === 'DELETE') {
    if (options.data && typeof options.data === 'object') {
      const query = toQuery(options.data as Record<string, unknown>)
      if (query) url += (url.includes('?') ? '&' : '?') + query
    }
  } else if (options.data !== undefined && options.data !== null) {
    if (typeof options.data === 'string') {
      body = options.data
      if (!headers['Content-Type'] && !headers['content-type']) headers['Content-Type'] = 'application/json'
    } else if (options.data instanceof FormData) {
      body = options.data
    } else {
      body = JSON.stringify(options.data)
      headers['Content-Type'] = headers['Content-Type'] ?? 'application/json'
    }
  }

  const timeout = window.setTimeout(() => controller.abort(), Number(options.timeout ?? 15000))

  fetch(url, { method, headers, body, signal: controller.signal })
    .then(async (response) => {
      const text = await response.text()
      const headerObject: Record<string, string> = {}
      response.headers.forEach((value, key) => {
        headerObject[key] = value
      })
      invoke(options, 'success', {
        statusCode: response.status,
        data: parseBody(text, response.headers.get('content-type') ?? ''),
        header: headerObject,
      })
    })
    .catch((error: unknown) => {
      const message = error instanceof Error ? error.message : String(error)
      invoke(options, 'fail', {
        errMsg: controller.signal.aborted ? 'request:fail timeout' : `request:fail ${message}`,
      })
    })
    .finally(() => {
      window.clearTimeout(timeout)
      invoke(options, 'complete')
    })

  return { abort: () => controller.abort() }
}

function uploadFile(options: Record<string, any>) {
  const url = String(options.url ?? '')
  const fieldName = String(options.name ?? 'file')
  const headers: Record<string, string> = { ...(options.header ?? {}) }

  const send = async (overrides: Record<string, string>): Promise<{ status: number; text: string }> => {
    const file = await resolveFile(String(options.filePath ?? ''), options.file)
    const form = new FormData()
    for (const [key, value] of Object.entries(options.formData ?? {})) {
      if (value !== undefined && value !== null) form.append(key, String(value))
    }
    form.append(fieldName, file, file.name)
    const response = await fetch(url, { method: 'POST', headers: overrides, body: form })
    return { status: response.status, text: await response.text() }
  }

  void (async () => {
    try {
      let result = await send(headers)
      if (result.status === 401) {
        try {
          result = await withAuthRetry((nextHeaders) => send(nextHeaders), headers, 401)
        } catch {
          // 刷新失败时按 401 原样返回，由调用方提示
        }
      }
      invoke(options, 'success', { statusCode: result.status, data: result.text })
    } catch (error: unknown) {
      invoke(options, 'fail', { errMsg: error instanceof Error ? error.message : String(error) })
    } finally {
      invoke(options, 'complete')
    }
  })()

  return { abort: () => undefined }
}

function downloadFile(options: Record<string, any>) {
  const controller = new AbortController()
  void (async () => {
    try {
      const response = await fetch(String(options.url ?? ''), { signal: controller.signal })
      const blob = await response.blob()
      const tempFilePath = URL.createObjectURL(blob)
      invoke(options, 'success', { statusCode: response.status, tempFilePath })
    } catch (error: unknown) {
      invoke(options, 'fail', { errMsg: error instanceof Error ? error.message : String(error) })
    } finally {
      invoke(options, 'complete')
    }
  })()
  return { abort: () => controller.abort() }
}

function pickFiles(options: Record<string, any>, mode: 'file' | 'image') {
  const input = document.createElement('input')
  input.type = 'file'
  input.multiple = Number(options.count ?? 1) > 1
  if (mode === 'image') input.accept = 'image/*'
  else if (Array.isArray(options.extension) && options.extension.length > 0) {
    input.accept = options.extension.map((ext: string) => (ext.startsWith('.') ? ext : `.${ext}`)).join(',')
  }
  input.style.display = 'none'
  document.body.appendChild(input)
  input.onchange = () => {
    const files = Array.from(input.files ?? [])
    const tempFiles = files.map((file) => {
      const path = URL.createObjectURL(file)
      FILE_REGISTRY.set(path, file)
      return { path, name: file.name, size: file.size, type: file.type }
    })
    const tempFilePaths = tempFiles.map((file) => file.path)
    if (mode === 'image') {
      invoke(options, 'success', { tempFilePaths, tempFiles })
    } else {
      invoke(options, 'success', { tempFiles, tempFilePaths })
    }
    invoke(options, 'complete')
    input.remove()
  }
  input.addEventListener('cancel', () => {
    input.remove()
    invoke(options, 'fail', { errMsg: 'chooseFile:fail cancel' })
    invoke(options, 'complete')
  })
  input.click()
}

function getImageInfo(options: Record<string, any>) {
  const image = new Image()
  image.crossOrigin = 'anonymous'
  image.onload = () => {
    invoke(options, 'success', {
      width: image.naturalWidth,
      height: image.naturalHeight,
      path: String(options.src ?? ''),
      orientation: 'up',
      type: 'unknown',
    })
    invoke(options, 'complete')
  }
  image.onerror = () => {
    invoke(options, 'fail', { errMsg: 'getImageInfo:fail' })
    invoke(options, 'complete')
  }
  image.src = String(options.src ?? '')
}

function loadImage(src: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const image = new Image()
    image.crossOrigin = 'anonymous'
    image.onload = () => resolve(image)
    image.onerror = () => reject(new Error('image load failed'))
    image.src = src
  })
}

function compressImage(options: Record<string, any>) {
  void (async () => {
    try {
      const image = await loadImage(String(options.src ?? ''))
      const targetWidth = Number(options.compressedWidth ?? image.naturalWidth)
      const targetHeight = Number(options.compressedHeight ?? Math.round((image.naturalHeight / image.naturalWidth) * targetWidth))
      const canvas = document.createElement('canvas')
      canvas.width = Math.max(1, targetWidth)
      canvas.height = Math.max(1, targetHeight)
      canvas.getContext('2d')?.drawImage(image, 0, 0, canvas.width, canvas.height)
      const quality = Math.min(1, Math.max(0.1, Number(options.quality ?? 80) / 100))
      const tempFilePath = canvas.toDataURL('image/jpeg', quality)
      invoke(options, 'success', { tempFilePath })
    } catch (error: unknown) {
      invoke(options, 'fail', { errMsg: error instanceof Error ? error.message : String(error) })
    } finally {
      invoke(options, 'complete')
    }
  })()
}

interface AudioLike {
  src: string
  play: () => void
  pause: () => void
  stop: () => void
  seek: (position: number) => void
  destroy: () => void
}

function createAudioContext(): Record<string, any> {
  const audio = new Audio()
  audio.preload = 'auto'
  const handlers: Record<string, Array<(...args: any[]) => void>> = {}
  const emit = (event: string, ...args: any[]) => handlers[event]?.forEach((fn) => fn(...args))

  audio.addEventListener('play', () => emit('Play'))
  audio.addEventListener('pause', () => emit('Pause'))
  audio.addEventListener('ended', () => emit('Ended'))
  audio.addEventListener('error', () => emit('Error', { errMsg: 'audio error' }))
  audio.addEventListener('timeupdate', () => emit('TimeUpdate'))
  audio.addEventListener('canplay', () => emit('Canplay'))
  audio.addEventListener('waiting', () => emit('Waiting'))
  audio.addEventListener('seeked', () => emit('Seeked'))
  audio.addEventListener('loadedmetadata', () => emit('Canplay'))

  const on = (event: string) => (handler: (...args: any[]) => void) => {
    ;(handlers[event] ??= []).push(handler)
  }
  const off = (event: string) => (handler?: (...args: any[]) => void) => {
    if (!handler) handlers[event] = []
    else handlers[event] = (handlers[event] ?? []).filter((fn) => fn !== handler)
  }

  return {
    get src() {
      return audio.src
    },
    set src(value: string) {
      audio.src = value
    },
    get autoplay() {
      return audio.autoplay
    },
    set autoplay(value: boolean) {
      audio.autoplay = value
    },
    get loop() {
      return audio.loop
    },
    set loop(value: boolean) {
      audio.loop = value
    },
    get volume() {
      return audio.volume
    },
    set volume(value: number) {
      audio.volume = Math.max(0, Math.min(1, value))
    },
    get playbackRate() {
      return audio.playbackRate
    },
    set playbackRate(value: number) {
      audio.playbackRate = value
    },
    get currentTime() {
      return audio.currentTime
    },
    set currentTime(value: number) {
      try {
        audio.currentTime = value
      } catch {
        // 元数据未就绪时忽略
      }
    },
    get duration() {
      return Number.isFinite(audio.duration) ? audio.duration : 0
    },
    get paused() {
      return audio.paused
    },
    play: () => {
      void audio.play().catch(() => emit('Error', { errMsg: 'play failed' }))
    },
    pause: () => audio.pause(),
    stop: () => {
      audio.pause()
      try {
        audio.currentTime = 0
      } catch {
        // ignore
      }
    },
    seek: (position: number) => {
      try {
        audio.currentTime = Math.max(0, position)
      } catch {
        // ignore
      }
    },
    destroy: () => {
      audio.pause()
      audio.removeAttribute('src')
      audio.load()
      for (const key of Object.keys(handlers)) handlers[key] = []
    },
    onPlay: on('Play'),
    onPause: on('Pause'),
    onStop: on('Pause'),
    onEnded: on('Ended'),
    onError: on('Error'),
    onTimeUpdate: on('TimeUpdate'),
    onCanplay: on('Canplay'),
    onWaiting: on('Waiting'),
    onSeeked: on('Seeked'),
    offPlay: off('Play'),
    offPause: off('Pause'),
    offEnded: off('Ended'),
    offError: off('Error'),
    offTimeUpdate: off('TimeUpdate'),
    offCanplay: off('Canplay'),
    offWaiting: off('Waiting'),
    offSeeked: off('Seeked'),
  }
}

function createBackgroundAudioManager(): Record<string, any> {
  return {
    ...createAudioContext(),
    title: '',
    singer: '',
    coverImgUrl: '',
    onPrev: () => undefined,
    onNext: () => undefined,
    offPrev: () => undefined,
    offNext: () => undefined,
  }
}

interface QueryTask {
  selector: string
  all: boolean
  rect: boolean
  callback?: (result: any) => void
  fieldsCallback?: (result: any) => void
  fieldsSpec?: Record<string, boolean>
}

function createSelectorQuery() {
  const tasks: QueryTask[] = []
  const query: Record<string, any> = {
    in: () => query,
    select(selector: string) {
      const task: QueryTask = { selector, all: false, rect: false }
      tasks.push(task)
      return {
        boundingClientRect(callback: (result: any) => void) {
          task.rect = true
          task.callback = callback
          return query
        },
        fields(spec: Record<string, boolean>, callback: (result: any) => void) {
          task.fieldsSpec = spec
          task.fieldsCallback = callback
          return query
        },
        scrollOffset(callback: (result: any) => void) {
          task.callback = callback
          return query
        },
      }
    },
    selectAll(selector: string) {
      const task: QueryTask = { selector, all: true, rect: false }
      tasks.push(task)
      return {
        boundingClientRect(callback: (result: any) => void) {
          task.rect = true
          task.callback = callback
          return query
        },
        fields(spec: Record<string, boolean>, callback: (result: any) => void) {
          task.fieldsSpec = spec
          task.fieldsCallback = callback
          return query
        },
      }
    },
    exec(callback?: (results: any[]) => void) {
      const results = tasks.map((task) => {
        if (task.all) {
          const nodes = Array.from(document.querySelectorAll(task.selector))
          if (task.rect) return nodes.map((node) => toRect(node.getBoundingClientRect()))
          return nodes.map((node) => ({ id: node.id, dataset: { ...(node as HTMLElement).dataset } }))
        }
        const node = document.querySelector(task.selector)
        if (!node) return null
        if (task.rect) {
          const rect = toRect(node.getBoundingClientRect())
          task.callback?.(rect)
          return rect
        }
        const value = { id: node.id, dataset: { ...(node as HTMLElement).dataset } }
        task.fieldsCallback?.(value)
        return value
      })
      callback?.(results)
      tasks.length = 0
    },
  }
  return query
}

function toRect(rect: DOMRect) {
  return { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom, width: rect.width, height: rect.height }
}

function createCanvasContext(canvasId: string) {
  let canvas = document.getElementById(canvasId) as HTMLCanvasElement | null
  if (!canvas) {
    canvas = document.querySelector(`[canvas-id="${canvasId}"]`) as HTMLCanvasElement | null
  }
  const context = canvas?.getContext('2d', { willReadFrequently: true }) ?? null
  const pending: Array<() => void> = []

  const proxy: Record<string, any> = {
    drawImage(...args: any[]) {
      pending.push(() => (context as any)?.drawImage(...args))
    },
    fillRect(...args: any[]) {
      pending.push(() => (context as any)?.fillRect(...args))
    },
    clearRect(...args: any[]) {
      pending.push(() => (context as any)?.clearRect(...args))
    },
    set fillStyle(value: string) {
      pending.push(() => {
        if (context) context.fillStyle = value
      })
    },
    draw(_reserve?: boolean, callback?: () => void) {
      for (const task of pending.splice(0)) {
        try {
          task()
        } catch {
          // 单条绘制失败不阻断后续
        }
      }
      callback?.()
    },
  }
  return proxy
}

function canvasGetImageData(options: Record<string, any>) {
  try {
    const canvasId = String(options.canvasId ?? '')
    const canvas =
      (document.getElementById(canvasId) as HTMLCanvasElement | null) ??
      (document.querySelector(`[canvas-id="${canvasId}"]`) as HTMLCanvasElement | null)
    const context = canvas?.getContext('2d', { willReadFrequently: true })
    if (!canvas || !context) throw new Error('canvas not found')
    const data = context.getImageData(
      Number(options.x ?? 0),
      Number(options.y ?? 0),
      Number(options.width ?? canvas.width),
      Number(options.height ?? canvas.height),
    ).data
    invoke(options, 'success', { data, width: Number(options.width ?? canvas.width), height: Number(options.height ?? canvas.height) })
  } catch (error: unknown) {
    invoke(options, 'fail', { errMsg: error instanceof Error ? error.message : String(error) })
  } finally {
    invoke(options, 'complete')
  }
}

function canvasToTempFilePath(options: Record<string, any>) {
  void (async () => {
    try {
      const canvasId = String(options.canvasId ?? '')
      const canvas =
        (document.getElementById(canvasId) as HTMLCanvasElement | null) ??
        (document.querySelector(`[canvas-id="${canvasId}"]`) as HTMLCanvasElement | null)
      if (!canvas) throw new Error('canvas not found')
      const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/webp', 0.85))
      if (!blob) throw new Error('canvas export failed')
      const file = new File([blob], 'canvas.webp', { type: 'image/webp' })
      const tempFilePath = URL.createObjectURL(file)
      FILE_REGISTRY.set(tempFilePath, file)
      invoke(options, 'success', { tempFilePath })
    } catch (error: unknown) {
      invoke(options, 'fail', { errMsg: error instanceof Error ? error.message : String(error) })
    } finally {
      invoke(options, 'complete')
    }
  })()
}

const eventBus = new Map<string, Set<(...args: any[]) => void>>()
const resizeHandlers = new Set<(res: { size: { windowWidth: number; windowHeight: number } }) => void>()
let resizeInstalled = false

function installResize() {
  if (resizeInstalled) return
  resizeInstalled = true
  window.addEventListener('resize', () => {
    const payload = { size: { windowWidth: window.innerWidth, windowHeight: window.innerHeight } }
    resizeHandlers.forEach((handler) => handler(payload))
  })
}

const innerAudio = createAudioContext()
const backgroundAudio = createBackgroundAudioManager()

export const uniShim: Record<string, any> = {
  request,
  uploadFile,
  downloadFile,
  chooseFile: (options: Record<string, any>) => pickFiles(options, 'file'),
  chooseMessageFile: (options: Record<string, any>) => pickFiles(options, 'file'),
  chooseImage: (options: Record<string, any>) => pickFiles(options, 'image'),
  getImageInfo,
  compressImage,
  createCanvasContext,
  canvasGetImageData,
  canvasToTempFilePath,
  createInnerAudioContext: () => innerAudio,
  getBackgroundAudioManager: () => backgroundAudio,
  createSelectorQuery,
  getSystemInfoSync: () => ({
    windowWidth: window.innerWidth,
    windowHeight: window.innerHeight,
    screenWidth: window.screen.width,
    screenHeight: window.screen.height,
    pixelRatio: window.devicePixelRatio || 1,
    platform: 'web',
    language: navigator.language,
    osName: navigator.platform,
  }),
  getWindowInfo: () => ({ windowWidth: window.innerWidth, windowHeight: window.innerHeight, pixelRatio: window.devicePixelRatio || 1 }),
  onWindowResize: (handler: (res: { size: { windowWidth: number; windowHeight: number } }) => void) => {
    installResize()
    resizeHandlers.add(handler)
  },
  offWindowResize: (handler?: (res: unknown) => void) => {
    if (handler) resizeHandlers.delete(handler as (res: { size: { windowWidth: number; windowHeight: number } }) => void)
    else resizeHandlers.clear()
  },
  showToast,
  hideToast,
  showLoading,
  hideLoading,
  showModal,
  showActionSheet,
  navigateTo,
  redirectTo,
  reLaunch,
  switchTab,
  navigateBack,
  setNavigationBarTitle: (options: Record<string, any>) => {
    if (options.title) document.title = String(options.title)
    invoke(options, 'success')
    invoke(options, 'complete')
  },
  setNavigationBarColor: (options: Record<string, any>) => {
    invoke(options, 'success')
    invoke(options, 'complete')
  },
  getStorageSync: storageGet,
  setStorageSync: storageSet,
  removeStorageSync: storageRemove,
  clearStorageSync: () => {
    try {
      localStorage.clear()
    } catch {
      // ignore
    }
  },
  getStorageInfoSync: () => {
    const keys = Object.keys(localStorage)
    return { keys, currentSize: JSON.stringify(localStorage).length / 1024, limitSize: 10240 }
  },
  pageScrollTo: (options: Record<string, any>) => {
    window.scrollTo({ top: Number(options.scrollTop ?? 0), behavior: 'smooth' })
  },
  stopPullDownRefresh: () => undefined,
  startPullDownRefresh: () => undefined,
  setClipboardData: (options: Record<string, any>) => {
    void navigator.clipboard
      ?.writeText(String(options.data ?? ''))
      .then(() => {
        invoke(options, 'success')
        invoke(options, 'complete')
      })
      .catch(() => {
        invoke(options, 'fail', { errMsg: 'setClipboardData:fail' })
        invoke(options, 'complete')
      })
  },
  getClipboardData: (options: Record<string, any>) => {
    void navigator.clipboard
      ?.readText()
      .then((text) => {
        invoke(options, 'success', { data: text })
        invoke(options, 'complete')
      })
      .catch(() => {
        invoke(options, 'fail', { errMsg: 'getClipboardData:fail' })
        invoke(options, 'complete')
      })
  },
  vibrateShort: (options?: Record<string, any>) => {
    if (options) {
      invoke(options, 'success')
      invoke(options, 'complete')
    }
  },
  openURL: (url: string) => window.open(url, '_blank', 'noopener,noreferrer'),
  requireNativePlugin: () => ({}),
  $emit: (event: string, ...args: any[]) => {
    eventBus.get(event)?.forEach((handler) => handler(...args))
  },
  $on: (event: string, handler: (...args: any[]) => void) => {
    if (!eventBus.has(event)) eventBus.set(event, new Set())
    eventBus.get(event)!.add(handler)
  },
  $off: (event: string, handler?: (...args: any[]) => void) => {
    if (!handler) eventBus.delete(event)
    else eventBus.get(event)?.delete(handler)
  },
  $once: (event: string, handler: (...args: any[]) => void) => {
    const wrapper = (...args: any[]) => {
      eventBus.get(event)?.delete(wrapper)
      handler(...args)
    }
    uniShim.$on(event, wrapper)
  },
  getCurrentPages: pageStack,
}

export function installUniShim() {
  ;(globalThis as unknown as { uni: Record<string, any> }).uni = uniShim
  ;(globalThis as unknown as { getCurrentPages: typeof pageStack }).getCurrentPages = pageStack
}

export { innerAudio, backgroundAudio }
