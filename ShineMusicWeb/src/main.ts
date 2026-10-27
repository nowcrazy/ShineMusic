import './platform/install'
import 'element-plus/dist/index.css'
import '@/styles/admin.scss'
import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import { ElMessage } from 'element-plus'
import App from './App.vue'
import router from './router'
import { createAppI18n } from '@/locale'
import { expireInactiveSession, isAuthenticated } from '@/services/session'
import { IDLE_TIMEOUT_MS, clearActivity, startIdleWatcher } from '@/services/idle-session'

createApp(App)
  .use(createPinia())
  .use(router)
  .use(createAppI18n())
  .use(ElementPlus, { locale: zhCn })
  .mount('#app')

// Web 端空闲超时：长时间无操作自动清除令牌并回到登录页（仅 Web，不影响 App）。
startIdleWatcher(() => {
  if (!isAuthenticated.value) {
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
