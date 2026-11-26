import type { ApiLibraryWorkflowPage, ApiLibraryWorkflowReview, ApiLibraryWorkflowSettings } from '@/types/api.generated'
import { API_PREFIX, ApiError, assertSessionRevisionCurrent, captureSessionRevision, fetchWithSession, http, resolveUrl } from './http'

function pageQuery(status: string | undefined, page: number, pageSize: number) {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  if (status) params.set('status', status)
  return params.toString()
}

export function getAdminLibraryCandidates(status = 'pending', page = 1, pageSize = 20) {
  return http.get<ApiLibraryWorkflowPage<ApiLibraryWorkflowReview>>(
    `${API_PREFIX}/admin/library-candidates?${pageQuery(status, page, pageSize)}`,
  )
}

export function getAdminLibraryCandidateSettings() {
  return http.get<ApiLibraryWorkflowSettings>(`${API_PREFIX}/admin/library-candidates/settings`)
}

export function getAdminLibraryCandidate(id: string) {
  return http.get<ApiLibraryWorkflowReview>(`${API_PREFIX}/admin/library-candidates/${encodeURIComponent(id)}`)
}

export function resolveAdminLibraryCandidate(id: string, decision: 'approve' | 'separate' | 'reject', reason?: string) {
  return http.post<void>(`${API_PREFIX}/admin/library-candidates/${encodeURIComponent(id)}/resolve`, { decision, reason })
}

/** <audio> cannot set Authorization, so fetch and return an object URL instead of putting a token in the URL. */
export async function fetchAdminLibraryCandidateAudio(id: string): Promise<Blob> {
  const revision = captureSessionRevision()
  const url = resolveUrl(`${API_PREFIX}/admin/library-candidates/${encodeURIComponent(id)}/stream?format=aac`)
  const response = await fetchWithSession(url, {}, revision)
  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new ApiError(response.status, body?.error?.code || 'internal_error', body?.error?.message ?? `试听加载失败（HTTP ${response.status}）`, body?.error?.traceId)
  }
  const blob = await response.blob()
  assertSessionRevisionCurrent(revision)
  return blob
}
