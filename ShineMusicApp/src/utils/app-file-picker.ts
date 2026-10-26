/**
 * App 端本地文件选择器（仅 Android）。
 *
 * 背景：uni.chooseFile 官方标注「仅支持 H5」，App 端调用不会弹任何界面，
 * 因此上传页需要走原生 Intent（ACTION_GET_CONTENT / SAF 方式，无需存储权限）。
 * SAF 返回 content:// URI，uni.uploadFile 无法直接读取，
 * 这里把内容复制到应用缓存目录后返回真实文件路径。
 */

/** 选择结果，结构对齐 uni.chooseFile 的 tempFiles */
export interface PickedLocalFile {
  path: string
  name: string
  size: number
}

// #ifdef APP-PLUS
/** startActivityForResult 的请求码（与其它调用错开即可） */
const REQUEST_CODE = 0x5301

/** 从 SAF 的 content:// URI 查询原始文件名 */
function queryDisplayName(resolver: any, uri: any, fallback: string): string {
  const android = plus.android as any
  try {
    const OpenableColumns = android.importClass('android.provider.OpenableColumns')
    const cursor = android.invoke(resolver, 'query', uri, null, null, null, null)
    if (cursor) {
      try {
        if (android.invoke(cursor, 'moveToFirst')) {
          const index = android.invoke(
            cursor,
            'getColumnIndex',
            android.getAttribute(OpenableColumns, 'DISPLAY_NAME'),
          ) as number
          if (index >= 0) {
            const name = android.invoke(cursor, 'getString', index) as string | null
            if (name) return name
          }
        }
      } finally {
        android.invoke(cursor, 'close')
      }
    }
  } catch {
    /* 查询失败时用兜底名 */
  }
  return fallback
}

/** DISPLAY_NAME 查询失败时，按 content:// URI 的 MIME 类型推断扩展名（保证后续格式校验拿到正确后缀） */
function extensionFromMime(resolver: any, uri: any): string {
  const android = plus.android as any
  try {
    const mime = android.invoke(resolver, 'getType', uri) as string | null
    if (mime) {
      const MimeTypeMap = android.importClass('android.webkit.MimeTypeMap')
      const ext = android.invoke(
        android.invoke(MimeTypeMap, 'getSingleton'),
        'getExtensionFromMimeType',
        mime,
      ) as string | null
      if (ext) return ext
    }
  } catch {
    /* 推断失败 */
  }
  return 'bin'
}

/** 已知的音频扩展名集合（用于判断 DISPLAY_NAME/MIME 结果是否可信） */
const KNOWN_AUDIO_EXTS = [
  'mp3', 'flac', 'wav', 'aac', 'm4a', 'm4b', 'ogg', 'oga',
  'wma', 'aiff', 'aif', 'ape', 'mpc', 'mpp', 'wv', 'dsf',
]

/**
 * 文件头嗅探：读取缓存文件前 16 字节识别真实音频格式。
 * SAF 的 DISPLAY_NAME / getType 在部分基座上不可用，这里作为最终兜底，
 * 保证复制到缓存的文件能拿到正确扩展名（validateFile 只认扩展名）。
 */
function sniffAudioExt(path: string): string {
  const android = plus.android as any
  let input: any = null
  try {
    const FileInputStream = android.importClass('java.io.FileInputStream')
    input = new FileInputStream(path)
    const buf = android.newObject('[B', 16)
    const n = android.invoke(input, 'read', buf) as number
    if (!n || n < 4) return ''
    const b = (i: number) => buf[i] & 0xff
    const s = (i: number, len: number) => {
      let r = ''
      for (let k = 0; k < len && i + k < n; k += 1) r += String.fromCharCode(b(i + k))
      return r
    }
    if (s(0, 3) === 'ID3') return 'mp3'
    if (b(0) === 0xff && (b(1) & 0xe0) === 0xe0) {
      // 0xFFFx：MPEG 帧同步；layer 位(2-1)为 0 是 AAC ADTS，否则按 mp3
      return (b(1) & 0x06) === 0 ? 'aac' : 'mp3'
    }
    if (s(0, 4) === 'fLaC') return 'flac'
    if (s(0, 4) === 'OggS') return 'ogg'
    if (s(0, 4) === 'RIFF' && s(8, 4) === 'WAVE') return 'wav'
    if (s(0, 4) === 'FORM' && (s(8, 4) === 'AIFF' || s(8, 4) === 'AIFC')) return 'aiff'
    if (s(4, 4) === 'ftyp') return 'm4a'
    if (s(0, 3) === 'MAC') return 'ape'
    if (s(0, 3) === 'MP+') return 'mpc'
    if (s(0, 4) === 'wvpk') return 'wv'
    if (s(0, 4) === 'DSD ') return 'dsf'
    if (b(0) === 0x30 && b(1) === 0x26 && b(2) === 0xb2 && b(3) === 0x75) return 'wma'
    return ''
  } catch {
    return ''
  } finally {
    try {
      if (input) android.invoke(input, 'close')
    } catch {
      /* ignore */
    }
  }
}

/** 把 content:// URI 的字节流复制到应用缓存目录，返回本地绝对路径 */
function copyUriToCache(resolver: any, uri: any, displayName: string): string {
  const android = plus.android as any
  const main = android.runtimeMainActivity()
  const cacheDir = android.invoke(main, 'getExternalCacheDir') ?? android.invoke(main, 'getCacheDir')
  if (!cacheDir) throw new Error('cache dir unavailable')
  const dir = android.invoke(cacheDir, 'getAbsolutePath') as string
  const safeName = displayName.replace(/[\\/:*?"<>|]/g, '_')
  const dest = `${dir}/sm-upload-${Date.now()}-${Math.random().toString(36).slice(2, 8)}-${safeName}`

  const input = android.invoke(resolver, 'openInputStream', uri)
  if (!input) throw new Error('openInputStream failed')
  const FileOutputStream = android.importClass('java.io.FileOutputStream')
  const output = new FileOutputStream(dest)
  try {
    let copied = false
    // Android 10+ 的公开 API：android.os.FileUtils.copy(InputStream, OutputStream)
    try {
      const FileUtils = android.importClass('android.os.FileUtils')
      if (FileUtils) {
        android.invoke(FileUtils, 'copy', input, output)
        copied = true
      }
    } catch {
      /* 低版本系统不可用，走下面的分块复制 */
    }
    if (!copied) {
      const ByteArrayOutputStream = android.importClass('java.io.ByteArrayOutputStream')
      const sink = new ByteArrayOutputStream()
      const buffer = android.newObject('[B', 65536)
      let read = 0
      while ((read = android.invoke(input, 'read', buffer) as number) > 0) {
        android.invoke(sink, 'write', buffer, 0, read)
      }
      android.invoke(output, 'write', android.invoke(sink, 'toByteArray'))
    }
  } finally {
    try {
      android.invoke(input, 'close')
    } catch {
      /* ignore */
    }
    try {
      android.invoke(output, 'close')
    } catch {
      /* ignore */
    }
  }
  return dest
}

/** 读取本地文件大小（字节） */
function localFileSize(path: string): number {
  const android = plus.android as any
  try {
    const File = android.importClass('java.io.File')
    const file = new File(path)
    return (android.invoke(file, 'length') as number) ?? 0
  } catch {
    return 0
  }
}

/** 打开系统文件选择器，返回复制到缓存目录后的本地文件列表 */
function pickViaAndroid(): Promise<PickedLocalFile[]> {
  return new Promise((resolve, reject) => {
    try {
      const android = plus.android as any
      const main = android.runtimeMainActivity()
      const Intent = android.importClass('android.content.Intent')
      const intent = new Intent(Intent.ACTION_GET_CONTENT)
      intent.setType('*/*')
      intent.addCategory(Intent.CATEGORY_OPENABLE)
      intent.putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)

      // 5+App 常用做法：覆盖 Activity 的 onActivityResult 接收选择结果
      main.onActivityResult = (
        requestCode: number,
        resultCode: number,
        data: any,
      ) => {
        if (requestCode !== REQUEST_CODE) return
        // 一次性回调，处理完立即解除，避免拦截后续其它原生结果
        main.onActivityResult = null
        if (resultCode !== -1 /* RESULT_OK */ || !data) {
          resolve([])
          return
        }
        try {
          const resolver = android.invoke(main, 'getContentResolver')
          const uris: any[] = []
          const clipData = android.invoke(data, 'getClipData')
          if (clipData) {
            const count = android.invoke(clipData, 'getItemCount') as number
            for (let i = 0; i < count; i += 1) {
              const item = android.invoke(clipData, 'getItemAt', i)
              uris.push(android.invoke(item, 'getUri'))
            }
          } else {
            const single = android.invoke(data, 'getData')
            if (single) uris.push(single)
          }
          // onActivityResult 由框架在 Activity 线程同步触发，此上下文中 SAF 的
          // ContentResolver 桥调用会静默失效（query/getType/openInputStream 均空操作）。
          // 回到 JS 事件循环后再做查询与复制。
          setTimeout(() => {
            try {
              const picked: PickedLocalFile[] = uris.map((uri, index) => {
                const fallback = `picked-${Date.now()}-${index}.${extensionFromMime(resolver, uri)}`
                const queried = queryDisplayName(resolver, uri, '')
                let path = copyUriToCache(resolver, uri, queried || fallback)
                // DISPLAY_NAME / MIME 都不可靠时，用文件头嗅探修正扩展名，保证 validateFile 识别
                let name = queried || fallback
                const nameExt = name.split('.').pop()?.toLowerCase() ?? ''
                if (!KNOWN_AUDIO_EXTS.includes(nameExt)) {
                  const sniffed = sniffAudioExt(path)
                  if (sniffed) {
                    name = `${name.replace(/\.[^.]*$/, '')}.${sniffed}`
                    // 同步重命名缓存文件：uni.uploadFile 的 multipart filename 取自 uri 的 basename
                    try {
                      const File = android.importClass('java.io.File')
                      const newPath = `${path.replace(/\.[^.]*$/, '')}.${sniffed}`
                      if (android.invoke(new File(path), 'renameTo', new File(newPath))) path = newPath
                    } catch {
                      /* 重命名失败则沿用原路径 */
                    }
                  }
                }
                return { path, name, size: localFileSize(path) }
              })
              resolve(picked)
            } catch (error) {
              reject(error instanceof Error ? error : new Error(String(error)))
            }
          }, 50)
        } catch (error) {
          reject(error instanceof Error ? error : new Error(String(error)))
        }
      }

      main.startActivityForResult(intent, REQUEST_CODE)
    } catch (error) {
      reject(error instanceof Error ? error : new Error(String(error)))
    }
  })
}
// #endif

/** 选择本地文件（仅 App 端可用；H5 走 uni.chooseFile） */
export function pickLocalFiles(): Promise<PickedLocalFile[]> {
  // #ifdef APP-PLUS
  return pickViaAndroid()
  // #endif
  // #ifndef APP-PLUS
  return Promise.reject(new Error('pickLocalFiles is app-only'))
  // #endif
}
