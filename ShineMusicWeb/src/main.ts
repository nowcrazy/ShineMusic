import './platform/install'
import 'element-plus/dist/index.css'
import '@/styles/admin.scss'
import { createApp, watch } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import { ElMessage } from 'element-plus'
import App from './App.vue'
import router from './router'
import { createAppI18n } from '@/locale'
import { expireInactiveSession, isAdmin, isAuthenticated, sessionStatus, startSessionRecovery } from '@/services/session'
import { IDLE_TIMEOUT_MS, clearActivity, startIdleWatcher } from '@/services/idle-session'

createApp(App)
  .use(createPinia())
  .use(router)
  .use(createAppI18n())
  .use(ElementPlus, { locale: zhCn })
  .mount('#app')

startSessionRecovery()
watch([sessionStatus, () => router.currentRoute.value.fullPath], () => {
  const current = router.currentRoute.value
  if (!isAuthenticated.value && current.meta.public !== true && current.name) {
    void router.replace({ name: 'login', query: { redirect: current.fullPath } })
  } else if (isAuthenticated.value && isAdmin.value && current.name === 'login') {
    const redirect = current.query.redirect
    void router.replace(typeof redirect === 'string' && redirect.startsWith('/') && !redirect.startsWith('//') ? redirect : '/dashboard')
  }
})

// Web 端空闲超时：长时间无操作自动清除令牌并回到登录页（仅 Web，不影响 App）。
startIdleWatcher(() => {
  if (sessionStatus.value === 'anonymous' || sessionStatus.value === 'unknown') {
    // 未登录时无会话可清理，仅丢弃旧活动记录，登录后重新计时。
    clearActivity()
    return
  }
  expireInactiveSession()
  ElMessage.warning(`已 ${Math.round(IDLE_TIMEOUT_MS / 60000)} 分钟无操作，请重新登录`)
  const current = router.currentRoute.value
  if (current.name !== 'login') {
    void router.replace({ name: 'login', query: { redirect: current.fullPath } })
  }
})
