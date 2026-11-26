import type { ApiLibraryWorkflowPage, ApiLibraryWorkflowReview, ApiLibraryWorkflowTrack } from '@/types/api.generated'
import { API_PREFIX, http } from './http'

function pageQuery(status: string | undefined, page: number, pageSize: number) {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  if (status) params.set('status', status)
  return params.toString()
}

export function getPrivateLibraryReviews(status = 'pending', page = 1, pageSize = 20) {
  return http.get<ApiLibraryWorkflowPage<ApiLibraryWorkflowReview>>(
    `${API_PREFIX}/me/library/reviews?${pageQuery(status, page, pageSize)}`,
  )
}

export function resolvePrivateLibraryReview(id: string, decision: 'replace' | 'separate' | 'reject', reason?: string) {
  return http.post<void>(`${API_PREFIX}/me/library/reviews/${encodeURIComponent(id)}/resolve`, { decision, reason })
}

export function getLibraryRecycleBin(page = 1, pageSize = 20) {
  return http.get<ApiLibraryWorkflowPage<ApiLibraryWorkflowTrack>>(
    `${API_PREFIX}/me/library/recycle-bin?${pageQuery(undefined, page, pageSize)}`,
  )
}

export function restoreLibraryTrack(trackId: string) {
  return http.post<void>(`${API_PREFIX}/me/library/${encodeURIComponent(trackId)}/restore`)
}
