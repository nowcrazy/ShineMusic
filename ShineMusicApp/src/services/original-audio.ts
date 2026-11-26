import { t } from '@/locale'
import { getAccessToken } from './tokens'
import { API_PREFIX, ApiError, assertApiContextCurrent, assertApiRequestOrigin, captureApiContext, refreshAccessToken, resolveUrl, trackApiRequest } from './http'
import { isOriginalAudioContentType, originalAudioFileName, saveOriginalAudioBlob } from './original-audio-file'
// #ifdef APP-PLUS
import { downloadAndroidOriginalAudio } from './original-audio-android'
// #endif

/** Save a private original without placing its access token in a URL or opening an unauthenticated tab. */
export async function downloadMyOriginalAudio(trackId: string, title: string): Promise<void> {
  // #ifdef H5
  if (typeof document !== 'undefined' && typeof URL.createObjectURL === 'function') {
    const context = captureApiContext()
    const controller = new AbortController()
    const unregister = trackApiRequest(context, () => controller.abort())
    try {
      const target = resolveUrl(`${API_PREFIX}/me/library/${encodeURIComponent(trackId)}/original`)
      const send = () => {
        assertApiRequestOrigin(context, target)
        const token = getAccessToken()
        return fetch(target, {
          headers: token ? { Authorization: `Bearer ${token}` } : {},
          signal: controller.signal,
          redirect: 'error',
        })
      }
      let response = await send()
      assertApiContextCurrent(context)
      if (response.status === 401 && await refreshAccessToken()) {
        assertApiContextCurrent(context)
        response = await send()
        assertApiContextCurrent(context)
      }
      if (response.status !== 200) {
        if (response.status === 404) throw new ApiError(404, 'original_unavailable', t('homePage.originalUnavailable'))
        if (response.status === 401) throw new ApiError(401, 'authentication_required', t('errors.authenticationRequired'))
        const body = await response.json().catch(() => null)
        assertApiContextCurrent(context)
        throw new ApiError(response.status, body?.error?.code ?? 'original_download_failed', body?.error?.message ?? t('homePage.originalDownloadFailed'))
      }
      if (!isOriginalAudioContentType(response.headers.get('Content-Type')))
        throw new ApiError(200, 'invalid_original_response', t('homePage.originalDownloadFailed'))
      const blob = await response.blob()
      assertApiContextCurrent(context)
      if (!blob.size) throw new ApiError(200, 'invalid_original_response', t('homePage.originalDownloadFailed'))
      const name = originalAudioFileName(response.headers.get('Content-Disposition'), title, response.headers.get('Content-Type'))
      saveOriginalAudioBlob(blob, name)
      return
    } catch (error) {
      assertApiContextCurrent(context)
      throw error
    } finally {
      unregister()
    }
  }
  // #endif
  // #ifdef APP-PLUS
  if (typeof plus !== 'undefined' && plus.os.name === 'Android') {
    const context = captureApiContext()
    const target = resolveUrl(`${API_PREFIX}/me/library/${encodeURIComponent(trackId)}/original`)
    return downloadAndroidOriginalAudio(context, target, title)
  }
  // #endif
  throw new ApiError(0, 'original_download_unsupported', t('homePage.originalDownloadBrowserOnly'))
}
