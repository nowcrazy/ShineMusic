import './platform/install'
import 'element-plus/dist/index.css'
import '@/styles/admin.scss'
import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import App from './App.vue'
import router from './router'
import { createAppI18n } from '@/locale'

createApp(App)
  .use(createPinia())
  .use(router)
  .use(createAppI18n())
  .use(ElementPlus, { locale: zhCn })
  .mount('#app')
