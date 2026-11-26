import { computed, ref, watch } from 'vue'
import { API_PREFIX, http } from './http'
import { currentUser, isAuthenticated } from './session-state'
import { useCapabilities } from './capabilities'
import type { ApiCollectionSummary, ApiPageResult } from '@/types/music'

/** Public editorial collections only; never user-owned playlists. */
export const savedCollections = ref<ApiCollectionSummary[]>([])
export const savedCollectionsTotal = ref(0)
export const savedCollectionsLoading = ref(false)
export const savedCollectionsError = ref(false)
export const savedCollectionsLoaded = ref(false)
export const savingCollectionIds = ref<Set<string>>(new Set())
export const savedCollectionIds = computed(() => new Set(savedCollections.value.map(item => item.id)))
let generation = 0
let pending: Promise<void> | null = null
const capabilities = useCapabilities()
watch(() => [currentUser.value?.id, isAuthenticated.value, capabilities.capabilities.editorialCollections], () => {
  resetSavedCollections()
}, { flush: 'sync' })

/** Clear cached collection data and invalidate any fetch/write started in the old scope. */
export function resetSavedCollections(): void {
  generation++
  pending = null
  savedCollections.value = []
  savedCollectionsTotal.value = 0
  savedCollectionsLoading.value = false
  savedCollectionsError.value = false
  savedCollectionsLoaded.value = false
  savingCollectionIds.value = new Set()
}

/** Fetch every page so detail membership is correct even beyond the first 100. */
export function loadSavedCollections(force = false): Promise<void> {
  if (!isAuthenticated.value || !capabilities.capabilities.editorialCollections) return Promise.resolve()
  if (pending) return pending
  if (savedCollectionsLoaded.value && !force) return Promise.resolve()
  const version = generation
  savedCollectionsLoading.value = true
  savedCollectionsError.value = false
  pending = (async () => {
    try {
      const items: ApiCollectionSummary[] = []
      let total = 0
      for (let page = 1; ; page++) {
        const result = await http.get<ApiPageResult<ApiCollectionSummary>>(`${API_PREFIX}/me/saved-collections?page=${page}&pageSize=100`)
        if (version !== generation) return
        total = result.total
        items.push(...result.items)
        if (!result.items.length || items.length >= total) break
      }
      savedCollections.value = items
      savedCollectionsTotal.value = total
      savedCollectionsLoaded.value = true
    } catch (error) {
      if (version === generation) savedCollectionsError.value = true
      throw error
    } finally {
      if (version === generation) { savedCollectionsLoading.value = false; pending = null }
    }
  })()
  return pending
}

/** Serialize writes; optimistic state is restored on failure, discarded on account changes. */
export async function toggleSavedCollection(collection: ApiCollectionSummary): Promise<void> {
  if (!isAuthenticated.value || !capabilities.capabilities.editorialCollections || savingCollectionIds.value.size) return
  const version = generation
  await loadSavedCollections()
  if (version !== generation || !isAuthenticated.value || savingCollectionIds.value.size) return
  const before = [...savedCollections.value]
  const totalBefore = savedCollectionsTotal.value
  const saved = savedCollectionIds.value.has(collection.id)
  savingCollectionIds.value = new Set([collection.id])
  savedCollectionsError.value = false
  savedCollections.value = saved ? before.filter(item => item.id !== collection.id) : [collection, ...before]
  savedCollectionsTotal.value += saved ? -1 : 1
  try {
    const path = `${API_PREFIX}/me/saved-collections/${encodeURIComponent(collection.id)}`
    if (saved) await http.delete(path)
    else await http.put(path)
  } catch (error) {
    if (version === generation) {
      savedCollections.value = before
      savedCollectionsTotal.value = totalBefore
      savedCollectionsError.value = true
    }
    throw error
  } finally {
    if (version === generation) savingCollectionIds.value = new Set()
  }
}
