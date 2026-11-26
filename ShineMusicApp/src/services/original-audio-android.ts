import { t } from '@/locale'
import { getAccessToken } from './tokens'
import { ApiError, assertApiContextCurrent, assertApiRequestOrigin, refreshAccessToken, trackApiRequest, type ApiRequestContext } from './http'
import { isOriginalAudioContentType, originalAudioFileName } from './original-audio-file'

let nextSaveRequestCode = 0x5302
let exporting = false

function failed(): ApiError { return new ApiError(0, 'original_download_failed', t('homePage.originalDownloadFailed')) }
function cancelled(): ApiError { return new ApiError(0, 'original_download_cancelled', '') }

function removeTemporary(path: string): Promise<void> {
  return new Promise(resolve => {
    try {
      plus.io.resolveLocalFileSystemURL(path, entry => {
        try { entry.remove(() => resolve(), () => resolve()) } catch { resolve() }
      }, () => resolve())
    } catch { resolve() }
  })
}

function abortDownload(task?: PlusDownloaderDownload): void { try { task?.abort() } catch { /* cleanup must continue */ } }

/** Download into app-private storage. The bearer token is never placed in the URL or SAF intent. */
async function downloadTemporary(context: ApiRequestContext, target: string): Promise<{ task: PlusDownloaderDownload; path: string; size: number; status: number }> {
  assertApiRequestOrigin(context, target)
  const path = `_doc/original-exports/${Date.now()}-${Math.random().toString(36).slice(2)}.tmp`
  let task: PlusDownloaderDownload | undefined
  let unregister = () => undefined as void
  try {
    const result = await new Promise<{ task: PlusDownloaderDownload; status: number }>((resolve, reject) => {
      let settled = false
      const finish = (error?: unknown, completed?: PlusDownloaderDownload, status = 0) => {
        if (settled) return
        settled = true
        if (error) reject(error)
        else if (completed) resolve({ task: completed, status })
      }
      task = plus.downloader.createDownload(target, { filename: path, timeout: 120, retry: 0 }, (completed, status) => finish(undefined, completed, status))
      const token = getAccessToken()
      if (token) task.setRequestHeader('Authorization', `Bearer ${token}`)
      unregister = trackApiRequest(context, () => {
        finish(new ApiError(0, 'api_context_changed', t('services.apiContextChanged')))
        abortDownload(task)
      })
      task.start()
    })
    assertApiContextCurrent(context)
    // Downloader may rewrite a filename on collision. Accept only the exact app-private file
    // allocated for this request; neither a response filename nor another task's file is trusted.
    const android = plus.android as any
    const File = android.importClass('java.io.File')
    const absolutePath = plus.io.convertLocalFileSystemURL(path)
    if (result.status === 200) {
      if (!result.task.filename) throw failed()
      const allocated = String(android.invoke(new File(absolutePath), 'getCanonicalPath'))
      const actual = String(android.invoke(new File(plus.io.convertLocalFileSystemURL(result.task.filename)), 'getCanonicalPath'))
      if (actual !== allocated) throw failed()
    }
    const file = new File(absolutePath)
    const size = Number(android.invoke(file, 'length'))
    if (result.status === 200 && (!Number.isSafeInteger(size) || size <= 0)) throw failed()
    return { ...result, path, size }
  } catch (error) {
    abortDownload(task)
    await removeTemporary(path)
    throw error
  } finally {
    unregister()
  }
}

/** Create a user-selected document, then copy bytes without loading the whole song into JS memory. */
function saveDocument(context: ApiRequestContext, path: string, size: number, name: string, mime: string): Promise<void> {
  return new Promise((resolve, reject) => {
    // A late result from a cancelled picker must never satisfy a subsequent export.
    const saveRequestCode = nextSaveRequestCode++
    if (nextSaveRequestCode > 0x6fff) nextSaveRequestCode = 0x5302
    const android = plus.android as any
    const main = android.runtimeMainActivity()
    const previous = main.onActivityResult
    let unregister = () => undefined as void
    let settled = false
    let invalidated = false
    const restore = () => { if (main.onActivityResult === handler) main.onActivityResult = previous ?? null }
    const finish = (error?: unknown) => {
      if (settled) return
      settled = true
      unregister()
      if (error) reject(error)
      else resolve()
    }
    const handler = (requestCode: number, resultCode: number, data: any) => {
      if (requestCode !== saveRequestCode) { previous?.call(main, requestCode, resultCode, data); return }
      restore()
      if (resultCode !== -1 || !data) { finish(cancelled()); return }
      // SAF bridge operations must run back on the JS event loop, outside the Activity callback.
      setTimeout(async () => {
        let uri: any
        let resolver: any
        let input: any
        let output: any
        let complete = false
        try {
          uri = android.invoke(data, 'getData')
          resolver = android.invoke(main, 'getContentResolver')
          if (!uri || !resolver) throw failed()
          if (invalidated) throw new ApiError(0, 'api_context_changed', t('services.apiContextChanged'))
          assertApiContextCurrent(context)
          const FileInputStream = android.importClass('java.io.FileInputStream')
          input = new FileInputStream(plus.io.convertLocalFileSystemURL(path))
          output = android.invoke(resolver, 'openOutputStream', uri, 'wt')
          if (!input || !output) throw failed()
          // Java array classes have no constructor. Allocate byte[] through the public
          // reflection API instead of depending on NativeJS newObject('[B', ...) behavior.
          const ReflectArray = android.importClass('java.lang.reflect.Array')
          const Byte = android.importClass('java.lang.Byte')
          const buffer = android.invoke(ReflectArray, 'newInstance', android.getAttribute(Byte, 'TYPE'), 65536)
          if (!buffer) throw failed()
          let count: number
          let copied = 0
          while ((count = Number(android.invoke(input, 'read', buffer))) > 0) {
            assertApiContextCurrent(context)
            android.invoke(output, 'write', buffer, 0, count)
            copied += count
            // Yield after each MiB so logout/server changes can interrupt a long SAF copy.
            if (copied % (1024 * 1024) < count) await new Promise<void>(resume => setTimeout(resume, 0))
          }
          if (copied !== size) throw failed()
          android.invoke(output, 'flush')
          android.invoke(output, 'close'); output = null
          android.invoke(input, 'close'); input = null
          assertApiContextCurrent(context)
          complete = true
          finish()
        } catch (error) { finish(error) }
        finally {
          try { if (input) android.invoke(input, 'close') } catch { /* best effort */ }
          try { if (output) android.invoke(output, 'close') } catch { /* best effort */ }
          if (!complete && uri && resolver) {
            try {
              const DocumentsContract = android.importClass('android.provider.DocumentsContract')
              android.invoke(DocumentsContract, 'deleteDocument', resolver, uri)
            } catch { /* Some document providers do not support deleting an incomplete export. */ }
          }
        }
      }, 50)
    }
    try {
      assertApiContextCurrent(context)
      const Intent = android.importClass('android.content.Intent')
      const intent = new Intent(Intent.ACTION_CREATE_DOCUMENT)
      intent.addCategory(Intent.CATEGORY_OPENABLE)
      intent.setType(mime)
      intent.putExtra(Intent.EXTRA_TITLE, name)
      main.onActivityResult = handler
      unregister = trackApiRequest(context, () => {
        invalidated = true
        restore()
        try { android.invoke(main, 'finishActivity', saveRequestCode) } catch { /* context guard still prevents copying */ }
        finish(new ApiError(0, 'api_context_changed', t('services.apiContextChanged')))
      })
      main.startActivityForResult(intent, saveRequestCode)
    } catch (error) { restore(); finish(error) }
  })
}

export async function downloadAndroidOriginalAudio(context: ApiRequestContext, target: string, title: string): Promise<void> {
  if (exporting) throw new ApiError(0, 'original_download_busy', t('homePage.originalDownloadBusy'))
  exporting = true
  let downloaded: Awaited<ReturnType<typeof downloadTemporary>> | undefined
  try {
    downloaded = await downloadTemporary(context, target)
    if (downloaded.status === 401 && await refreshAccessToken()) {
      assertApiContextCurrent(context)
      await removeTemporary(downloaded.path)
      abortDownload(downloaded.task)
      downloaded = await downloadTemporary(context, target)
    }
    assertApiContextCurrent(context)
    if (downloaded.status !== 200) {
      throw new ApiError(downloaded.status, downloaded.status === 404 ? 'original_unavailable' : 'original_download_failed',
        t(downloaded.status === 404 ? 'homePage.originalUnavailable' : 'homePage.originalDownloadFailed'))
    }
    const mime = downloaded.task.getResponseHeader('Content-Type').split(';')[0].trim().toLowerCase()
    if (!isOriginalAudioContentType(mime)) throw failed()
    const length = downloaded.task.getResponseHeader('Content-Length')
    if (length && (!/^\d+$/.test(length.trim()) || Number(length) !== downloaded.size)) throw failed()
    const name = originalAudioFileName(downloaded.task.getResponseHeader('Content-Disposition'), title, mime)
    await saveDocument(context, downloaded.path, downloaded.size, name, mime)
  } finally {
    if (downloaded) {
      abortDownload(downloaded.task)
      await removeTemporary(downloaded.path)
    }
    exporting = false
  }
}
