/**
 * Web 前端应用根组件。
 * 启动流程与 App 端保持一致：先应用主题（避免首屏闪烁），再恢复会话与能力开关。
 */
<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue'
import '@/styles/global.scss'
import { initTheme } from '@/services/theme'
import { isAuthenticated, restoreSession } from '@/services/session'
import { loadCapabilities } from '@/services/capabilities'
import { loadFavorites } from '@/services/library'
import { loadFeedback } from '@/services/feedback'
import { flushPlayerState, syncFromBackgroundAudio } from '@/services/player'

function onVisibilityChange() {
  if (document.visibilityState === 'visible') syncFromBackgroundAudio()
}

onMounted(() => {
  initTheme()
  document.addEventListener('visibilitychange', onVisibilityChange)
  void (async () => {
    await restoreSession()
    if (isAuthenticated.value) {
      loadFavorites().catch(() => undefined)
      loadFeedback().catch(() => undefined)
    }
    loadCapabilities()
  })()
})

onUnmounted(() => {
  document.removeEventListener('visibilitychange', onVisibilityChange)
  window.removeEventListener('beforeunload', flushPlayerState)
})

window.addEventListener('beforeunload', flushPlayerState)
</script>

<template>
  <router-view />
</template>
