import { API_PREFIX, ApiError, assertSessionRevisionCurrent, captureSessionRevision, fetchWithSession, resolveUrl } from './http'
import { isOriginalAudioContentType, originalAudioFileName, saveOriginalAudioBlob } from './original-audio-file'

/** Download only through the authenticated file boundary, never via a token in the URL. */
export async function downloadAdminOriginalAudio(trackId: string, title: string): Promise<void> {
  const revision = captureSessionRevision()
  const response = await fetchWithSession(
    resolveUrl(`${API_PREFIX}/admin/tracks/${encodeURIComponent(trackId)}/original`),
    { redirect: 'error' }, revision,
  )
  if (response.status !== 200) {
    if (response.status === 404) throw new ApiError(404, 'original_unavailable', '没有可导出的已确认原件，历史资产可能尚未完成原件确认')
    const body = await response.json().catch(() => null)
    assertSessionRevisionCurrent(revision)
    throw new ApiError(response.status, body?.error?.code ?? 'original_download_failed', body?.error?.message ?? `原件下载失败（HTTP ${response.status}）`)
  }
  if (!isOriginalAudioContentType(response.headers.get('Content-Type')))
    throw new ApiError(200, 'invalid_original_response', '服务器未返回原始音频文件')
  const blob = await response.blob()
  assertSessionRevisionCurrent(revision)
  if (!blob.size) throw new ApiError(200, 'invalid_original_response', '服务器返回的原件为空')
  const name = originalAudioFileName(response.headers.get('Content-Disposition'), title, response.headers.get('Content-Type'))
  saveOriginalAudioBlob(blob, name)
}
