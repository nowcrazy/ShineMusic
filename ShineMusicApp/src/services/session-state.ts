/** Authentication views shared by cache services without importing session orchestration. */
import { computed, reactive } from 'vue'
import type { ApiMe } from '@/types/music'

export const sessionState = reactive<{
  user: ApiMe | null
  status: 'unknown' | 'anonymous' | 'authenticated' | 'unavailable'
}>({ user: null, status: 'unknown' })

export const currentUser = computed(() => sessionState.user)
export const isAuthenticated = computed(() => sessionState.status === 'authenticated')
export const isAdmin = computed(() => isAuthenticated.value && (sessionState.user?.roles.includes('admin') ?? false))
export const sessionStatus = computed(() => sessionState.status)
